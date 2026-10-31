using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;

namespace NetControl.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _typeFilter = "All";
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _deviceFilter = "";    // MAC or empty
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private Device _selectedDevice;

    public ObservableCollection<HistoryEvent> Entries { get; } = new();
    public ObservableCollection<HistoryEvent> DeviceHistory { get; } = new();
    public ObservableCollection<string> Types { get; } = new()
    {
        "All", "Connected", "Disconnected", "IpChanged", "Blocked", "Unblocked", "Schedule", "Manual", "Security"
    };

    public HistoryViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        Reload();
    }

    public void Reload()
    {
        try
        {
            var mac = DeviceFilter;
            var list = AppServices.Db.QueryHistory(SearchText, TypeFilter,
                string.IsNullOrWhiteSpace(mac) ? "*" : mac, FromDate, ToDate, 1000);
            _sync.Post(_ =>
            {
                Entries.Clear();
                foreach (var e in list) Entries.Add(e);
                ResultCount = Entries.Count;
            }, null);
        }
        catch { }
    }

    public void ShowDeviceHistory(Device d)
    {
        if (d == null) return;
        DeviceFilter = d.Mac;
        try
        {
            var list = AppServices.Db.QueryHistory("", "All", d.Mac, null, null, 500);
            _sync.Post(_ =>
            {
                DeviceHistory.Clear();
                foreach (var e in list) DeviceHistory.Add(e);
            }, null);
        }
        catch { }
    }

    partial void OnTypeFilterChanged(string value) => Reload();

    [RelayCommand]
    private void Search() => Reload();

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        TypeFilter = "All";
        FromDate = null;
        ToDate = null;
        DeviceFilter = "";
        DeviceHistory.Clear();
        Reload();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        AppServices.Db.ClearHistory();
        Reload();
        DeviceHistory.Clear();
    }
}
