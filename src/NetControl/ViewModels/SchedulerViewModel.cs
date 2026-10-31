using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Models;
using NetControl.Services;

namespace NetControl.ViewModels;

public partial class SchedulerViewModel : ObservableObject
{
    private readonly SynchronizationContext _sync;

    [ObservableProperty] private Schedule _selectedSchedule;
    [ObservableProperty] private Device _selectedDevice;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newAction = "Block";
    [ObservableProperty] private string _newStart = "22:00";
    [ObservableProperty] private string _newEnd = "07:00";
    [ObservableProperty] private bool _day0, _day1, _day2, _day3, _day4, _day5, _day6;
    [ObservableProperty] private string _message = "";

    public ObservableCollection<Schedule> Schedules => new(AppServices.Scheduler.Schedules);
    public ObservableCollection<Device> Devices => AppServices.Devices.Devices;

    public SchedulerViewModel()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        AppServices.Scheduler.SchedulesChanged += () => _sync.Post(_ =>
        {
            OnPropertyChanged(nameof(Schedules));
            Schedules.Clear();
            foreach (var s in AppServices.Scheduler.Schedules) Schedules.Add(s);
        }, null);
    }

    [RelayCommand]
    private void AddSchedule()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            Message = "Give the schedule a name.";
            return;
        }
        if (SelectedDevice == null && NewAction == "Block")
        {
            Message = "Select a device, or choose action 'Allow (restore)'.";
            return;
        }

        int start, end;
        try
        {
            start = Schedule.TextToMinutes(NewStart);
            end = Schedule.TextToMinutes(NewEnd);
        }
        catch
        {
            Message = "Times must be HH:mm (e.g. 22:00).";
            return;
        }

        var days = new[] { Day0, Day1, Day2, Day3, Day4, Day5, Day6 };
        if (days.All(d => !d))
        {
            Message = "Select at least one day of the week.";
            return;
        }

        if (_editingSchedule != null)
        {
            // Update the existing schedule in place.
            var s = _editingSchedule;
            s.Name = NewName.Trim();
            s.Mac = SelectedDevice?.Mac ?? "*";
            s.Action = NewAction;
            s.Days = days;
            s.StartMinutes = start;
            s.EndMinutes = end;
            AppServices.Scheduler.Update(s);
            Message = $"Schedule '{s.Name}' updated ({s.DaysText}, {Schedule.MinutesToText(start)}-{Schedule.MinutesToText(end)}).";
            _editingSchedule = null;
            return;
        }

        var s2 = new Schedule
        {
            Name = NewName.Trim(),
            Mac = SelectedDevice?.Mac ?? "*",
            Action = NewAction,
            Days = days,
            StartMinutes = start,
            EndMinutes = end,
            Enabled = true
        };
        AppServices.Scheduler.Add(s2);
        Message = $"Schedule '{s2.Name}' saved ({s2.DaysText}, {Schedule.MinutesToText(start)}-{Schedule.MinutesToText(end)}).";
        NewName = "";
    }

    private Schedule _editingSchedule;

    [RelayCommand]
    private void EditSchedule(object parameter)
    {
        if (parameter is not Schedule s) return;
        _editingSchedule = s;
        NewName = s.Name;
        NewAction = s.Action;
        NewStart = Schedule.MinutesToText(s.StartMinutes);
        NewEnd = Schedule.MinutesToText(s.EndMinutes);
        Day0 = s.Days[0]; Day1 = s.Days[1]; Day2 = s.Days[2]; Day3 = s.Days[3];
        Day4 = s.Days[4]; Day5 = s.Days[5]; Day6 = s.Days[6];
        SelectedDevice = s.Mac == "*" ? null : AppServices.Devices.FindByMac(s.Mac);
        Message = $"Editing '{s.Name}' - change the values and press 'Save schedule'.";
    }

    [RelayCommand]
    private void DeleteSchedule(object parameter)
    {
        if (parameter is not Schedule s) return;
        if (s.CurrentlyApplied)
        {
            // Revert any active effect before removing.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                s.CurrentlyApplied = false;
                await AppServices.Scheduler.EvaluateAsync();
            });
        }
        AppServices.Scheduler.Delete(s);
        Message = $"Schedule '{s.Name}' deleted.";
    }

    [RelayCommand]
    private void ToggleEnabled(object parameter)
    {
        if (parameter is not Schedule s) return;
        s.Enabled = !s.Enabled;
        AppServices.Scheduler.Update(s);
        Message = $"Schedule '{s.Name}' {(s.Enabled ? "enabled" : "disabled")}.";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task EvaluateNowAsync()
    {
        await AppServices.Scheduler.EvaluateAsync();
        Message = $"Evaluated at {DateTime.Now:HH:mm:ss}. Check the Last result column.";
    }

    [RelayCommand]
    private void UpdateSchedule(object parameter)
    {
        if (parameter is not Schedule s) return;
        AppServices.Scheduler.Update(s);
        Message = $"Schedule '{s.Name}' updated.";
    }
}
