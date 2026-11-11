// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using WinPlay.Core.Discovery;
using WinPlay.Core.Plist;
using WinPlay.Core.Rtsp;

namespace MaPasserelle.Engine;

/// <summary>Trouver les téléviseurs : recherche sur le réseau local, ou question directe à une adresse.</summary>
public static class Devices
{
    public const int DefaultPort = 7000;

    public static IPAddress? Ipv4(AirPlayDevice device) =>
        device.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>Vrai si <paramref name="target"/> est une adresse IPv4, éventuellement suivie du port.</summary>
    public static bool TryParseAddress(string target, out IPAddress address, out int port)
    {
        if (IPEndPoint.TryParse(target, out var endpoint) && endpoint.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            (address, port) = (endpoint.Address, endpoint.Port == 0 ? DefaultPort : endpoint.Port);
            return true;
        }
        (address, port) = (IPAddress.None, 0);
        return false;
    }

    /// <summary>Écoute le réseau pendant <paramref name="duration"/> et renvoie les appareils vus, triés par nom.</summary>
    public static async Task<IReadOnlyList<AirPlayDevice>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        using var browser = new AirPlayBrowser();
        browser.Start();
        try { await Task.Delay(duration, ct); }
        catch (OperationCanceledException) { }
        return [.. browser.Snapshot().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Cherche un appareil par nom (sans tenir compte de la casse) ou par identifiant sur le réseau
    /// local, pendant au plus <paramref name="seconds"/> secondes. Une adresse IP évite la recherche,
    /// que certains réseaux bloquent (Wi-Fi d'établissement, réseaux séparés).
    /// </summary>
    public static async Task<AirPlayDevice?> FindAsync(string target, int seconds = 8, CancellationToken ct = default)
    {
        if (TryParseAddress(target, out var address, out int port))
            return await AskAsync(address, port, ct);

        using var browser = new AirPlayBrowser();
        browser.Start();
        string id = AirPlayDevice.NormalizeDeviceId(target);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        AirPlayDevice? found = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            found = browser.Snapshot().FirstOrDefault(d =>
                string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DeviceId, id, StringComparison.OrdinalIgnoreCase));
            if (found is not null && Ipv4(found) is not null) break;
            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { }
        }
        return found;
    }

    /// <summary>Interroge directement un appareil (GET /info) ; null s'il ne répond pas dans les 5 secondes.</summary>
    public static async Task<AirPlayDevice?> AskAsync(IPAddress address, int port = DefaultPort, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var rtsp = new RtspConnection();
            await rtsp.ConnectAsync(address, port, timeout.Token);
            var response = await rtsp.RequestAsync(new RtspRequest { Method = "GET", Uri = "/info" }, timeout.Token);
            response.EnsureSuccess("GET /info");
            var info = BinaryPlist.ReadDictionary(response.Body);
            if (info.GetValueOrDefault("deviceID") is not string deviceId) return null;
            return new AirPlayDevice
            {
                DeviceId = AirPlayDevice.NormalizeDeviceId(deviceId),
                Name = info.GetValueOrDefault("name") as string ?? address.ToString(),
                Model = info.GetValueOrDefault("model") as string,
                RawFeatures = info.GetValueOrDefault("features") is long features ? (ulong)features : 0,
                PublicKey = info.GetValueOrDefault("pk") is byte[] pk ? Convert.ToHexString(pk).ToLowerInvariant() : null,
                Addresses = [address],
                AirPlayPort = port,
            };
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
            or RtspException or FormatException or InvalidDataException)
        {
            return null;
        }
    }
}
