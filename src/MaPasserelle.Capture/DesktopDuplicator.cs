// SPDX-License-Identifier: GPL-3.0-or-later
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MaPasserelle.Capture;

/// <summary>
/// Captures one screen via the DXGI Desktop Duplication API (no mouse pointer). GPU-composited,
/// low-overhead, and vendor-independent (Intel/AMD/NVIDIA/Qualcomm/MediaTek on Windows).
/// Each captured frame is copied on the GPU into a caller-owned texture and the DXGI
/// frame is released immediately, minimizing duplication back-pressure. One instance per
/// capture thread; not thread-safe.
/// </summary>
internal sealed class DesktopDuplicator : IFrameCapture
{
    private readonly IDXGIOutputDuplication _duplication;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    public string Name { get; }

    /// <param name="monitor">Écran à dupliquer (HMONITOR) ; 0 = l'écran principal.</param>
    public DesktopDuplicator(nint monitor = 0)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var (adapter, output) = FindOutput(monitor);
        using (adapter)
        using (output)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        {
            // La duplication exige que le périphérique soit créé sur la carte qui pilote l'écran.
            // BgraSupport enables the video processor; VideoSupport enables the color-convert path.
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels,
                out ID3D11Device? device).CheckError();
            Device = device!;
            Context = Device.ImmediateContext;

            var desc = output.Description;
            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            Name = "écran « " + desc.DeviceName + " » (duplication)";
            _duplication = output1.DuplicateOutput(Device);
        }
        Format = Format.B8G8R8A8_UNorm;
    }

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output) FindOutput(nint monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
            {
                if (monitor == 0 || output!.Description.Monitor == monitor) return (adapter, output!);
                output.Dispose();
            }
            adapter.Dispose();
        }
        throw new InvalidOperationException("écran introuvable");
    }

    /// <summary>Creates a texture matching the desktop, suitable as a video-processor input.</summary>
    public ID3D11Texture2D CreateBgraTexture() => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    /// <summary>
    /// Copies the next desktop frame into <paramref name="dest"/> on the GPU and releases
    /// the DXGI frame. Returns false if no new frame arrived within the timeout (screen
    /// unchanged) so the caller can re-encode the previous frame.
    /// </summary>
    public CaptureResult TryCaptureInto(ID3D11Texture2D dest, int timeoutMs)
    {
        Result acquire = _duplication.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo _, out IDXGIResource? resource);
        if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            resource?.Dispose();
            return CaptureResult.NoFrame;
        }
        if (acquire == Vortice.DXGI.ResultCode.AccessLost)
        {
            resource?.Dispose();
            return CaptureResult.Ended;
        }
        acquire.CheckError();
        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            Context.CopyResource(dest, texture);
            return CaptureResult.Frame;
        }
        finally
        {
            resource!.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
