// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using MaPasserelle.Capture;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Mirror;

namespace MaPasserelle.Engine;

/// <summary>Pourquoi un partage s'est terminé. <see cref="Reason"/> reprend les valeurs du contrat.</summary>
public sealed record MirrorEnd(string Reason, string? ErrorCode = null, string? Message = null)
{
    public bool IsError => ErrorCode is not null;
}

/// <summary>
/// Un partage vers un téléviseur : connexion, capture, envoi, et détection de toutes les fins
/// (téléviseur coupé, source fermée, capture en échec, flux figé). Usage : créer, s'abonner aux
/// événements, <see cref="StartAsync"/>, attendre <see cref="Completion"/> ou appeler
/// <see cref="Stop"/>, puis libérer (ce qui envoie l'ordre d'arrêt au téléviseur).
/// </summary>
public sealed class Mirroring : IAsyncDisposable
{
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(20);

    private readonly AirPlayDevice _device;
    private readonly HapPairingCredentials _credentials;
    private readonly MirrorOptions _options;
    private readonly bool _withAudio;
    private readonly TaskCompletionSource<MirrorEnd> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _watch = new();
    private readonly Stopwatch _clock = new();
    private MirrorSession? _session;
    private MirrorSource? _source;
    private int _disposed;

    public Mirroring(AirPlayDevice device, HapPairingCredentials credentials, MirrorOptions options, bool withAudio = true)
    {
        _device = device;
        _credentials = credentials;
        _options = options;
        _withAudio = withAudio;
    }

    /// <summary>Étapes techniques de la connexion et de la capture (journal).</summary>
    public event Action<string>? Stage;
    /// <summary>La vidéo part : largeur, hauteur, débit en bit/s, nom de l'encodeur.</summary>
    public event Action<int, int, int, string>? Video;

    public AirPlayDevice Device => _device;
    public Task<MirrorEnd> Completion => _end.Task;
    public long FramesSent => _session?.FramesSent ?? 0;
    public bool HasAudio => _session?.HasAudio ?? false;
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Se connecte et lance l'envoi. Lève une exception si la connexion échoue.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Devices.Ipv4(_device) is not { } address)
            throw new InvalidOperationException($"{_device.Name} n'a pas d'adresse IPv4");

        _session = await MirrorSession.ConnectAsync(address, _device.AirPlayPort ?? Devices.DefaultPort, _credentials,
            stage =>
            {
                Stage?.Invoke(stage);
                // Le téléviseur a coupé (veille, extinction, autre source) : la bibliothèque le signale
                // seulement par ces messages, et continue de compter des images.
                if (stage.StartsWith("data channel closed", StringComparison.Ordinal)
                    || stage.StartsWith("feedback ended", StringComparison.Ordinal)
                    || stage.StartsWith("event channel closed", StringComparison.Ordinal))
                    _end.TrySetResult(new("disconnected", "disconnected", "le téléviseur a mis fin au partage"));
            }, ct, _withAudio);

        _source = new MirrorSource(_options);
        _source.Diagnostic += d => Stage?.Invoke("capture: " + d);
        _source.Started += (w, h, bitrate, encoder) => Video?.Invoke(w, h, bitrate, encoder);
        _source.Ended += (failed, message) => _end.TrySetResult(failed
            ? new("capture_failed", "capture_failed", message)
            : new("source_closed"));

        await _session.StartStreamingAsync(_source, _session.HasAudio ? new LoopbackAudioSource() : null);
        _clock.Start();
        _ = WatchStallAsync(_watch.Token);
    }

    /// <summary>Demande l'arrêt ; <paramref name="reason"/> devient la raison de fin si le partage tournait encore.</summary>
    public void Stop(string reason = "command") => _end.TrySetResult(new(reason));

    // Flux figé : plus aucune image envoyée pendant 20 s = le téléviseur a très probablement coupé.
    private async Task WatchStallAsync(CancellationToken ct)
    {
        long lastFrames = -1;
        var lastProgress = Stopwatch.StartNew();
        try
        {
            while (!_end.Task.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                long frames = FramesSent;
                if (frames != lastFrames) { lastFrames = frames; lastProgress.Restart(); }
                else if (lastProgress.Elapsed > StallLimit)
                    _end.TrySetResult(new("stalled", "stream_stalled", "plus aucune image envoyée depuis 20 secondes"));
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _end.TrySetResult(new("command"));
        _watch.Cancel();
        if (_source is not null) await _source.DisposeAsync();
        // La fermeture de la session envoie l'ordre d'arrêt au téléviseur.
        if (_session is not null) await _session.DisposeAsync();
        _watch.Dispose();
        _clock.Stop();
    }
}
