// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MaPasserelle.Capture;
using MaPasserelle.Engine;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;

namespace MaPasserelle.App;

/// <summary>Un appareil de projection dans la liste.</summary>
public sealed class DeviceItem(AirPlayDevice device, bool paired)
{
    public AirPlayDevice Device { get; } = device;
    public bool Paired { get; set; } = paired;
    public string Label => Paired ? Device.Name : Device.Name + " (à jumeler)";
}

/// <summary>Un écran ou une fenêtre à partager, avec sa miniature (chargée après coup).</summary>
public sealed class SourceItem(string label, string detail, CaptureKind kind, nint handle) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public string Label { get; } = label;
    public string Detail { get; } = detail;
    public CaptureKind Kind { get; } = kind;
    public nint Handle { get; } = handle;
    public string Glyph => Kind == CaptureKind.Screen ? "" : "";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new(nameof(PlaceholderVisibility)));
        }
    }

    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Nom lu par les lecteurs d'écran et l'automatisation.
    public override string ToString() => Label;
}

/// <summary>
/// Le panneau de Ma Passerelle, ouvert depuis l'icône de la barre des tâches. Il se place au-dessus
/// de la zone de notification et se masque dès qu'on clique ailleurs, comme les panneaux de Windows.
/// </summary>
public sealed partial class PanelWindow : Window
{
    private const int WidthDip = 420, HeightDip = 660;
    private const int ThumbWidth = 340, ThumbHeight = 192; // deux fois la taille affichée, pour les écrans à haute densité

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<DeviceItem> _devices = [];
    private readonly ObservableCollection<SourceItem> _sources = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private Mirroring? _mirroring;
    private ReceiverPairing.Handle? _pairing;
    private DeviceItem? _pairingDevice;
    private bool _loading = true;
    private bool _busy;
    private bool _hadFocus;
    private long _shownAt;
    private int _sourcesVersion;
    private CancellationTokenSource? _search;

    /// <summary>L'état a changé (partage démarré ou arrêté) : l'icône met à jour son infobulle et son menu.</summary>
    public event Action? StateChanged;

    public bool IsSharing => _mirroring is not null;
    public string? SharingTarget => _mirroring?.Device.Name;

    public PanelWindow()
    {
        InitializeComponent();
        DeviceBox.ItemsSource = _devices;
        SourceGrid.ItemsSource = _sources;
        SourceGrid.SelectionChanged += (_, _) => UpdateDeviceState();
        CancelPairing();

        (_settings.MaxHeight switch { 720 => Quality720, 2160 => Quality2160, _ => Quality1080 }).IsChecked = true;
        (_settings.Fps >= 60 ? Fps60 : Fps30).IsChecked = true;
        AudioSwitch.IsOn = _settings.Audio;
        CursorSwitch.IsOn = _settings.Cursor;
        Startup.Refresh();
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "";
        VersionText.Text = "Version " + version;
        _loading = false;

        // Fenêtre de type panneau : pas de barre de titre, ni dans la barre des tâches ni dans Alt+Tab.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ma-passerelle.ico"));
        SystemBackdrop = new DesktopAcrylicBackdrop();
        // Bordure neutre, comme les panneaux de Windows, même si l'accent colore les bordures.
        Root.ActualThemeChanged += (_, _) => SetBorderColor();
        SetBorderColor();
        AppWindow.Closing += (_, e) => { e.Cancel = true; HidePanel(); };
        Activated += OnActivated;

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ShowSharingState();

        RefreshSources();
        _ = SearchAsync();
    }

    // ------------------------------------------------------------------ affichage du panneau

    public void ShowPanel()
    {
        _hadFocus = false;
        _shownAt = Environment.TickCount64;
        PlaceNearTray();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (!IsSharing) RefreshSources();
    }

    public void HidePanel()
    {
        AppWindow.Hide();
        OnCloseSettings(this, new RoutedEventArgs());
    }

    public void TogglePanel()
    {
        if (AppWindow.IsVisible) HidePanel();
        else ShowPanel();
    }

