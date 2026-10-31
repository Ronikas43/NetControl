using System;

namespace NetControl.Services;

/// <summary>
/// Composition root: builds and wires the service graph once for the whole app.
/// </summary>
public static class AppServices
{
    public static Database Db { get; private set; }
    public static SettingsService Settings { get; private set; }
    public static OuiService Oui { get; private set; }
    public static DeviceService Devices { get; private set; }
    public static ArpService Arp { get; private set; }
    public static RouterService Router { get; private set; }
    public static UpnpService Upnp { get; private set; }
    public static AccessControlService Access { get; private set; }
    public static TrafficMonitor Traffic { get; private set; }
    public static SchedulerService Scheduler { get; private set; }
    public static SecurityService Security { get; private set; }
    public static NotificationService Notifications { get; private set; }
    public static MacService Mac { get; private set; }
    public static ScanManager Scan { get; private set; }

    public static void Build()
    {
        Db = new Database();
        Settings = new SettingsService();
        Settings.Load();
        Oui = new OuiService();
        Devices = new DeviceService(Db, Oui, Settings);
        Arp = new ArpService();
        Router = new RouterService(Settings.Settings);
        Upnp = new UpnpService();
        Traffic = new TrafficMonitor();
        Notifications = new NotificationService(Settings);
        Access = new AccessControlService(Router, Arp, Settings, Devices);
        Scheduler = new SchedulerService(Db, Access, Devices, Settings);
        Security = new SecurityService(Db, Devices, Arp, Settings);
        Mac = new MacService();
        Scan = new ScanManager(Devices, Settings, Arp, Traffic);

        WireNotifications();
    }

    private static void WireNotifications()
    {
        Devices.NewDeviceDiscovered += d =>
        {
            if (!d.Trusted)
            {
                Notifications.NotifyUnknownDevice(d);
                var ev = new Models.SecurityEvent
                {
                    Severity = "Warning",
                    Type = "UnknownDevice",
                    Mac = d.Mac,
                    Ip = d.Ip,
                    Message = $"Unknown device on the network: {d.DisplayName} ({d.Ip})"
                };
                Security.RaiseManual(ev);
            }
            else
            {
                Notifications.NotifyNewDevice(d);
            }
        };
        Devices.IpMacConflict += (ip, oldMac, newMac) =>
        {
            var ev = new Models.SecurityEvent
            {
                Severity = "Critical",
                Type = "ArpChange",
                Mac = newMac,
                Ip = ip,
                Message = $"Suspicious IP-to-MAC change: {ip} was owned by {oldMac} and is now answered by {newMac}. " +
                          "This can be a DHCP lease moving to new hardware - or ARP spoofing."
            };
            Security.RaiseManual(ev);
            Notifications.NotifyArpAlert(ev.Message);
        };
        Devices.DeviceWentOffline += d => Notifications.NotifyDeviceOffline(d);
        Security.ToastNotification += (t, m) => Notifications.ShowSecurityToast(t, m);
        Access.OperationFailed += (ctx, reason) => Notifications.NotifyFailure(ctx, reason);
        Scheduler.ScheduleExecuted += (name, result) => Notifications.NotifySchedule(name, result);
    }
}
