// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaPasserelle.App;

/// <summary>
/// Ma Passerelle dans la zone de notification : un clic sur l'icône ouvre le panneau, un clic droit
/// ouvre un menu (partager ou arrêter, ouvrir, quitter). Une seule instance à la fois : en relancer
/// une autre rouvre simplement le panneau de la première.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\MaPasserelle.Instance";
    private const string ShowSignalName = @"Local\MaPasserelle.Show";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private PanelWindow? _panel;
    private TaskbarIcon? _tray;
    private MenuFlyoutItem? _shareItem;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    /// <summary>Note une erreur imprévue dans %APPDATA%\MaPasserelle\erreurs.log, sans arrêter le programme.</summary>
    public static void Log(Exception exception)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaPasserelle");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "erreurs.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // Une instance tourne déjà : on lui demande d'afficher son panneau, et on s'arrête.
            // Windows a donné le premier plan à cette nouvelle instance : on le cède à la première.
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Exit();
            return;
        }

        _panel = new PanelWindow();
        _panel.StateChanged += UpdateTray;
        CreateTray();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                _panel.DispatcherQueue.TryEnqueue(() => _panel.ShowPanel());
        }) { IsBackground = true, Name = "MaPasserelle-Signal" };
        listener.Start();

        // Lancé au démarrage de Windows (--tray) : rester discret dans la zone de notification.
        if (!Environment.GetCommandLineArgs().Contains("--tray")) _panel.ShowPanel();
    }

    private void CreateTray()
    {
        _shareItem = new MenuFlyoutItem { Text = "Partager", Command = new Relay(() => _ = _panel!.ToggleSharingAsync()) };
        var menu = new MenuFlyout();
        menu.Items.Add(_shareItem);
        menu.Items.Add(new MenuFlyoutItem { Text = "Ouvrir Ma Passerelle", Command = new Relay(() => _panel!.ShowPanel()) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quitter", Command = new Relay(() => _ = QuitAsync()) });

        _tray = new TaskbarIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "ma-passerelle.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            LeftClickCommand = new Relay(() => _panel!.TogglePanel()),
        };
        _tray.ForceCreate(false);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _panel is null || _shareItem is null) return;
        _tray.ToolTipText = _panel.IsSharing ? $"Ma Passerelle — partage sur {_panel.SharingTarget}" : "Ma Passerelle";
        _shareItem.Text = _panel.IsSharing ? "Arrêter le partage" : "Partager";
    }

    /// <summary>Arrête un partage en cours (le téléviseur reçoit l'ordre d'arrêt), puis quitte.</summary>
    public async Task QuitAsync()
    {
        if (_panel is not null) await _panel.ShutdownAsync();
        _tray?.Dispose();
        _showSignal?.Dispose();
        _instance?.ReleaseMutex();
        Exit();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    private sealed class Relay(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
