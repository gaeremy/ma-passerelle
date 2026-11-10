// SPDX-License-Identifier: GPL-3.0-or-later
// Dérivé de WinPlay.Capture/ScreenMirrorSource.cs (© 2026 Dinesh Dhotrad), modifié pour
// Ma Passerelle : choix de l'écran ou d'une fenêtre, résolution plafonnée, fin de source.
using System.Diagnostics;
using WinPlay.Core.Mirror;

namespace MaPasserelle.Capture;

public enum CaptureKind { Screen, Window }

public enum CaptureBackend
{
    /// <summary>Windows.Graphics.Capture : écrans et fenêtres, avec le pointeur.</summary>
    Graphics,
    /// <summary>Duplication de bureau DXGI : écrans seulement, sans le pointeur. Solution de repli.</summary>
    Duplication,
}

/// <summary>Ce qu'on partage et comment.</summary>
public sealed record MirrorOptions
{
    public CaptureKind Kind { get; init; } = CaptureKind.Screen;
    /// <summary>HMONITOR ou HWND selon <see cref="Kind"/> ; 0 = l'écran principal.</summary>
    public nint Handle { get; init; }
    /// <summary>Hauteur maximale de l'image envoyée : 720, 1080 ou 2160 ; 0 = automatique (1080).</summary>
    public int MaxHeight { get; init; }
    public int Fps { get; init; } = 30;
    /// <summary>Débit en Mbit/s ; 0 = calculé d'après la résolution et la cadence.</summary>
    public int BitrateMbps { get; init; }
    public bool Cursor { get; init; } = true;
    public CaptureBackend Backend { get; init; } = CaptureBackend.Graphics;
}

/// <summary>
/// <see cref="IH264VideoSource"/> qui partage un écran ou une fenêtre : capture GPU en BGRA →
/// NV12 (conversion et mise à l'échelle) → H.264 Media Foundation → unités d'accès Annex-B.
/// Tourne sur un fil MTA dédié, cadencé à la fréquence demandée ; quand l'image ne change
/// pas, la précédente est ré-encodée pour que l'horloge du récepteur continue d'avancer.
/// </summary>
public sealed class MirrorSource : IH264VideoSource
{
    private readonly MirrorOptions _options;
    private Thread? _thread;
    private volatile bool _stopped;
    private int _receiverDisplayW;
    private int _receiverDisplayH;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public event Action<ReadOnlyMemory<byte>, bool>? FrameEncoded;
    public event Action<string>? Diagnostic;
    /// <summary>La source a disparu (false : fenêtre fermée, écran débranché) ou la capture a échoué (true).</summary>
    public event Action<bool, string>? Ended;
    /// <summary>La capture a démarré : largeur et hauteur envoyées, débit en bit/s, nom de l'encodeur.</summary>
    public event Action<int, int, int, string>? Started;

    public MirrorSource(MirrorOptions options) => _options = options;

    public void Configure(int receiverDisplayWidth, int receiverDisplayHeight)
    {
        _receiverDisplayW = receiverDisplayWidth;
        _receiverDisplayH = receiverDisplayHeight;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(() => CaptureLoop(ct))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "MaPasserelle-Capture",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public void RequestKeyframe() { /* the MFT emits an IDR + SPS/PPS as its first output automatically */ }

    private void CaptureLoop(CancellationToken ct)
    {
        IFrameCapture? capture = null;
        GpuColorConverter? converter = null;
        IH264Encoder? encoder = null;
        int fps = _options.Fps;
        try
        {
            capture = CreateCapture();
            int srcW = capture.Width, srcH = capture.Height;

            // La taille envoyée est fixée une fois pour toutes : l'encodeur et le récepteur ne
            // supportent pas d'en changer en cours de route. Une fenêtre redimensionnée est
            // simplement remise à l'échelle à l'intérieur.
            (Width, Height) = EncodeSize(_options.Kind, srcW, srcH, _options.MaxHeight, _receiverDisplayW, _receiverDisplayH);
            int bitrate = _options.BitrateMbps > 0 ? _options.BitrateMbps * 1_000_000 : AutoBitrate(Width, Height, fps);

            converter = new GpuColorConverter(capture.Device, srcW, srcH, Width, Height, fps);

            // Prefer the GPU hardware encoder (NVENC/QuickSync/AMF/Adreno); fall back to
            // the software MFT only if no hardware encoder is present.
            encoder = CreateEncoder(Width, Height, fps, bitrate);
            encoder.Encoded += au => FrameEncoded?.Invoke(au, IsKeyframe(au));
            Diagnostic?.Invoke($"{capture.Name} {srcW}x{srcH} → {Width}x{Height} @ {fps} images/s, {bitrate / 1_000_000} Mbit/s ({encoder.Name})");

            Started?.Invoke(Width, Height, bitrate, encoder.Name);

            byte[] nv12 = new byte[Width * Height * 3 / 2]; // NV12 = Y (w*h) + interleaved UV (w*h/2)
            bool haveFrame = false;

            var sw = Stopwatch.StartNew();
            long frameIndex = 0;
            double frameMs = 1000.0 / fps;

            while (!_stopped && !ct.IsCancellationRequested)
            {
                double dueMs = frameIndex * frameMs;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs)
                {
                    Thread.Sleep(Math.Max(1, (int)(dueMs - nowMs)));
                    continue;
                }

                switch (capture.TryCaptureInto(converter.InputTexture, timeoutMs: 12))
                {
                    case CaptureResult.Frame:
                        converter.Convert(nv12);
                        haveFrame = true;
                        break;
                    case CaptureResult.SizeChanged:
                        converter.Dispose();
                        converter = new GpuColorConverter(capture.Device, capture.Width, capture.Height, Width, Height, fps);
                        Diagnostic?.Invoke($"source redimensionnée : {capture.Width}x{capture.Height}");
                        continue; // la prochaine image arrive à la bonne taille
                    case CaptureResult.Ended:
                        Ended?.Invoke(false, "la fenêtre ou l'écran partagé n'existe plus");
                        return;
                }
                if (!haveFrame) { frameIndex++; continue; } // nothing captured yet

                encoder.Encode(nv12);
                frameIndex++;
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !_stopped)
            {
                Diagnostic?.Invoke($"capture/encode error: {ex.Message}");
                Ended?.Invoke(true, "la capture a échoué : " + ex.Message);
            }
        }
        finally
        {
            encoder?.Dispose();
            converter?.Dispose();
            capture?.Dispose();
        }
    }