    /// <summary>En bas à droite de l'écran principal, au-dessus de la barre des tâches.</summary>
    private void PlaceNearTray()
    {
        var area = DisplayArea.Primary.WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int width = (int)(WidthDip * scale), height = Math.Min((int)(HeightDip * scale), area.Height - 24);
        int margin = (int)(12 * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - width - margin, area.Y + area.Height - height - margin, width, height));
    }

    // Se masque quand on clique ailleurs. Les listes déroulantes s'ouvrent dans leur propre fenêtre :
    // on ne se masque donc que si le premier plan appartient à un autre programme. Si Windows a refusé
    // le premier plan au panneau, il n'a jamais eu le focus : il reste affiché. Juste après l'ouverture,
    // le premier plan peut encore passer d'un programme à l'autre (relance de l'application) : on l'ignore.
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            if (GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this)) _hadFocus = true;
            return;
        }
        if (!_hadFocus || Environment.TickCount64 - _shownAt < 600) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(150);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid != (uint)Environment.ProcessId && !_busy) HidePanel();
        });
    }

    private void OnHide(object sender, RoutedEventArgs e) => HidePanel();

    private void OnQuit(object sender, RoutedEventArgs e) => _ = ((App)Application.Current).QuitAsync();

    // ------------------------------------------------------------------ appareils

    private void OnSearch(object sender, RoutedEventArgs e) => _ = SearchAsync();

    /// <summary>Le dernier appareil utilisé d'abord (question directe à son adresse), puis la recherche réseau.</summary>
    private async Task SearchAsync()
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        SearchButton.IsEnabled = false;
        if (_devices.Count == 0) DeviceBox.PlaceholderText = "Recherche des appareils…";
        try
        {
            var credentials = new Credentials();
            if (_settings.DeviceAddress is { } last && System.Net.IPAddress.TryParse(last, out var address))
            {
                if (await Devices.AskAsync(address, _settings.DevicePort, search.Token) is { } known)
                    AddDevice(known, credentials.IsPaired(known.DeviceId));
            }
            foreach (var device in await Devices.DiscoverAsync(TimeSpan.FromSeconds(4), search.Token))
                if (device.IsMirroringCandidate && Devices.Ipv4(device) is not null)
                    AddDevice(device, credentials.IsPaired(device.DeviceId));
        }
        finally
        {
            if (search == _search)
            {
                SearchButton.IsEnabled = !IsSharing;
                if (_devices.Count == 0)
                    DeviceBox.PlaceholderText = "Aucun appareil trouvé, essayer l'adresse IP";
            }
        }
    }

    private DeviceItem AddDevice(AirPlayDevice device, bool paired)
    {
        var existing = _devices.FirstOrDefault(d => d.Device.DeviceId == device.DeviceId);
        if (existing is not null) return existing;
        var item = new DeviceItem(device, paired);
        _devices.Add(item);
        // Choix par défaut : le dernier appareil utilisé, sinon un appareil déjà jumelé, sinon le premier.
        bool isLast = _settings.DeviceId == device.DeviceId;
        bool lastSelected = SelectedDevice?.Device.DeviceId == _settings.DeviceId;
        if (SelectedDevice is null || isLast || (!lastSelected && paired && !SelectedDevice.Paired))
            DeviceBox.SelectedItem = item;
        return item;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelPairing();
        UpdateDeviceState();
    }

    private DeviceItem? SelectedDevice => DeviceBox.SelectedItem as DeviceItem;

    private void UpdateDeviceState()
    {
        var item = SelectedDevice;
        PairPanel.Visibility = item is { Paired: false } ? Visibility.Visible : Visibility.Collapsed;
        if (!IsSharing)
        {
            SetShareText(item is null ? "Partager" : "Partager sur " + item.Device.Name);
            ShareButton.IsEnabled = CanShare;
        }
    }

    private bool CanShare => !_busy && SelectedDevice is { Paired: true } && SourceGrid.SelectedItem is SourceItem;

    private void OnShowAddress(object sender, RoutedEventArgs e)
    {
        AddressPanel.Visibility = Visibility.Visible;
        ShowAddressButton.Visibility = Visibility.Collapsed;
        AddressBox.Focus(FocusState.Programmatic);
    }

    private void OnAddressKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; OnAddAddress(sender, e); }
    }

    private async void OnAddAddress(object sender, RoutedEventArgs e)
    {
        string text = AddressBox.Text.Trim();
        if (!Devices.TryParseAddress(text, out var address, out int port))
        {
            Show(InfoBarSeverity.Warning, "Adresse non valable", "Saisir une adresse comme 192.168.1.20.");
            return;
        }
        Busy(true);
        try
        {
            var device = await Devices.AskAsync(address, port);
            if (device is null)
            {
                Show(InfoBarSeverity.Warning, "Pas de réponse", $"Aucun appareil compatible ne répond à l'adresse {address}.");
                return;
            }
            var item = AddDevice(device, new Credentials().IsPaired(device.DeviceId));
            DeviceBox.SelectedItem = item;
            AddressBox.Text = "";
            AddressPanel.Visibility = Visibility.Collapsed;
            ShowAddressButton.Visibility = Visibility.Visible;
            Message.IsOpen = false;
        }
        finally { Busy(false); }
    }

    // ------------------------------------------------------------------ jumelage

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } item || Devices.Ipv4(item.Device) is not { } address) return;
        Busy(true);
        try
        {
            _pairing = await ReceiverPairing.BeginAsync(address, item.Device.AirPlayPort ?? Devices.DefaultPort, CancellationToken.None);
            _pairingDevice = item;
            PairText.Text = $"Saisir le code qui s'affiche maintenant sur « {item.Device.Name} ».";
            PairButton.Visibility = Visibility.Collapsed;
            PinPanel.Visibility = Visibility.Visible;
            PinBox.Text = "";
            PinBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Jumelage impossible", ex.Message);
        }
        finally { Busy(false); }
    }

    private void OnPinKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; OnPinConfirm(sender, e); }
    }

    private async void OnPinConfirm(object sender, RoutedEventArgs e)
    {
        if (_pairing is not { } pairing || _pairingDevice is not { } item) return;
        string pin = PinBox.Text.Trim();
        if (pin.Length == 0) return;
        Busy(true);
        try
        {
            var credentials = await pairing.FinishAsync(pin, CancellationToken.None);
            new Credentials().Save(item.Device.DeviceId, credentials);
            item.Paired = true;
            RefreshDeviceLabels();
            CancelPairing();
            Show(InfoBarSeverity.Success, "Jumelé", $"« {item.Device.Name} » est prêt pour le partage.");
        }
        catch (HapPairingException)
        {
            Show(InfoBarSeverity.Error, "Code refusé", "Le code ne correspond pas. Relancer le jumelage pour obtenir un nouveau code.");
            CancelPairing();
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Jumelage impossible", ex.Message);
            CancelPairing();
        }
        finally
        {
            Busy(false);
            UpdateDeviceState();
        }
    }

    private void CancelPairing()
    {
        _pairing?.Dispose();
        _pairing = null;
        _pairingDevice = null;
        PairText.Text = "Cet appareil n'est pas encore jumelé. Le jumelage se fait une seule fois.";
        PairButton.Visibility = Visibility.Visible;
        PinPanel.Visibility = Visibility.Collapsed;
    }

    // La liste affiche Label : on remplace l'élément pour que le libellé se mette à jour.
    private void RefreshDeviceLabels()
    {
        var selected = SelectedDevice;
        for (int i = 0; i < _devices.Count; i++)
        {
            var item = _devices[i];
            _devices[i] = new DeviceItem(item.Device, item.Paired);
            if (item == selected) DeviceBox.SelectedIndex = i;
        }
    }

    // ------------------------------------------------------------------ écrans et fenêtres

    private void OnKindChanged(object sender, RoutedEventArgs e) => RefreshSources();

    /// <summary>Remplit la grille, puis charge les miniatures en arrière-plan.</summary>
    private void RefreshSources()
    {
        if (SourceGrid is null) return; // pendant InitializeComponent
        var previous = (SourceGrid.SelectedItem as SourceItem)?.Handle;
        int version = ++_sourcesVersion;
        _sources.Clear();

        bool wantWindows = WindowChoice.IsChecked == true;
        IReadOnlyList<ScreenInfo> screens = wantWindows ? [] : DesktopCatalog.Screens();
        IReadOnlyList<WindowInfo> windows = wantWindows ? DesktopCatalog.Windows() : [];
        foreach (var s in screens)
            _sources.Add(new SourceItem($"Écran {s.Index}" + (s.Primary ? " (principal)" : ""), $"{s.Width} × {s.Height}", CaptureKind.Screen, s.Handle));
        foreach (var w in windows)
            _sources.Add(new SourceItem(w.Title, w.Minimized ? w.Process + " · réduite" : w.Process, CaptureKind.Window, w.Handle));
        SourceGrid.SelectedItem = _sources.FirstOrDefault(s => s.Handle == previous) ?? _sources.FirstOrDefault();
        UpdateDeviceState();

        var items = _sources.ToList();
        _ = Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (version != _sourcesVersion) return;
                var pixels = item.Kind == CaptureKind.Screen
                    ? screens.FirstOrDefault(s => s.Handle == item.Handle) is { } screen ? Thumbnails.OfScreen(screen, ThumbWidth, ThumbHeight) : null
                    : Thumbnails.OfWindow(item.Handle, ThumbWidth, ThumbHeight);
                if (pixels is null) continue;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (version != _sourcesVersion) return;
                    using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8,
                        pixels.Width, pixels.Height, BitmapAlphaMode.Premultiplied);
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(bitmap);
                    item.Thumbnail = source;
                });
            }
        });
    }

    // ------------------------------------------------------------------ réglages

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        ShowStartupStatus(await Startup.GetAsync());
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
    }

    private void OnQualityChecked(object sender, RoutedEventArgs e) =>
        SaveSetting(() => _settings.MaxHeight = int.Parse((string)((RadioButton)sender).Tag));

    private void OnFpsChecked(object sender, RoutedEventArgs e) =>
        SaveSetting(() => _settings.Fps = int.Parse((string)((RadioButton)sender).Tag));

    private void OnSwitchToggled(object sender, RoutedEventArgs e) => SaveSetting(() =>
    {
        _settings.Audio = AudioSwitch.IsOn;
        _settings.Cursor = CursorSwitch.IsOn;
    });

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Startup.Status status;
        try { status = await Startup.SetAsync(StartupSwitch.IsOn); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            status = await Startup.GetAsync();
        }
        ShowStartupStatus(status);
    }

    /// <summary>Affiche l'état réel, que Windows peut imposer (désactivé par l'utilisateur ou par l'établissement).</summary>
    private void ShowStartupStatus(Startup.Status status)
    {
        _loading = true;
        StartupSwitch.IsOn = status == Startup.Status.On;
        _loading = false;
        StartupSwitch.IsEnabled = status is Startup.Status.On or Startup.Status.Off;
        StartupHint.Text = status switch
        {
            Startup.Status.BlockedByUser => "Désactivé dans Paramètres Windows → Applications → Démarrage. Le réactiver là-bas.",
            Startup.Status.ByPolicy => "Réglé par l'administrateur de ce PC.",
            _ => "",
        };
        StartupHint.Visibility = StartupHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveSetting(Action change)
    {
        if (_loading) return;
        change();
        _settings.Save();
    }

    // ------------------------------------------------------------------ partage

    private void OnShare(object sender, RoutedEventArgs e) => _ = ToggleSharingAsync();

    public async Task ToggleSharingAsync()
    {
        if (IsSharing) await StopSharingAsync();
        else await StartSharingAsync();
    }

    public async Task StartSharingAsync()
    {
        if (IsSharing || _busy) return;
        if (SelectedDevice is not { Paired: true } item || SourceGrid.SelectedItem is not SourceItem source)
        {
            ShowPanel();
            return;
        }
        var credentials = new Credentials().Load(item.Device.DeviceId);
        if (credentials is null)
        {
            item.Paired = false;
            RefreshDeviceLabels();
            return;
        }

        var options = new MirrorOptions
        {
            Kind = source.Kind,
            Handle = source.Handle,
            MaxHeight = _settings.MaxHeight,
            Fps = _settings.Fps,
            Cursor = _settings.Cursor,
        };
        var mirroring = new Mirroring(item.Device, credentials, options, _settings.Audio);
        Busy(true);
        Message.IsOpen = false;
        StateText.Text = $"Connexion à {item.Device.Name}…";
        try
        {
            await mirroring.StartAsync();
        }
        catch (Exception ex)
        {
            await mirroring.DisposeAsync();
            StateText.Text = "Prêt";
            Show(InfoBarSeverity.Error, "Connexion impossible", $"{item.Device.Name} ne répond pas. Vérifier qu'il est allumé et sur le même réseau. ({ex.Message})");
            return;
        }
        finally { Busy(false); }

        _mirroring = mirroring;
        _settings.DeviceId = item.Device.DeviceId;
        _settings.DeviceName = item.Device.Name;
        _settings.DeviceAddress = Devices.Ipv4(item.Device)?.ToString();
        _settings.DevicePort = item.Device.AirPlayPort ?? Devices.DefaultPort;
        _settings.Save();

        ShareIcon.Glyph = "";
        SetShareText("Arrêter le partage");
        SetChoicesEnabled(false);
        ShowSharingState();
        _clock.Start();
        StateChanged?.Invoke();

        var end = await mirroring.Completion;
        await EndSharingAsync(mirroring, end);
    }

    public Task StopSharingAsync()
    {
        _mirroring?.Stop("command");
        return Task.CompletedTask;
    }

    private async Task EndSharingAsync(Mirroring mirroring, MirrorEnd end)
    {
        _clock.Stop();
        await mirroring.DisposeAsync();
        _mirroring = null;
        ShareIcon.Glyph = "";
        SetChoicesEnabled(true);
        StateText.Text = "Prêt";
        StateChanged?.Invoke();
        RefreshSources();

        switch (end.Reason)
        {
            case "disconnected":
                Show(InfoBarSeverity.Informational, "Partage terminé", "L'appareil a mis fin au partage (veille, extinction ou autre source).");
                break;
            case "source_closed":
                Show(InfoBarSeverity.Informational, "Partage terminé", "La fenêtre partagée a été fermée.");
                break;
            case "stalled":
                Show(InfoBarSeverity.Warning, "Partage interrompu", "Plus aucune image ne passait. Vérifier le réseau, puis relancer.");
                break;
            case "capture_failed":
                Show(InfoBarSeverity.Error, "Partage interrompu", end.Message ?? "La capture a échoué.");
                break;
        }
    }

    private void SetChoicesEnabled(bool enabled)
    {
        DeviceBox.IsEnabled = SearchButton.IsEnabled = ShowAddressButton.IsEnabled = enabled;
        SourceGrid.IsEnabled = ScreenChoice.IsEnabled = WindowChoice.IsEnabled = enabled;
    }

    private void ShowSharingState()
    {
        if (_mirroring is not { } m) return;
        var t = m.Elapsed;
        string duration = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        StateText.Text = $"Partage en cours sur {m.Device.Name} · {duration}";
    }

    // ------------------------------------------------------------------ outils

    private void SetShareText(string text)
    {
        ShareText.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShareButton, text);
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        ShareButton.IsEnabled = IsSharing ? !busy : CanShare;
        PairButton.IsEnabled = !busy;
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }

    /// <summary>Arrête un partage en cours (à la fermeture du programme).</summary>
    public async Task ShutdownAsync()
    {
        _search?.Cancel();
        CancelPairing();
        if (_mirroring is { } m)
        {
            _clock.Stop();
            _mirroring = null;
            await m.DisposeAsync();
        }
    }

    private void SetBorderColor()
    {
        uint color = Root.ActualTheme == ElementTheme.Light ? 0x00D0D0D0u : 0x00404040u; // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 34 /* DWMWA_BORDER_COLOR */, ref color, sizeof(uint));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
