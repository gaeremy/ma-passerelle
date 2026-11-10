// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace MaPasserelle.Capture;

/// <summary>
/// Capture d'un écran ou d'une fenêtre par l'API Windows.Graphics.Capture. Contrairement à
/// la duplication de bureau, elle inclut le pointeur de la souris, sait suivre une fenêtre
/// (même partiellement recouverte) et fonctionne pour n'importe quel écran.
/// Une instance par fil de capture ; non réentrant.
/// </summary>
internal sealed class GraphicsCapture : IFrameCapture
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private volatile bool _closed;

    public ID3D11Device Device { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Name { get; }

    public GraphicsCapture(nint handle, bool isWindow, bool cursor)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels,
            out ID3D11Device? device).CheckError();
        Device = device!;
        _context = Device.ImmediateContext;

        using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint inspectable));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        nint itemPointer = isWindow ? interop.CreateForWindow(handle, ref iid) : interop.CreateForMonitor(handle, ref iid);
        _item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);
        _item.Closed += (_, _) => _closed = true;
        Name = (isWindow ? "fenêtre" : "écran") + " « " + _item.DisplayName + " »";

        SizeInt32 size = _item.Size;
        Width = Math.Max(2, size.Width);
        Height = Math.Max(2, size.Height);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, PixelFormat, 2, new SizeInt32(Width, Height));
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = cursor;
        // Le liseré jaune autour de la zone capturée ne peut être retiré que sur Windows 11.
        try { _session.IsBorderRequired = false; } catch (Exception) { }
        _session.StartCapture();
    }

    public CaptureResult TryCaptureInto(ID3D11Texture2D dest, int timeoutMs)
    {
        if (_closed) return CaptureResult.Ended;

        // On ne garde que l'image la plus récente.
        Direct3D11CaptureFrame? frame = null;
        while (_pool.TryGetNextFrame() is { } next)
        {
            frame?.Dispose();
            frame = next;
        }
        if (frame is null) return CaptureResult.NoFrame;

        using (frame)
        {
            SizeInt32 size = frame.ContentSize;
            if (size.Width < 2 || size.Height < 2) return CaptureResult.NoFrame;
            if (size.Width != Width || size.Height != Height)
            {
                Width = size.Width;
                Height = size.Height;
                _pool.Recreate(_winrtDevice, PixelFormat, 2, size);
                return CaptureResult.SizeChanged;
            }

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid iid = Texture2DIid;
            using var texture = new ID3D11Texture2D(access.GetInterface(ref iid));
            _context.CopySubresourceRegion(dest, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
            return CaptureResult.Frame;
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        _winrtDevice.Dispose();
        _context.Dispose();
        Device.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }
}
