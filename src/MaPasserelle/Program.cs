// SPDX-License-Identifier: GPL-3.0-or-later
// Ma Passerelle — partage d'écran compatible Apple TV, compagnon de Mon Estrade.
// Programme autonome piloté en ligne de commande ; le contrat est décrit dans docs/CONTRAT.md.
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WinPlay.Capture;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Fairplay;
using WinPlay.Core.Hap;
using WinPlay.Core.Mirror;
using WinPlay.Core.Rtsp;

const int ExitOk = 0, ExitError = 1, ExitUsage = 2, ExitNotFound = 3, ExitNotPaired = 4, ExitPairingFailed = 5, ExitConnection = 6;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "help";
bool json = HasFlag("--json");
var outLock = new object();
var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

try
{
    return command switch
    {
        "discover" => await DiscoverAsync(),
        "pair" => await PairAsync(),
        "unpair" => await UnpairAsync(),
        "mirror" => await MirrorAsync(),
        "version" => Version(),
        "help" => Help(),
        _ => Fail(ExitUsage, "usage", $"commande inconnue « {command} »"),
    };
}
catch (Exception ex) when (ex is SocketException or IOException or RtspException or FairplayException or TimeoutException)
{
    return Fail(ExitConnection, "connection_failed", ex.Message);
}
catch (HapPairingException ex)
{
    return Fail(ExitPairingFailed, "pairing_failed", ex.Message);
}
catch (PairingRequiredException ex)
{
    return Fail(ExitNotPaired, "not_paired", ex.Message);
}

// ------------------------------------------------------------------ commandes

async Task<int> DiscoverAsync()
{
    int seconds = int.TryParse(GetOpt("--seconds"), out int s) ? Math.Clamp(s, 1, 60) : 6;
    Emit("searching", ("seconds", seconds));
    using var browser = new AirPlayBrowser();
    browser.Start();
    await Task.Delay(TimeSpan.FromSeconds(seconds));

    var store = new Credentials();
    var devices = browser.Snapshot();
    foreach (var d in devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
    {
        Emit("device",
            ("id", d.DeviceId),
            ("name", d.Name),
            ("model", d.Model),
            ("address", Ipv4(d)?.ToString()),
            ("mirroring", d.IsMirroringCandidate),
            ("audio", d.SupportsAudio),
            ("paired", store.Load(d.DeviceId) is not null));
    }
    Emit("done", ("count", devices.Count));
    return ExitOk;
}

async Task<int> PairAsync()
{
    if (GetOpt("--to") is not { } target) return Fail(ExitUsage, "usage", "pair demande --to <nom ou id>");
    var device = await FindAsync(target);
    if (device is null || Ipv4(device) is not { } address)
        return Fail(ExitNotFound, "not_found", $"appareil « {target} » introuvable sur le réseau");

    using var handle = await ReceiverPairing.BeginAsync(address, device.AirPlayPort ?? 7000, CancellationToken.None);
    Emit("pin_required", ("id", device.DeviceId), ("name", device.Name));

    string? pin = GetOpt("--pin");
    if (string.IsNullOrWhiteSpace(pin))
    {
        // Le code affiché sur le téléviseur est attendu sur l'entrée standard (une ligne).
        var read = Console.In.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(120))) == read) pin = (await read)?.Trim();
    }
    if (string.IsNullOrWhiteSpace(pin))
        return Fail(ExitPairingFailed, "pin_missing", "aucun code reçu dans les 120 secondes");

    var credentials = await handle.FinishAsync(pin, CancellationToken.None);
    new Credentials().Save(device.DeviceId, credentials);
    Emit("paired", ("id", device.DeviceId), ("name", device.Name));
    return ExitOk;
}

async Task<int> UnpairAsync()
{
    if (GetOpt("--to") is not { } target) return Fail(ExitUsage, "usage", "unpair demande --to <nom ou id>");
    // L'id suffit : l'appareil n'a pas besoin d'être allumé pour être oublié.
    var device = await FindAsync(target, seconds: 4);
    string id = device?.DeviceId ?? AirPlayDevice.NormalizeDeviceId(target);
    new Credentials().Remove(id);
    Emit("unpaired", ("id", id), ("name", device?.Name));
    return ExitOk;
}

