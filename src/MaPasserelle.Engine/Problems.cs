// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using WinPlay.Core.Rtsp;

namespace MaPasserelle.Engine;

/// <summary>Messages en français, affichables tels quels, pour les échecs de connexion au téléviseur.</summary>
public static class Problems
{
    /// <summary>Explique pourquoi la connexion à <paramref name="device"/> a échoué.</summary>
    public static string Connection(Exception ex, string device) => ex switch
    {
        // Le téléviseur accepte la session puis refuse les flux : il n'arrive pas à rappeler le PC
        // (horloge, événements). Presque toujours le pare-feu Windows, parfois un réseau séparé.
        RtspException when ex.Message.Contains("SETUP", StringComparison.Ordinal) && ex.Message.Contains("400", StringComparison.Ordinal) =>
            $"{device} refuse le partage, car il n'arrive pas à joindre ce PC. Vérifiez que le pare-feu Windows autorise Ma Passerelle "
            + "(réseaux privés et publics) et que le PC et l'appareil sont sur le même réseau.",
        SocketException or TimeoutException =>
            $"{device} ne répond pas. Vérifiez qu'il est allumé et sur le même réseau que ce PC.",
        _ => $"La connexion à {device} a échoué ({ex.Message}).",
    };
}
