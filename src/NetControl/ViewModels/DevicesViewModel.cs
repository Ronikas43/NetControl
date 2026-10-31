using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;

namespace NetControl.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private Device _selectedDevice;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _filterCategory = "All";
    [ObservableProperty] private string _filterOnline = "All";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Filtered view of the device list (rebuilt on filter or device change).</summary>
    public ObservableCollection<Device> FilteredDevices { get; } = new();

    public List<string> Categories { get; } = new()
        { "All", "Other", "PC", "Phone", "Tablet", "Console", "Router", "IoT", "Printer", "TV" };

    public List<string> OnlineFilters { get; } = new() { "All", "Online", "Offline" };

    /// <summary>Raised with text to copy - the WPF layer puts it on the clipboard.</summary>
    public event Action<string> CopyToClipboard;

    public DevicesViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        AppServices.Devices.DeviceChanged += _ => _sync.Post(_ => RebuildFiltered(), null);
        AppServices.Devices.DeviceCameOnline += _ => _sync.Post(_ => RebuildFiltered(), null);
        AppServices.Devices.DeviceWentOffline += _ => _sync.Post(_ => RebuildFiltered(), null);
        AppServices.Devices.NewDeviceDiscovered += _ => _sync.Post(_ => RebuildFiltered(), null);
    }

    public void RebuildFiltered()
    {
        var q = FilterText?.Trim() ?? "";
        IEnumerable<Device> query = AppServices.Devices.Devices;

        if (FilterCategory != "All")
            query = query.Where(d => d.Category.ToString() == FilterCategory);
        if (FilterOnline == "Online") query = query.Where(d => d.Online);
        if (FilterOnline == "Offline") query = query.Where(d => !d.Online);

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(d =>
                (d.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Ip?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Mac?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Vendor?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Hostname?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Model?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.OsGuess?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.FriendlyNameSeen?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var sorted = query
            .OrderByDescending(d => d.Online)
            .ThenByDescending(d => d.LastSeen)
            .ToList();

        FilteredDevices.Clear();
        foreach (var d in sorted) FilteredDevices.Add(d);

        if (SelectedDevice != null && !sorted.Contains(SelectedDevice))
            SelectedDevice = null;
    }

    partial void OnFilterTextChanged(string value) => RebuildFiltered();
    partial void OnFilterCategoryChanged(string value) => RebuildFiltered();
    partial void OnFilterOnlineChanged(string value) => RebuildFiltered();

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsBusy = true;
        await AppServices.Scan.ScanNowAsync();
        RebuildFiltered();
        IsBusy = false;
    }

    [RelayCommand]
    private async Task BlockDeviceAsync(object parameter)
    {
        foreach (var d in Targets(parameter))
        {
            var r = await AppServices.Access.SetInternetAccessAsync(d, false);
            StatusMessage = r.Success ? $"Blocked via {r.Method}: {r.Message}" : $"FAILED: {r.Message}";
        }
        RebuildFiltered();
    }

    [RelayCommand]
    private async Task UnblockDeviceAsync(object parameter)
    {
        foreach (var d in Targets(parameter))
        {
            var r = await AppServices.Access.SetInternetAccessAsync(d, true);
            StatusMessage = r.Success ? $"Restored via {r.Method}: {r.Message}" : $"FAILED: {r.Message}";
        }
        RebuildFiltered();
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        var r = await AppServices.Access.RestoreAllAsync();
        StatusMessage = $"{(r.Success ? "Done" : "Partial failure")}: {r.Message}";
        RebuildFiltered();
    }

    [RelayCommand]
    private void ToggleTrust(object parameter)
    {
        foreach (var d in Targets(parameter))
            AppServices.Devices.SetTrusted(d, !d.Trusted);
        RebuildFiltered();
    }

    [RelayCommand]
    private void SaveDevice()
    {
        if (SelectedDevice == null) return;
        AppServices.Devices.SetNotes(SelectedDevice, SelectedDevice.Notes);
        StatusMessage = $"Saved details for {SelectedDevice.DisplayName}";
    }

    [RelayCommand]
    private void RenameDevice()
    {
        if (SelectedDevice == null) return;
        AppServices.Devices.Rename(SelectedDevice, SelectedDevice.CustomName);
        RebuildFiltered();
    }

    [RelayCommand]
    private void CopyInfo(object parameter)
    {
        if (parameter is Device d)
        {
            var text = $"Name: {d.DisplayName}{Environment.NewLine}" +
                       $"IP: {d.Ip}{Environment.NewLine}" +
                       $"MAC: {d.Mac}{Environment.NewLine}" +
                       $"Vendor: {d.Vendor}{Environment.NewLine}" +
                       $"Hostname: {d.Hostname}{Environment.NewLine}" +
                       $"Advertised name: {d.FriendlyNameSeen}{Environment.NewLine}" +
                       $"Model: {d.Model}{Environment.NewLine}" +
                       $"OS (estimated): {d.OsGuess}{Environment.NewLine}" +
                       $"Category: {d.Category}{Environment.NewLine}" +
                       $"Confidence: {d.ConfidenceLabel} ({d.Confidence}/100){Environment.NewLine}" +
                       $"Evidence sources: {d.Sources}{Environment.NewLine}" +
                       $"First seen: {d.FirstSeen:yyyy-MM-dd HH:mm}{Environment.NewLine}" +
                       $"Last seen: {d.LastSeen:yyyy-MM-dd HH:mm}";
            CopyToClipboard?.Invoke(text);
            StatusMessage = $"Copied network info of {d.DisplayName}";
        }
        else if (parameter is string s)
        {
            CopyToClipboard?.Invoke(s);
            StatusMessage = "Copied to clipboard";
        }
    }

    [RelayCommand]
    private void MarkOffline(object parameter)
    {
        foreach (var d in Targets(parameter))
            AppServices.Devices.ForceOffline(d);
        RebuildFiltered();
    }

    private static List<Device> Targets(object parameter)
    {
        if (parameter is System.Collections.IList list && list.Count > 0)
            return list.Cast<Device>().ToList();
        return new List<Device>();
    }
}
