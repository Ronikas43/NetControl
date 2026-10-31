using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;

namespace NetControl.ViewModels;

public partial class SecurityViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private SecurityEvent _selectedEvent;
    [ObservableProperty] private string _monitorStatus = "";
    [ObservableProperty] private string _gatewayStatus = "";
    [ObservableProperty] private string _adminStatus = "";
    [ObservableProperty] private Device _selectedDevice;

    public ObservableCollection<SecurityEvent> Events => new(AppServices.Security.Events);
    public ObservableCollection<Device> Devices => AppServices.Devices.Devices;

    public SecurityViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        AppServices.Security.EventsReloaded += () => _sync.Post(_ =>
        {
            OnPropertyChanged(nameof(Events));
            Events.Clear();
            foreach (var e in AppServices.Security.Events) Events.Add(e);
        }, null);

        AppServices.Arp.StatusChanged += s => _sync.Post(_ => RefreshStatus(), null);
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        MonitorStatus = AppServices.Arp.GetStatusText();
        GatewayStatus = string.IsNullOrEmpty(AppServices.Arp.GatewayMac)
            ? "Gateway MAC not learned yet (run a scan)."
            : $"Gateway {AppServices.Arp.GatewayIp} = {AppServices.Arp.GatewayMac}";
        AdminStatus = NetworkHelper.IsAdmin()
            ? "Running as Administrator - ARP enforcement available."
            : "Not running as Administrator - ARP enforcement (sending) is unavailable.";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ProbeNowAsync()
    {
        await AppServices.Security.ProbeGatewayAsync();
        RefreshStatus();
    }

    [RelayCommand]
    private void MarkTrusted()
    {
        if (SelectedDevice == null)
        {
            if (SelectedEvent != null && !string.IsNullOrEmpty(SelectedEvent.Mac))
            {
                var d = AppServices.Devices.FindByMac(SelectedEvent.Mac);
                if (d != null)
                {
                    AppServices.Devices.SetTrusted(d, true);
                    return;
                }
            }
            return;
        }
        AppServices.Devices.SetTrusted(SelectedDevice, true);
    }

    [RelayCommand]
    private void ClearEvents() => AppServices.Security.ClearAll();
}