async Task<int> MirrorAsync()
{
    if (GetOpt("--to") is not { } target) return Fail(ExitUsage, "usage", "mirror demande --to <nom ou id>");
    int fps = int.TryParse(GetOpt("--fps"), out int f) ? Math.Clamp(f, 5, 60) : 30;
    int mbps = int.TryParse(GetOpt("--mbps"), out int mb) ? Math.Clamp(mb, 0, 50) : 0;
    double? minutes = double.TryParse(GetOpt("--minutes"), System.Globalization.CultureInfo.InvariantCulture, out double m) ? m : null;
    bool withAudio = !HasFlag("--no-audio");

    var device = await FindAsync(target);
    if (device is null || Ipv4(device) is not { } address)
        return Fail(ExitNotFound, "not_found", $"appareil « {target} » introuvable sur le réseau");
    if (!device.IsMirroringCandidate)
        return Fail(ExitError, "not_supported", $"{device.Name} n'accepte pas le partage d'écran");
    var credentials = new Credentials().Load(device.DeviceId);
    if (credentials is null)
        return Fail(ExitNotPaired, "not_paired", $"{device.Name} n'est pas encore jumelé : lancer d'abord « pair »");

    // Trois façons d'arrêter : la commande « stop » (ou la fermeture) sur l'entrée standard,
    // Ctrl+C, ou la durée limite. Mon Estrade tient l'entrée standard : s'il se ferme, on s'arrête.
    var stop = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult("signal"); };

    Emit("connecting", ("id", device.DeviceId), ("name", device.Name), ("address", address.ToString()));
    var session = await MirrorSession.ConnectAsync(address, device.AirPlayPort ?? 7000, credentials,
        stage => Emit("stage", ("message", stage)), withAudio: withAudio);
    var started = DateTime.UtcNow;
    int exit = ExitOk;
    string reason = "error";
    try
    {
        await using var source = new ScreenMirrorSource(fps, mbps);
        source.Diagnostic += d => Emit("stage", ("message", "capture: " + d));
        bool audio = session.HasAudio;
        await session.StartStreamingAsync(source, audio ? new LoopbackAudioSource() : null);
        started = DateTime.UtcNow;
        Emit("streaming", ("id", device.DeviceId), ("name", device.Name), ("fps", fps), ("mbps", mbps), ("audio", audio));

        void Status() => Emit("status",
            ("frames", session.FramesSent),
            ("seconds", (long)(DateTime.UtcNow - started).TotalSeconds),
            ("audio", audio));

        if (!HasFlag("--no-stdin"))
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    string? line = await Console.In.ReadLineAsync();
                    if (line is null) { stop.TrySetResult("stdin_closed"); return; }
                    switch (line.Trim().ToLowerInvariant())
                    {
                        case "stop": stop.TrySetResult("command"); return;
                        case "status": Status(); break;
                        case "": break;
                        default: Emit("error", ("code", "unknown_command"), ("message", $"commande inconnue « {line.Trim()} »")); break;
                    }
                }
            });
        }

        // Surveillance : état toutes les 10 s, durée limite, et flux figé (plus aucune image
        // envoyée pendant 20 s = le téléviseur a très probablement coupé la session).
        long lastFrames = 0;
        var lastProgress = DateTime.UtcNow;
        while (true)
        {
            var tick = Task.Delay(TimeSpan.FromSeconds(10));
            if (await Task.WhenAny(stop.Task, tick) == stop.Task) { reason = await stop.Task; break; }
            Status();
            long frames = session.FramesSent;
            if (frames != lastFrames) { lastFrames = frames; lastProgress = DateTime.UtcNow; }
            else if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(20))
            {
                Emit("error", ("code", "stream_stalled"), ("message", "plus aucune image envoyée depuis 20 secondes"));
                reason = "stalled"; exit = ExitConnection; break;
            }
            if (minutes is { } limit && DateTime.UtcNow - started > TimeSpan.FromMinutes(limit)) { reason = "time_limit"; break; }
        }

    }
    finally
    {
        // La fermeture envoie l'ordre d'arrêt au téléviseur ; « stopped » est toujours le dernier événement.
        long frames = session.FramesSent;
        await session.DisposeAsync();
        Emit("stopped", ("reason", reason), ("frames", frames), ("seconds", (long)(DateTime.UtcNow - started).TotalSeconds));
    }
    return exit;
}

