// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;

namespace MaPasserelle.App;

/// <summary>
/// Lancement au démarrage de Windows, discret (--tray : seulement l'icône près de l'horloge).
/// Version installée à la main : clé Run de l'utilisateur. Le paquet MSIX passera par sa propre
/// tâche de démarrage (StartupTask), que Windows exige pour une application empaquetée.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ma Passerelle";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Si l'application a été déplacée, la clé pointe à nouveau vers le bon exécutable.</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}
