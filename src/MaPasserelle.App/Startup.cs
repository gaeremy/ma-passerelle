// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace MaPasserelle.App;

/// <summary>
/// Démarrer avec Windows, discrètement (seulement l'icône près de l'horloge). Version paquet : la
/// tâche de démarrage du manifeste (Gestionnaire des tâches → Démarrage). Version autonome : une
/// valeur dans HKCU\…\Run, avec --tray. L'état vit dans Windows : on le relit à chaque affichage.
/// Même mécanisme que Mon Estrade (Services/StartWithWindows).
/// </summary>
public static class Startup
{
    public enum Status { On, Off, BlockedByUser, ByPolicy }

    public const string TrayArgument = "--tray";
    private const string TaskId = "MaPasserelleStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Ma Passerelle";

    public static bool IsPackaged
    {
        get
        {
            try { return Package.Current is not null; }
            catch (Exception) { return false; }
        }
    }

    public static async Task<Status> GetAsync()
    {
        if (!IsPackaged) return Registered() ? Status.On : Status.Off;
        var task = await StartupTask.GetAsync(TaskId);
        return Map(task.State);
    }

    /// <summary>Active ou désactive ; renvoie l'état obtenu (Windows peut refuser).</summary>
    public static async Task<Status> SetAsync(bool on)
    {
        if (!IsPackaged)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" {TrayArgument}");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            return Registered() ? Status.On : Status.Off;
        }
        var task = await StartupTask.GetAsync(TaskId);
        if (on && task.State == StartupTaskState.Disabled) return Map(await task.RequestEnableAsync());
        if (!on && task.State == StartupTaskState.Enabled) task.Disable();
        return Map(task.State);
    }

    /// <summary>Version autonome déplacée : la clé pointe à nouveau vers le bon exécutable.</summary>
    public static void Refresh()
    {
        if (IsPackaged) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is string && !Registered())
        {
            key.Dispose();
            using var writable = Registry.CurrentUser.CreateSubKey(RunKey);
            writable.SetValue(RunValue, $"\"{Environment.ProcessPath}\" {TrayArgument}");
        }
    }

    private static bool Registered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string command
            && command.StartsWith($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static Status Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => Status.On,
        StartupTaskState.DisabledByUser => Status.BlockedByUser,
        StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => Status.ByPolicy,
        _ => Status.Off,
    };
}
