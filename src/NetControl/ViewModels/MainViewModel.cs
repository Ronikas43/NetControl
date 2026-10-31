using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Services;
using NetControl.Utilities;

namespace NetControl.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    public DashboardViewModel Dashboard { get; }
    public DevicesViewModel DevicesPage { get; }
    public BandwidthViewModel BandwidthPage { get; }
    public HistoryViewModel HistoryPage { get; }
    public SchedulerViewModel SchedulerPage { get; }
    public SecurityViewModel SecurityPage { get; }
    public MacViewModel MacPage { get; }
    public SettingsViewModel SettingsPage { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new();

    [ObservableProperty]
    private ObservableObject _currentViewModel;

    [ObservableProperty]
    private NavItem _selectedNav;

    [ObservableProperty]
    private string _scanStatus = "Starting...";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _adapterText = "No adapter";

    [ObservableProperty]
    private string _speedText = "↓ 0 B/s   ↑ 0 B/s";

    [ObservableProperty]
    private string _routerStatusText = "Router: not configured";

    [ObservableProperty]
    private string _arpStatusText = "ARP: off";

    // In-app banner
    [ObservableProperty]
    private bool _bannerVisible;

    [ObservableProperty]
    private string _bannerText = "";

    [ObservableProperty]
    private string _bannerSeverity = "Info";

    public string Title => "NetControl";

    public MainViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();

        Dashboard = new DashboardViewModel();
        DevicesPage = new DevicesViewModel();
        BandwidthPage = new BandwidthViewModel();
        HistoryPage = new HistoryViewModel();
        SchedulerPage = new SchedulerViewModel();
        SecurityPage = new SecurityViewModel();
        MacPage = new MacViewModel();
        SettingsPage = new SettingsViewModel();

        NavItems.Add(new NavItem("\uE80F", "Dashboard", NavTarget.Dashboard));
        NavItems.Add(new NavItem("\uE716", "Devices", NavTarget.Devices));
        NavItems.Add(new NavItem("\uE8D0", "Bandwidth", NavTarget.Bandwidth));
        NavItems.Add(new NavItem("\uE81C", "History", NavTarget.History));
        NavItems.Add(new NavItem("\uE823", "Scheduler", NavTarget.Scheduler));
        NavItems.Add(new NavItem("\uEA18", "Security", NavTarget.Security));
        NavItems.Add(new NavItem("\uE8F1", "MAC Tools", NavTarget.Mac));
        NavItems.Add(new NavItem("\uE713", "Settings", NavTarget.Settings));
        _selectedNav = NavItems[0];
        _currentViewModel = Dashboard;

        // Live status bar
        AppServices.Scan.StatusChanged += s => _sync.Post(_ => ScanStatus = s, null);
        AppServices.Scan.ScanStateChanged += scanning => _sync.Post(_ => IsScanning = scanning, null);
        AppServices.Scan.AdapterChanged += a => _sync.Post(_ =>
        {
            if (a != null)
                AdapterText = $"{a.Name} ({a.Type}) - {a.Subnet}";
            else
                AdapterText = "No adapter";
        }, null);
        AppServices.Traffic.SampleTaken += () => _sync.Post(_ =>
        {
            var (up, down) = AppServices.Traffic.CurrentSpeeds();
            SpeedText = $"↓ {Format.Speed(down)}   ↑ {Format.Speed(up)}";
            Dashboard.OnSample();
        }, null);
        AppServices.Router.LastStatusChanged += () => _sync.Post(_ =>
        {
            var st = AppServices.Router.LastStatus;
            RouterStatusText = st.Connected
                ? $"Router: connected ({st.Kind})"
                : "Router: not connected";
            ArpStatusText = $"ARP: {(AppServices.Settings.Settings.Arp.EnforcementEnabled ? "on" : "off")}" +
                            (AppServices.Arp.NpcapAvailable ? "" : " (Npcap missing)");
        }, null);

        AppServices.Notifications.BannerRaised += (title, msg, sev) => _sync.Post(_ =>
        {
            BannerSeverity = sev;
            BannerText = $"{title}: {msg}";
            BannerVisible = true;
        }, null);

        // First paint of router status
        var st0 = AppServices.Router.LastStatus;
        RouterStatusText = st0.Connected ? $"Router: connected ({st0.Kind})" : "Router: not connected";
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value == null) return;
        CurrentViewModel = value.Target switch
        {
            NavTarget.Dashboard => Dashboard,
            NavTarget.Devices => DevicesPage,
            NavTarget.Bandwidth => BandwidthPage,
            NavTarget.History => HistoryPage,
            NavTarget.Scheduler => SchedulerPage,
            NavTarget.Security => SecurityPage,
            NavTarget.Mac => MacPage,
            NavTarget.Settings => SettingsPage,
            _ => Dashboard
        };
    }

    [RelayCommand]
    private void Navigate(NavItem item) => SelectedNav = item;

    [RelayCommand]
    private void CloseBanner() => BannerVisible = false;
}

public enum NavTarget { Dashboard, Devices, Bandwidth, History, Scheduler, Security, Mac, Settings }

public class NavItem
{
    public string Glyph { get; }
    public string Label { get; }
    public NavTarget Target { get; }
    public NavItem(string glyph, string label, NavTarget target)
    {
        Glyph = glyph;
        Label = label;
        Target = target;
    }
}