    private IFrameCapture CreateCapture()
    {
        if (_options.Kind == CaptureKind.Window)
            return new GraphicsCapture(_options.Handle, isWindow: true, _options.Cursor);

        nint monitor = _options.Handle != 0 ? _options.Handle : DesktopCatalog.Screens()[0].Handle;
        if (_options.Backend == CaptureBackend.Duplication) return new DesktopDuplicator(monitor);
        try
        {
            return new GraphicsCapture(monitor, isWindow: false, _options.Cursor);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"capture moderne indisponible ({ex.Message}) ; duplication de bureau, sans pointeur");
            return new DesktopDuplicator(monitor);
        }
    }

    private IH264Encoder CreateEncoder(int width, int height, int fps, int bitrate)
    {
        try
        {
            return new HardwareH264Encoder(width, height, fps, bitrate);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"hardware encoder unavailable ({ex.Message}); using software");
            return new MediaFoundationH264Encoder(width, height, fps, bitrate);
        }
    }

    private static bool IsKeyframe(byte[] annexB) => H264.ContainsKeyframe(H264.SplitAnnexB(annexB));

    /// <summary>
    /// Taille de l'image envoyée. Le cadre maximal est en 16:9 (1280x720, 1920x1080 ou
    /// 3840x2160), réduit à l'affichage du récepteur s'il l'annonce.
    /// Un écran y est ajusté en gardant ses proportions, sans jamais être agrandi.
    /// Une fenêtre occupe le cadre entier, avec des bandes noires autour : sa taille peut
    /// ainsi changer sans toucher au flux.
    /// </summary>
    internal static (int Width, int Height) EncodeSize(CaptureKind kind, int srcW, int srcH, int maxHeight, int dispW, int dispH)
    {
        int boxH = maxHeight > 0 ? maxHeight : 1080;
        int boxW = boxH * 16 / 9;
        if (dispW > 0 && dispH > 0 && (boxW > dispW || boxH > dispH))
        {
            double fit = Math.Min((double)dispW / boxW, (double)dispH / boxH);
            boxW = (int)Math.Round(boxW * fit);
            boxH = (int)Math.Round(boxH * fit);
        }

        int w = boxW, h = boxH;
        if (kind == CaptureKind.Screen)
        {
            double scale = Math.Min(1.0, Math.Min((double)boxW / srcW, (double)boxH / srcH));
            w = (int)Math.Round(srcW * scale);
            h = (int)Math.Round(srcH * scale);
        }
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1)); // NV12 : dimensions paires
    }

    /// <summary>Bitrate scaled to resolution + frame rate (~0.10 bits/pixel/frame), clamped to a sane range.</summary>
    internal static int AutoBitrate(int width, int height, int fps)
    {
        long bits = (long)(width * height * fps * 0.10);
        return (int)Math.Clamp(bits, 4_000_000, 60_000_000);
    }

    public ValueTask DisposeAsync()
    {
        _stopped = true;
        _thread?.Join(2000);
        return ValueTask.CompletedTask;
    }
}
