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

public partial class BandwidthViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private string _downloadSpeed = "0 B/s";
    [ObservableProperty] private string _uploadSpeed = "0 B/s";
    [ObservableProperty] private string _sessionDownload = "0 B";
    [ObservableProperty] private string _sessionUpload = "0 B";
    [ObservableProperty] private string _sourceNote =
        "Live speeds are measured from the selected adapter's byte counters. " +
        "Per-device usage requires router support (OpenWrt) - NetControl never fabricates per-device numbers.";
    [ObservableProperty] private Device _selectedDevice;
    [ObservableProperty] private string _limitDownKbps = "";
    [ObservableProperty] private string _limitUpKbps = "";
    [ObservableProperty] private string _limitStatus = "No bandwidth limit is active through NetControl.";
    [ObservableProperty] private string _routerStatus = "";

    public ObservableCollection<Device> Devices => AppServices.Devices.Devices;
    public Services.TrafficMonitor Traffic => AppServices.Traffic;

    public BandwidthViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        AppServices.Traffic.SampleTaken += () => _sync.Post(_ => OnSample(), null);
        AppServices.Router.LastStatusChanged += () => _sync.Post(_ => UpdateRouterStatus(), null);
        UpdateRouterStatus();
    }

    private void OnSample()
    {
        var (up, down) = AppServices.Traffic.CurrentSpeeds();
        DownloadSpeed = Format.Speed(down);
        UploadSpeed = Format.Speed(up);
        SessionDownload = Format.Bytes(AppServices.Traffic.SessionDownloadBytes);
        SessionUpload = Format.Bytes(AppServices.Traffic.SessionUploadBytes);
    }

    private void UpdateRouterStatus()
    {
        var st = AppServices.Router.LastStatus;
        RouterStatus = st.Connected
            ? $"{st.Kind}: {st.Message}"
            : "Router not connected - configure an OpenWrt router in Settings to enable router-based policies.";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ApplyLimitAsync()
    {
        if (SelectedDevice == null)
        {
            LimitStatus = "Select a device first.";
            return;
        }
        long down = long.TryParse(LimitDownKbps, out var d) ? d : 0;
        long up = long.TryParse(LimitUpKbps, out var u) ? u : 0;
        if (down <= 0 && up <= 0)
        {
            LimitStatus = "Enter a download or upload limit in kbps.";
            return;
        }
        var r = await AppServices.Access.SetBandwidthLimitAsync(SelectedDevice, down, up);
        LimitStatus = $"{(r.Success ? "Applied" : "Not applied")} - {r.Message}";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RemoveLimitAsync()
    {
        if (SelectedDevice == null)
        {
            LimitStatus = "Select a device first.";
            return;
        }
        var r = await AppServices.Access.RemoveBandwidthLimitAsync(SelectedDevice);
        LimitStatus = r.Message;
    }
}
