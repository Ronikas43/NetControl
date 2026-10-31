using System;

namespace NetControl.Models;

public class HistoryEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Type { get; set; } = "";     // Connected, Disconnected, IpChanged, Blocked, Unblocked, Bandwidth, Schedule, Manual, Security
    public string Message { get; set; } = "";
}

public class SecurityEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Severity { get; set; } = "Info";   // Info, Warning, Critical
    public string Type { get; set; } = "";           // NewDevice, UnknownDevice, ArpSpoof, ArpChange, OperationFailed
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Message { get; set; } = "";
}

public class Schedule
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";            // device MAC or "*" for all managed devices
    public string Action { get; set; } = "Block";    // Block | Allow
    public bool[] Days { get; set; } = new bool[7];  // Sunday..Saturday
    public int StartMinutes { get; set; }            // minutes from midnight
    public int EndMinutes { get; set; }
    public bool Enabled { get; set; } = true;
    public bool CurrentlyApplied { get; set; }
    public string LastResult { get; set; } = "";

    public string DaysText
    {
        get
        {
            string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            if (Days.All(d => d)) return "Every day";
            if (Days[1] && Days[2] && Days[3] && Days[4] && Days[5] && !Days[0] && !Days[6]) return "Weekdays";
            if (Days[0] && Days[6] && !Days[1] && !Days[2] && !Days[3] && !Days[4] && !Days[5]) return "Weekends";
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 7; i++) if (Days[i]) parts.Add(names[i]);
            return string.Join(", ", parts);
        }
    }

    public string WindowText => $"{MinutesToText(StartMinutes)} - {MinutesToText(EndMinutes)}";
    public string StateText => !Enabled ? "Disabled" : (CurrentlyApplied ? "Applied" : "Scheduled");

    public static string MinutesToText(int m) => $"{m / 60:00}:{m % 60:00}";
    public static int TextToMinutes(string s)
    {
        var p = s.Split(':');
        return int.Parse(p[0]) * 60 + int.Parse(p[1]);
    }
}
