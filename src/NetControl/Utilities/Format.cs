using System;

namespace NetControl.Utilities;

public static class Format
{
    public static string Speed(double bytesPerSec)
    {
        if (bytesPerSec < 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024) return $"{bytesPerSec / 1024 / 1024:0.00} MB/s";
        return $"{bytesPerSec / 1024 / 1024 / 1024:0.00} GB/s";
    }

    public static string Bytes(double bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes < 1024) return $"{bytes:0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:0.0} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024 / 1024:0.00} MB";
        return $"{bytes / 1024 / 1024 / 1024:0.00} GB";
    }

    public static string TimeAgo(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 5) return "just now";
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string Mac(string mac) => string.IsNullOrWhiteSpace(mac) ? "-" : mac;
    public static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;
}
