using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;

namespace NetControl.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private ObservableCollection<AdapterInfo> _adapters = new();
    [ObservableProperty] private AdapterInfo _selectedAdapter;
    [ObservableProperty] private string _routerTestResult = "";
    [ObservableProperty] private string _upnpTestResult = "";
    [ObservableProperty] private string _arpStatus = "";
    [ObservableProperty] private string _ouiInfo = "";
    [ObservableProperty] private bool _savedMessageVisible;

    public AppSettings S => AppServices.Settings.Settings;

    public ObservableCollection<string> Themes { get; } = new() { "Dark", "Light" };
    public ObservableCollection<DeviceCategory> CategoryList { get; } =
        new(Enum.GetValues<DeviceCategory>());

    /// <summary>Raised with the new theme name - the WPF layer swaps the resource dictionary.</summary>
    public event Action<string> ThemeChangeRequested;

    public SettingsViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        LoadAdapters();
        OuiInfo = $"Vendor database: {AppServices.Oui.EntryCount:N0} manufacturer entries (IEEE OUI)";
        ArpStatus = AppServices.Arp.GetStatusText() + " | " +
                    (NetworkHelper.IsAdmin() ? "Administrator: yes" : "Administrator: no");
    }

    public void LoadAdapters()
    {
        var list = NetworkHelper.GetAdapters();
        _sync.Post(_ =>
        {
            Adapters.Clear();
            foreach (var a in list) Adapters.Add(a);
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Mac == S.SelectedAdapterMac)
                              ?? Adapters.FirstOrDefault();
        }, null);
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedAdapter != null)
            S.SelectedAdapterMac = SelectedAdapter.Mac;

        AppServices.Settings.Save();
        AppServices.Router.ReloadSettings(AppServices.Settings.Settings);
        AppServices.Scan.RestartAutoTimer();
        SavedMessageVisible = true;
    }

    [RelayCommand]
    private void ApplyScanSettings()
    {
        Save();
        AppServices.Scan.RestartAutoTimer();
    }

    [RelayCommand]
    private void ChangeTheme(string theme)
    {
        S.Theme = theme;
        ThemeChangeRequested?.Invoke(theme);
        Save();
    }

    [RelayCommand]
    private async Task TestRouterAsync()
    {
        RouterTestResult = "Testing connection...";
        Save();
        var status = await AppServices.Router.TestAsync();
        RouterTestResult = $"{(status.Connected ? "SUCCESS" : "FAILED")} - {status.Message}";
    }

    [RelayCommand]
    private async Task TestUpnpAsync()
    {
        UpnpTestResult = "Searching for UPnP gateway...";
        var status = await AppServices.Upnp.GetStatusAsync();
        UpnpTestResult = $"{(status.Connected ? "FOUND" : "NOT FOUND")} - {status.Message}";
    }

    [RelayCommand]
    private void RescanAdapter() => LoadAdapters();

    [RelayCommand]
    private void PruneHistoryNow()
    {
        AppServices.Db.PruneHistory(S.HistoryRetentionDays);
        ArpStatus = $"History pruned to entries newer than {S.HistoryRetentionDays} days.";
    }

    [RelayCommand]
    private void ToggleArpEnforcement()
    {
        S.Arp.EnforcementEnabled = !S.Arp.EnforcementEnabled;
        Save();
        ArpStatus = AppServices.Arp.GetStatusText();
        if (S.Arp.EnforcementEnabled && AppServices.Arp.NpcapAvailable && AppServices.Scan.CurrentAdapter != null)
        {
            AppServices.Arp.BindAdapter(AppServices.Scan.CurrentAdapter);
            if (AppServices.Arp.IsBound)
            {
                AppServices.Arp.LearnGatewayMac(AppServices.Scan.GatewayIp);
                if (S.Arp.PassiveDetection) AppServices.Arp.StartMonitoring();
            }
        }
        else if (!S.Arp.EnforcementEnabled)
        {
            _ = AppServices.Arp.StopAllSpoofing();
        }
    }
}
