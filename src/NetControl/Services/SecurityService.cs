using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// Security monitoring: unknown/new device classification, ARP spoof detection
/// (passive + periodic active gateway probes) and the security event log.
/// </summary>
public class SecurityService : IDisposable
{
    private readonly Database _db;
    private readonly DeviceService _devices;
    private readonly ArpService _arp;
    private readonly SettingsService _settings;
    private System.Timers.Timer _probeTimer;

    public List<SecurityEvent> Events { get; private set; } = new();

    public event Action<SecurityEvent> SecurityEventRaised;   // raised on captured context
    public event Action EventsReloaded;

    private readonly SynchronizationContext _sync;

    public SecurityService(Database db, DeviceService devices, ArpService arp, SettingsService settings)
    {
        _db = db;
        _devices = devices;
        _arp = arp;
        _settings = settings;
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();

        _devices.NewDeviceDiscovered += OnNewDevice;
        _arp.ArpSpoofDetected += OnArpAlert;
    }

    public void LoadFromDatabase()
    {
        try { Events = _db.LoadSecurityEvents(); } catch { Events = new List<SecurityEvent>(); }
        EventsReloaded?.Invoke();
    }

    private void OnNewDevice(Device d)
    {
        if (d.IsLocal) return;
        var unknown = !d.Trusted;
        var ev = new SecurityEvent
        {
            Timestamp = DateTime.Now,
            Severity = unknown ? "Warning" : "Info",
            Type = unknown ? "UnknownDevice" : "NewDevice",
            Mac = d.Mac,
            Ip = d.Ip,
            Message = unknown
                ? $"Unknown device joined the network: {d.DisplayName} ({d.Ip}, {d.Vendor}). Mark it as trusted if it is yours."
                : $"New device joined: {d.DisplayName} ({d.Ip})"
        };
        Raise(ev, notify: true);
    }

    private void OnArpAlert(ArpAlert alert)
    {
        if (!_settings.Settings.Notifications.ArpAlert) return;
        var ev = new SecurityEvent
        {
            Timestamp = DateTime.Now,
            Severity = "Critical",
            Type = "ArpSpoof",
            Mac = alert.ClaimedMac,
            Ip = alert.SenderIp,
            Message = $"Possible ARP spoofing: {alert.Source} saw {alert.SenderIp} claimed by {alert.ClaimedMac} " +
                      $"but the known gateway MAC is {alert.ExpectedMac}. {alert.Detail}"
        };
        Raise(ev, notify: true);
    }

    public void StartProbing()
    {
        if (_probeTimer != null) return;
        _probeTimer = new System.Timers.Timer(60000) { AutoReset = true };
        _probeTimer.Elapsed += async (_, _) => await ProbeGatewayAsync();
        _probeTimer.Start();
    }

    public async Task ProbeGatewayAsync()
    {
        if (!_settings.Settings.Notifications.ArpAlert) return;
        if (!_arp.IsBound && !_arp.NpcapAvailable)
        {
            // Still possible without Npcap: SendARP-based probe.
            var alert = ProbeWithoutNpcap();
            if (alert != null) OnArpAlert(alert);
            return;
        }
        var a = await Task.Run(() => _arp.ProbeGateway());
        if (a != null) OnArpAlert(a);
    }

    private ArpAlert ProbeWithoutNpcap()
    {
        if (string.IsNullOrEmpty(_arp.GatewayIp)) return null;
        var now = NetworkHelper.ResolveMac(_arp.GatewayIp);
        if (string.IsNullOrEmpty(now)) return null;
        now = NetworkHelper.NormalizeMac(now);
        var expected = NetworkHelper.NormalizeMac(_arp.GatewayMac);
        if (string.IsNullOrEmpty(expected) || now.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            // Learn baseline on first successful probe.
            if (string.IsNullOrEmpty(expected)) _arp.SetGatewayMacBaseline(now);
            return null;
        }
        return new ArpAlert
        {
            SenderIp = _arp.GatewayIp,
            ClaimedMac = now,
            ExpectedMac = expected,
            Source = "Active probe (no Npcap)",
            Detail = "Gateway MAC differs from the learned baseline"
        };
    }

    public void RaiseManual(SecurityEvent ev) => Raise(ev, notify: false);

    private void Raise(SecurityEvent ev, bool notify)
    {
        try { _db.AddSecurityEvent(ev); } catch { }
        lock (this)
        {
            Events.Insert(0, ev);
            if (Events.Count > 300) Events.RemoveAt(Events.Count - 1);
        }
        _sync.Post(_ =>
        {
            SecurityEventRaised?.Invoke(ev);
            EventsReloaded?.Invoke();
        }, null);

        if (notify && _settings.Settings.Notifications.ToastEnabled)
            ToastNotification?.Invoke(
                ev.Severity == "Critical" ? "NetControl - Security alert" : "NetControl - Security",
                ev.Message);
    }

    /// <summary>Set by the App layer to route security alerts into desktop notifications.</summary>
    public event Action<string, string> ToastNotification;

    public void ClearAll()
    {
        try { _db.ClearHistory(); } catch { }
        Events = new List<SecurityEvent>();
        EventsReloaded?.Invoke();
    }

    public void Dispose() => _probeTimer?.Dispose();
}
