using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;

namespace NetControl.ViewModels;

public partial class MacViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private ObservableCollection<AdapterInfo> _adapters = new();
    [ObservableProperty] private AdapterInfo _selectedAdapter;
    [ObservableProperty] private string _newMac = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _adminNotice = "";
    [ObservableProperty] private Device _selectedDevice;

    public ObservableCollection<Device> Devices => AppServices.Devices.Devices;

    /// <summary>Raised with text to copy - the WPF layer puts it on the clipboard.</summary>
    public event Action<string> CopyToClipboard;

    public MacViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        AdminNotice = NetworkHelper.IsAdmin()
            ? "Running as Administrator: MAC changes are possible."
            : "Not running as Administrator: MAC changes will fail until NetControl is started as Administrator.";
        LoadAdapters();
    }

    public void LoadAdapters()
    {
        var list = NetworkHelper.GetAdapters();
        _sync.Post(_ =>
        {
            Adapters.Clear();
            foreach (var a in list) Adapters.Add(a);
            SelectedAdapter ??= Adapters.FirstOrDefault();
        }, null);
    }

    [RelayCommand]
    private void RefreshAdapters()
    {
        LoadAdapters();
        StatusMessage = $"Found {Adapters.Count} active adapter(s).";
    }

    [RelayCommand]
    private void CopyMac(object parameter)
    {
        string mac = parameter switch
        {
            AdapterInfo a => a.Mac,
            Device d => d.Mac,
            string s => s,
            _ => SelectedAdapter?.Mac ?? ""
        };
        if (!string.IsNullOrEmpty(mac))
        {
            CopyToClipboard?.Invoke(mac);
            StatusMessage = $"Copied {mac}";
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ChangeMacAsync()
    {
        if (SelectedAdapter == null)
        {
            StatusMessage = "Select an adapter first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewMac))
        {
            StatusMessage = "Enter a new MAC address (e.g. 0A:1B:2C:3D:4E:5F).";
            return;
        }

        StatusMessage = "Changing MAC address - the adapter will restart for a few seconds...";
        var (ok, msg, original) = await System.Threading.Tasks.Task.Run(() =>
        {
            var r = AppServices.Mac.ChangeMac(SelectedAdapter, NewMac, out var orig);
            return (r.ok, r.message, orig);
        });

        StatusMessage = $"{(ok ? "Done" : "Failed")}: {msg}";
        LoadAdapters();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RestoreMacAsync()
    {
        if (SelectedAdapter == null)
        {
            StatusMessage = "Select an adapter first.";
            return;
        }
        StatusMessage = "Restoring original MAC address - the adapter will restart for a few seconds...";
        var (ok, msg) = await System.Threading.Tasks.Task.Run(() => AppServices.Mac.RestoreMac(SelectedAdapter));
        StatusMessage = $"{(ok ? "Done" : "Failed")}: {msg}";
        LoadAdapters();
    }

    [RelayCommand]
    private void LookupVendor()
    {
        if (SelectedDevice == null)
        {
            StatusMessage = "Select a device to look up its manufacturer.";
            return;
        }
        var vendor = AppServices.Oui.LookupOrDefault(SelectedDevice.Mac);
        StatusMessage = $"{SelectedDevice.Mac} -> {vendor}";
    }
}
