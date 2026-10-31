using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// Evaluates persisted schedules every 20 seconds and applies Block/Allow windows
/// through the AccessControlService. Reverts the action when a window ends.
/// </summary>
public class SchedulerService : IDisposable
{
    private readonly Database _db;
    private readonly AccessControlService _access;
    private readonly DeviceService _devices;
    private readonly SettingsService _settings;
    private System.Timers.Timer _timer;
    private readonly object _gate = new();

    public event Action SchedulesChanged;
    public event Action<string, string> ScheduleExecuted;   // (schedule name, result message)

    public List<Schedule> Schedules { get; private set; } = new();

    public SchedulerService(Database db, AccessControlService access, DeviceService devices, SettingsService settings)
    {
        _db = db;
        _access = access;
        _devices = devices;
        _settings = settings;
    }

    public void Start()
    {
        Reload();
        if (_timer != null) return;
        _timer = new System.Timers.Timer(20000) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await EvaluateAsync();
        _timer.Start();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try { Schedules = _db.LoadSchedules(); } catch { Schedules = new List<Schedule>(); }
        }
        SchedulesChanged?.Invoke();
    }

    public bool IsInWindow(Schedule s, DateTime now)
    {
        bool today = s.Days[(int)now.DayOfWeek];
        int nowMin = now.Hour * 60 + now.Minute;
        bool inWindow;
        if (s.StartMinutes == s.EndMinutes) inWindow = false;
        else if (s.StartMinutes < s.EndMinutes) inWindow = nowMin >= s.StartMinutes && nowMin < s.EndMinutes;
        else inWindow = nowMin >= s.StartMinutes || nowMin < s.EndMinutes; // overnight window
        return today && inWindow;
    }

    public async Task EvaluateAsync()
    {
        lock (_gate) { }
        List<Schedule> snapshot;
        lock (_gate) snapshot = Schedules.ToList();
        if (snapshot.Count == 0) return;

        bool dirty = false;
        foreach (var s in snapshot)
        {
            if (!s.Enabled) continue;

            bool inWindow = IsInWindow(s, DateTime.Now);
            if (inWindow && !s.CurrentlyApplied)
            {
                // Window opened: apply the schedule action.
                var r = await ApplyScheduleActionAsync(s).ConfigureAwait(false);
                s.CurrentlyApplied = r.Success;
                s.LastResult = $"{DateTime.Now:HH:mm} apply: {r.Message}";
                _db.UpdateSchedule(s);
                dirty = true;
                ScheduleExecuted?.Invoke(s.Name, r.Success ? $"Applied: {r.Message}" : $"FAILED: {r.Message}");
            }
            else if (!inWindow && s.CurrentlyApplied)
            {
                // Window closed: revert.
                var r = await RevertScheduleActionAsync(s).ConfigureAwait(false);
                s.CurrentlyApplied = false;
                s.LastResult = $"{DateTime.Now:HH:mm} revert: {r.Message}";
                _db.UpdateSchedule(s);
                dirty = true;
                ScheduleExecuted?.Invoke(s.Name, r.Success ? $"Window ended: {r.Message}" : $"Revert FAILED: {r.Message}");
            }
        }
        if (dirty) SchedulesChanged?.Invoke();
    }

    private async Task<AccessResult> ApplyScheduleActionAsync(Schedule s)
    {
        var targets = ResolveTargets(s.Mac);
        var messages = new List<string>();
        bool ok = true;
        foreach (var d in targets)
        {
            bool wantBlock = s.Action == "Block";
            var r = await _access.SetInternetAccessAsync(d, !wantBlock).ConfigureAwait(false);
            messages.Add($"{d.DisplayName}: {(r.Success ? (wantBlock ? "blocked" : "allowed") : "FAILED")}");
            if (!r.Success) ok = false;
        }
        return new AccessResult
        {
            Success = ok && messages.Count > 0,
            Message = messages.Count == 0 ? "No matching device found" : string.Join("; ", messages)
        };
    }

    private async Task<AccessResult> RevertScheduleActionAsync(Schedule s)
    {
        // Revert means: undo what the window applied.
        // A Block window ends -> restore access. An Allow window ends -> nothing to force
        // (we never re-block devices that the user did not block), so this is a no-op.
        if (s.Action != "Block") return new AccessResult { Success = true, Message = "Nothing to revert" };

        var targets = ResolveTargets(s.Mac);
        var messages = new List<string>();
        foreach (var d in targets.Where(d => d.Blocked))
        {
            var r = await _access.SetInternetAccessAsync(d, true).ConfigureAwait(false);
            messages.Add($"{d.DisplayName}: {(r.Success ? "restored" : "FAILED")}");
        }
        return new AccessResult
        {
            Success = true,
            Message = messages.Count == 0 ? "No devices needed restoring" : string.Join("; ", messages)
        };
    }

    private List<Device> ResolveTargets(string mac)
    {
        if (mac == "*")
            return _devices.Devices.Where(d => !d.IsLocal && !d.IsGateway).ToList();
        var d = _devices.FindByMac(mac);
        return d != null ? new List<Device> { d } : new List<Device>();
    }

    public void Add(Schedule s)
    {
        s.Id = _db.AddSchedule(s);
        Reload();
    }

    public void Update(Schedule s)
    {
        _db.UpdateSchedule(s);
        Reload();
    }

    public void Delete(Schedule s)
    {
        _db.DeleteSchedule(s.Id);
        Reload();
    }

    public void Dispose() => _timer?.Dispose();
}
