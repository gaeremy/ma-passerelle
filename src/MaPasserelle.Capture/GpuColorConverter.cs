// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MaPasserelle.Capture;

/// <summary>
/// GPU color-convert + scale using the Direct3D11 Video Processor: BGRA desktop frame →
/// NV12 at the negotiated encode resolution, entirely on the GPU (the heaviest per-frame
/// operation), then a single staging read-back to the CPU NV12 buffer the encoder wants.
/// The video processor is vendor-independent — Intel, AMD, NVIDIA, Qualcomm and MediaTek
/// GPUs all implement it — so this path works on any Windows device.
/// Ma Passerelle : espaces de couleur explicites (RGB plein → YCbCr BT.709 studio, ce que
/// l'encodeur annonce) et auto-test au démarrage. Certains pilotes (machine virtuelle VMware,
/// pilote d'affichage de base) produisent une chrominance fausse, image verte et rose : la
/// conversion se fait alors sur le processeur, plus lente mais juste.
/// </summary>
internal sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private readonly ID3D11Texture2D _outputNv12;
    private readonly ID3D11Texture2D _stagingNv12;
    private readonly int _dstW;
    private readonly int _dstH;
    private readonly int _srcW, _srcH;
    private readonly int _boxX, _boxY, _boxW, _boxH;
    /// <summary>Conversion sur le processeur (le processeur vidéo du pilote a échoué à l'auto-test).</summary>
    private readonly ID3D11Texture2D? _stagingBgra;
    private readonly int[]? _columnMap;

    /// <summary>Vrai si la conversion se fait sur le processeur (pilote vidéo défaillant).</summary>
    public bool UsesCpu => _stagingBgra is not null;

    /// <summary>The BGRA input texture the capture writes into (desktop-sized).</summary>
    public ID3D11Texture2D InputTexture { get; }

    public GpuColorConverter(ID3D11Device device, int srcW, int srcH, int dstW, int dstH, int fps)
    {
        _context = device.ImmediateContext;
        _dstW = dstW;
        _dstH = dstH;
        _srcW = srcW;
        _srcH = srcH;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1u),
            InputWidth = (uint)srcW,
            InputHeight = (uint)srcH,
            OutputFrameRate = new Rational((uint)fps, 1u),
            OutputWidth = (uint)dstW,
            OutputHeight = (uint)dstH,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        InputTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)srcW,
            Height = (uint)srcH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _outputNv12 = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _stagingNv12 = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _outputView = _videoDevice.CreateVideoProcessorOutputView(_outputNv12, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        // Studio-range BT.709 in, studio-range NV12 out (standard SDR mirroring).
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        // Garde les proportions de la source : elle est centrée dans l'image de sortie,
        // avec des bandes noires si les deux formats diffèrent.
        double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        int w = Math.Min(dstW, (int)Math.Round(srcW * scale)), h = Math.Min(dstH, (int)Math.Round(srcH * scale));
        int x = (dstW - w) / 2, y = (dstH - h) / 2;
        (_boxX, _boxY, _boxW, _boxH) = (x, y, w, h);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(x, y, x + w, y + h));

        // Espaces de couleur explicites : l'écran est en RGB plein (0-255), l'encodeur attend du
        // YCbCr BT.709 « studio » (16-235). Sans cela, le pilote choisit (souvent BT.601 ou plein).
        try
        {
            using var context1 = _videoContext.QueryInterface<ID3D11VideoContext1>();
            context1.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
            context1.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidCastException)
        {
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1 });
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        }

        if (!SelfTest(device))
        {
            _stagingBgra = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)srcW, Height = (uint)srcH, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
            });
            _columnMap = new int[dstW];
            for (int dx = 0; dx < dstW; dx++)
                _columnMap[dx] = dx < x || dx >= x + w ? -1 : Math.Min(srcW - 1, (dx - x) * srcW / w) * 4;
        }
    }

    /// <summary>
    /// Convertit deux couleurs connues (gris moyen, rouge) et vérifie le résultat au centre de l'image.
    /// Faux si le processeur vidéo du pilote se trompe (ou plante) : il faut alors convertir sur le processeur.
    /// </summary>
    private bool SelfTest(ID3D11Device device)
    {
        try
        {
            using var target = device.CreateRenderTargetView(InputTexture);
            (byte Y, byte U, byte V) Probe(Vortice.Mathematics.Color4 color)
            {
                _context.ClearRenderTargetView(target, color);
                Blt();
                _context.CopyResource(_stagingNv12, _outputNv12);
                var map = _context.Map(_stagingNv12, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int pitch = (int)map.RowPitch, cx = (_dstW / 2) & ~1, cy = (_dstH / 2) & ~1;
                    byte y = Marshal.ReadByte(map.DataPointer, cy * pitch + cx);
                    int uv = pitch * _dstH + cy / 2 * pitch + cx;
                    return (y, Marshal.ReadByte(map.DataPointer, uv), Marshal.ReadByte(map.DataPointer, uv + 1));
                }
                finally { _context.Unmap(_stagingNv12, 0); }
            }
            var gray = Probe(new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 1f)); // attendu ≈ 126, 128, 128
            var red = Probe(new Vortice.Mathematics.Color4(1f, 0f, 0f, 1f));        // attendu ≈ 63, 102, 240
            return Math.Abs(gray.Y - 126) <= 24 && Math.Abs(gray.U - 128) <= 10 && Math.Abs(gray.V - 128) <= 10
                && red.V >= 200 && red.U <= 116;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
    }

    /// <summary>Converts the current <see cref="InputTexture"/> into <paramref name="nv12"/> (Y plane then interleaved UV).</summary>
    public void Convert(byte[] nv12)
    {
        if (_stagingBgra is not null) { ConvertOnCpu(nv12); return; }
        Blt();
        CopyNv12(nv12);
    }

    private void Blt()
    {
        var inputView = _videoDevice.CreateVideoProcessorInputView(InputTexture, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
        try
        {
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView,
            };
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, [stream]);
        }
        finally
        {
            inputView.Dispose();
        }
    }

    private void CopyNv12(byte[] nv12)
    {
        _context.CopyResource(_stagingNv12, _outputNv12);
        var map = _context.Map(_stagingNv12, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)map.RowPitch;
            // Y plane: dstH rows of dstW bytes.
            for (int y = 0; y < _dstH; y++)
                Marshal.Copy(IntPtr.Add(map.DataPointer, y * rowPitch), nv12, y * _dstW, _dstW);
            // UV plane follows the Y plane at rowPitch*dstH; dstH/2 rows of dstW bytes.
            int uvSrcBase = rowPitch * _dstH;
            int uvDstBase = _dstW * _dstH;
            for (int y = 0; y < _dstH / 2; y++)
                Marshal.Copy(IntPtr.Add(map.DataPointer, uvSrcBase + y * rowPitch), nv12, uvDstBase + y * _dstW, _dstW);
        }
        finally
        {
            _context.Unmap(_stagingNv12, 0);
        }
    }

    /// <summary>
    /// Conversion de secours sur le processeur : BGRA → NV12 BT.709 studio, mise à l'échelle au plus proche,
    /// bandes noires autour si les proportions diffèrent.
    /// </summary>
    private unsafe void ConvertOnCpu(byte[] nv12)
    {
        _context.CopyResource(_stagingBgra!, InputTexture);
        var map = _context.Map(_stagingBgra!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int pitch = (int)map.RowPitch, w = _dstW, h = _dstH, uvBase = w * h;
            byte* src = (byte*)map.DataPointer;
            int[] columns = _columnMap!;
            fixed (byte* dst = nv12)
            {
                for (int dy = 0; dy < h; dy++)
                {
                    byte* yRow = dst + dy * w;
                    byte* uvRow = (dy & 1) == 0 ? dst + uvBase + dy / 2 * w : null;
                    if (dy < _boxY || dy >= _boxY + _boxH)
                    {
                        new Span<byte>(yRow, w).Fill(16);
                        if (uvRow != null) new Span<byte>(uvRow, w).Fill(128);
                        continue;
                    }
                    byte* line = src + Math.Min(_srcH - 1, (dy - _boxY) * _srcH / _boxH) * pitch;
                    for (int dx = 0; dx < w; dx++)
                    {
                        int c = columns[dx];
                        if (c < 0)
                        {
                            yRow[dx] = 16;
                            if (uvRow != null) uvRow[dx] = 128;
                            continue;
                        }
                        int b = line[c], g = line[c + 1], r = line[c + 2];
                        yRow[dx] = (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
                        if (uvRow != null && (dx & 1) == 0)
                        {
                            uvRow[dx] = (byte)(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128);
                            if (dx + 1 < w) uvRow[dx + 1] = (byte)(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
                        }
                    }
                }
            }
        }
        finally
        {
            _context.Unmap(_stagingBgra!, 0);
        }
    }

    public void Dispose()
    {
        _stagingBgra?.Dispose();
        _outputView.Dispose();
        _stagingNv12.Dispose();
        _outputNv12.Dispose();
        InputTexture.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
