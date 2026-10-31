using System;
using Microsoft.Toolkit.Uwp.Notifications;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// Windows 10/11 toast notifications. Requires a notification-capable desktop;
/// every call is guarded so a missing toast support never crashes the app.
/// </summary>
public class NotificationService
{
    private readonly SettingsService _settings;
    public event Action<string, string, string> BannerRaised;   // (title, message, severity) for in-app banners

    public NotificationService(SettingsService settings)
    {
        _settings = settings;
    }

    public void RaiseBanner(string title, string message, string severity = "Info") =>
        BannerRaised?.Invoke(title, message, severity);

    public void NotifyNewDevice(Device d)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.NewDevice) return;
        Show("New device on your network", $"{d.DisplayName} ({d.Ip}) - {d.Vendor}");
    }

    public void NotifyDeviceOffline(Device d)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.DeviceOffline) return;
        Show("Device left the network", $"{d.DisplayName} ({d.Ip}) is offline");
    }

    public void NotifyUnknownDevice(Device d)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.UnknownDevice) return;
        Show("Unknown device detected",
            $"{d.DisplayName} ({d.Ip}, {d.Vendor}) is not trusted. Open NetControl to review it.");
    }

    public void NotifyArpAlert(string message)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.ArpAlert) return;
        Show("Security alert: ARP activity", message);
    }

    public void ShowSecurityToast(string title, string message)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.ArpAlert) return;
        Show(title, message);
    }

    public void NotifySchedule(string name, string result)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.ScheduleActions) return;
        Show($"Schedule '{name}'", result);
    }

    public void NotifyFailure(string context, string reason)
    {
        var n = _settings.Settings.Notifications;
        if (!n.ToastEnabled || !n.OperationFailed) return;
        Show($"Operation failed: {context}", reason);
    }

    private void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message.Length > 200 ? message.Substring(0, 200) + "…" : message)
                .Show();
        }
        catch
        {
            // Toast may be unavailable (unpackaged edge cases). The in-app banner still shows.
        }
    }
}
