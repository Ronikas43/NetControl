using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;
using NetControl.Utilities;

namespace NetControl.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private int _totalDevices;
    [ObservableProperty] private int _onlineDevices;
    [ObservableProperty] private int _offlineDevices;
    [ObservableProperty] private int _unknownDevices;
    [ObservableProperty] private int _trustedDevices;
    [ObservableProperty] private int _blockedDevices;
    [ObservableProperty] private string _uploadSpeed = "0 B/s";
    [ObservableProperty] private string _downloadSpeed = "0 B/s";
    [ObservableProperty] private string _sessionUpload = "0 B";
    [ObservableProperty] private string _sessionDownload = "0 B";
    [ObservableProperty] private string _adapterInfo = "No adapter";
    [ObservableProperty] private string _enforcementInfo = "";

    public ObservableCollection<HistoryEvent> RecentActivity { get; } = new();
    public ObservableCollection<SecurityEvent> RecentAlerts { get; } = new();
    public ObservableCollection<Device> OnlineList { get; } = new();

    public DashboardViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();

        AppServices.Devices.DeviceChanged += _ => RefreshStats();
        AppServices.Devices.NewDeviceDiscovered += _ => { RefreshStats(); ReloadRecent(); };
        AppServices.Devices.DeviceCameOnline += _ => RefreshStats();
        AppServices.Devices.DeviceWentOffline += _ => { RefreshStats(); ReloadRecent(); };
        AppServices.Security.SecurityEventRaised += _ => ReloadAlerts();
        AppServices.Scan.AdapterChanged += a => _sync.Post(_ =>
            AdapterInfo = a == null ? "No adapter" : $"{a.Type}: {a.Name} @ {a.Subnet}", null);

        RefreshStats();
        ReloadRecent();
        ReloadAlerts();
    }

    public void OnSample()
    {
        var (up, down) = AppServices.Traffic.CurrentSpeeds();
        UploadSpeed = Format.Speed(up);
        DownloadSpeed = Format.Speed(down);
        SessionUpload = Format.Bytes(AppServices.Traffic.SessionUploadBytes);
        SessionDownload = Format.Bytes(AppServices.Traffic.SessionDownloadBytes);
    }

    [RelayCommand]
    public void RefreshStats()
    {
        var devices = AppServices.Devices.Devices.ToList();
        TotalDevices = devices.Count;
        OnlineDevices = devices.Count(d => d.Online);
        OfflineDevices = devices.Count(d => !d.Online && !d.Mac.StartsWith("UNKNOWN-"));
        UnknownDevices = devices.Count(d => !d.Trusted && !d.IsLocal);
        TrustedDevices = devices.Count(d => d.Trusted);
        BlockedDevices = devices.Count(d => d.Blocked);
        EnforcementInfo = AppServices.Access.EnforcementSummary;

        _sync.Post(_ =>
        {
            OnlineList.Clear();
            foreach (var d in devices.Where(d => d.Online).OrderByDescending(d => d.LastSeen).Take(12))
                OnlineList.Add(d);
        }, null);
    }

    [RelayCommand]
    private void Rescan() => _ = AppServices.Scan.ScanNowAsync();

    private void ReloadRecent()
    {
        try
        {
            var items = AppServices.Db.LoadRecentHistory(10);
            _sync.Post(_ =>
            {
                RecentActivity.Clear();
                foreach (var e in items) RecentActivity.Add(e);
            }, null);
        }
        catch { }
    }

    private void ReloadAlerts()
    {
        try
        {
            var items = AppServices.Security.Events.Take(6).ToList();
            _sync.Post(_ =>
            {
                RecentAlerts.Clear();
                foreach (var e in items) RecentAlerts.Add(e);
            }, null);
        }
        catch { }
    }
}