int Version()
{
    string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
    Emit("version", ("name", "Ma Passerelle"), ("version", version), ("contract", 1));
    return ExitOk;
}

int Help()
{
    Console.WriteLine("""
        Ma Passerelle — partage d'écran compatible Apple TV

          mapasserelle discover [--seconds N]           liste les appareils du réseau
          mapasserelle pair --to <nom|id> [--pin NNNN]  jumelle (le code s'affiche sur le téléviseur)
          mapasserelle unpair --to <nom|id>             oublie un jumelage
          mapasserelle mirror --to <nom|id> [--fps N] [--mbps N] [--no-audio] [--minutes M] [--no-stdin]
                                                        partage l'écran ; taper « stop » ou « status »
          mapasserelle version

        Ajouter --json pour une sortie lisible par un programme (une ligne JSON par événement).
        Détail du contrat : docs/CONTRAT.md
        """);
    return ExitOk;
}

// ------------------------------------------------------------------ outils

string? GetOpt(string name) =>
    args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length)
        .Select(x => args[x.i + 1]).FirstOrDefault();

bool HasFlag(string name) => args.Contains(name);

static IPAddress? Ipv4(AirPlayDevice d) =>
    d.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

/// Cherche un appareil par nom (sans tenir compte de la casse) ou par identifiant.
async Task<AirPlayDevice?> FindAsync(string target, int seconds = 8)
{
    Emit("searching", ("target", target));
    using var browser = new AirPlayBrowser();
    browser.Start();
    string id = AirPlayDevice.NormalizeDeviceId(target);
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    AirPlayDevice? found = null;
    while (DateTime.UtcNow < deadline)
    {
        found = browser.Snapshot().FirstOrDefault(d =>
            string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.DeviceId, id, StringComparison.OrdinalIgnoreCase));
        if (found is not null && Ipv4(found) is not null) break;
        await Task.Delay(300);
    }
    return found;
}

/// Écrit un événement : une ligne JSON avec --json, sinon une ligne lisible.
void Emit(string name, params (string Key, object? Value)[] fields)
{
    string line;
    if (json)
    {
        var o = new Dictionary<string, object?> { ["event"] = name };
        foreach (var (k, v) in fields) o[k] = v;
        line = JsonSerializer.Serialize(o, jsonOptions);
    }
    else
    {
        line = name + string.Concat(fields.Where(x => x.Value is not null).Select(x => $"  {x.Key}={x.Value}"));
    }
    lock (outLock)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}

int Fail(int exitCode, string code, string message)
{
    Emit("error", ("code", code), ("message", message));
    return exitCode;
}

/// Jumelages de Ma Passerelle (%APPDATA%\MaPasserelle), protégés par DPAPI pour l'utilisateur courant.
/// Un jumelage déjà fait avec WinPlay sur ce compte est repris tel quel à la première utilisation.
sealed class Credentials
{
    private readonly CredentialStore _own = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaPasserelle", "credentials.dat"));

    public HapPairingCredentials? Load(string deviceId)
    {
        if (_own.Load(deviceId) is { } mine) return mine;
        if (new CredentialStore().Load(deviceId) is not { } imported) return null;
        _own.Save(deviceId, imported);
        return imported;
    }

    public void Save(string deviceId, HapPairingCredentials credentials) => _own.Save(deviceId, credentials);

    public void Remove(string deviceId) => _own.Remove(deviceId);
}
