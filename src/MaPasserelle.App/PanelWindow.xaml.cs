// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using MaPasserelle.Capture;
using MaPasserelle.Engine;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;

namespace MaPasserelle.App;

/// <summary>Un téléviseur dans la liste.</summary>
public sealed class DeviceItem(AirPlayDevice device, bool paired)
{
    public AirPlayDevice Device { get; } = device;
    public bool Paired { get; set; } = paired;
    public string Label => Paired ? Device.Name : Device.Name + " (à jumeler)";
}

/// <summary>Un écran ou une fenêtre à partager.</summary>
public sealed class SourceItem(string label, CaptureKind kind, nint handle)
{
    public string Label { get; } = label;
    public CaptureKind Kind { get; } = kind;
    public nint Handle { get; } = handle;
}

/// <summary>
/// Le panneau de Ma Passerelle, ouvert depuis l'icône de la barre des tâches. Il se place au-dessus
/// de la zone de notification et se masque dès qu'on clique ailleurs, comme les panneaux de Windows.
/// </summary>
public sealed partial class PanelWindow : Window
{
    private const int WidthDip = 380, HeightDip = 600;

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
    private CancellationTokenSource? _search;

    /// <summary>L'état a changé (partage démarré ou arrêté) : l'icône met à jour son infobulle et son menu.</summary>
    public event Action? StateChanged;

    public bool IsSharing => _mirroring is not null;
    public string? SharingTarget => _mirroring?.Device.Name;

    public PanelWindow()
    {
        InitializeComponent();
        DeviceBox.ItemsSource = _devices;
        SourceBox.ItemsSource = _sources;

        QualityBox.SelectedIndex = _settings.MaxHeight switch { 720 => 1, 1080 => 2, 2160 => 3, _ => 0 };
        FpsBox.SelectedIndex = _settings.Fps >= 60 ? 1 : 0;
        AudioSwitch.IsOn = _settings.Audio;
        CursorSwitch.IsOn = _settings.Cursor;
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
        PlaceNearTray();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (!IsSharing) RefreshSources();
    }

    public void HidePanel() => AppWindow.Hide();

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
    // le premier plan au panneau, il n'a jamais eu le focus : il reste affiché.
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            if (GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this)) _hadFocus = true;
            return;
        }
        if (!_hadFocus) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(150);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid != (uint)Environment.ProcessId && !_busy) HidePanel();
        });
    }

    private void OnHide(object sender, RoutedEventArgs e) => HidePanel();

    private void OnQuit(object sender, RoutedEventArgs e) => _ = ((App)Application.Current).QuitAsync();

    // ------------------------------------------------------------------ téléviseurs

    private void OnSearch(object sender, RoutedEventArgs e) => _ = SearchAsync();

    /// <summary>Le dernier téléviseur utilisé d'abord (question directe à son adresse), puis la recherche réseau.</summary>
    private async Task SearchAsync()
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        SearchButton.IsEnabled = false;
        if (_devices.Count == 0) DeviceBox.PlaceholderText = "Recherche des téléviseurs…";
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
                SearchButton.IsEnabled = true;
                if (_devices.Count == 0)
                    DeviceBox.PlaceholderText = "Aucun téléviseur trouvé : essayer l'adresse IP";
            }
        }
    }

    private DeviceItem AddDevice(AirPlayDevice device, bool paired)
    {
        var existing = _devices.FirstOrDefault(d => d.Device.DeviceId == device.DeviceId);
        if (existing is not null) return existing;
        var item = new DeviceItem(device, paired);
        _devices.Add(item);
        // Choix par défaut : le dernier téléviseur utilisé, sinon un téléviseur déjà jumelé, sinon le premier.
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
            ShareButton.IsEnabled = item is { Paired: true } && !_busy;
        }
    }

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
                Show(InfoBarSeverity.Warning, "Pas de réponse", $"Aucun téléviseur compatible ne répond à l'adresse {address}.");
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
        PairText.Text = "Ce téléviseur n'est pas encore jumelé. Le jumelage se fait une seule fois.";
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

    // ------------------------------------------------------------------ sources

    private void OnKindChanged(object sender, RoutedEventArgs e) => RefreshSources();

    private void OnSourceOpened(object? sender, object e) => RefreshSources(keepSelection: true);

    private void RefreshSources(bool keepSelection = false)
    {
        if (SourceBox is null) return; // pendant InitializeComponent
        var previous = (SourceBox.SelectedItem as SourceItem)?.Handle;
        _sources.Clear();
        if (WindowChoice.IsChecked == true)
        {
            foreach (var w in DesktopCatalog.Windows())
                _sources.Add(new SourceItem(string.IsNullOrEmpty(w.Process) ? w.Title : $"{w.Title} — {w.Process}", CaptureKind.Window, w.Handle));
        }
        else
        {
            foreach (var s in DesktopCatalog.Screens())
                _sources.Add(new SourceItem($"Écran {s.Index} — {s.Width} × {s.Height}" + (s.Primary ? " (principal)" : ""), CaptureKind.Screen, s.Handle));
        }
        SourceBox.SelectedItem = (keepSelection ? _sources.FirstOrDefault(s => s.Handle == previous) : null) ?? _sources.FirstOrDefault();
    }

    // ------------------------------------------------------------------ réglages

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.MaxHeight = int.Parse((string)((ComboBoxItem)QualityBox.SelectedItem).Tag);
        _settings.Fps = int.Parse((string)((ComboBoxItem)FpsBox.SelectedItem).Tag);
        _settings.Audio = AudioSwitch.IsOn;
        _settings.Cursor = CursorSwitch.IsOn;
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
        if (SelectedDevice is not { Paired: true } item)
        {
            ShowPanel();
            return;
        }
        if (SourceBox.SelectedItem is not SourceItem source)
        {
            Show(InfoBarSeverity.Warning, "Rien à partager", "Choisir un écran ou une fenêtre.");
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
        DeviceBox.IsEnabled = SearchButton.IsEnabled = ShowAddressButton.IsEnabled = SourceBox.IsEnabled = ScreenChoice.IsEnabled = WindowChoice.IsEnabled = false;
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
        DeviceBox.IsEnabled = SearchButton.IsEnabled = ShowAddressButton.IsEnabled = SourceBox.IsEnabled = ScreenChoice.IsEnabled = WindowChoice.IsEnabled = true;
        StateText.Text = "Prêt";
        UpdateDeviceState();
        StateChanged?.Invoke();

        switch (end.Reason)
        {
            case "disconnected":
                Show(InfoBarSeverity.Informational, "Partage terminé", "Le téléviseur a mis fin au partage (veille, extinction ou autre source).");
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
        ShareButton.IsEnabled = !busy && (IsSharing || SelectedDevice is { Paired: true });
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
