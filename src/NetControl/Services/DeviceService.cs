using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// Central in-memory device registry, synchronized with SQLite. Raises change events
/// on the SynchronizationContext captured at construction (i.e. the UI thread).
/// </summary>
public class DeviceService
{
    private readonly Database _db;
    private readonly OuiService _oui;
    private readonly SettingsService _settings;
    private readonly SynchronizationContext _sync;

    public ObservableCollection<Device> Devices { get; } = new();

    public event Action<Device> DeviceCameOnline;      // includes brand-new devices
    public event Action<Device> DeviceWentOffline;
    public event Action<Device> NewDeviceDiscovered;   // first time ever seen
    public event Action<Device> DeviceChanged;
    /// <summary>An IP that previously belonged to MAC A is now answered by MAC B.</summary>
    public event Action<string, string, string> IpMacConflict;   // (ip, oldMac, newMac)

    public DeviceService(Database db, OuiService oui, SettingsService settings)
    {
        _db = db;
        _oui = oui;
        _settings = settings;
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public void LoadFromDatabase()
    {
        var loaded = _db.LoadDevices();
        foreach (var d in loaded)
        {
            d.Online = false; // re-evaluated on first scan
            if (string.IsNullOrEmpty(d.Vendor)) d.Vendor = _oui.LookupOrDefault(d.Mac);
            Devices.Add(d);
        }
    }

    public Device FindByMac(string mac) =>
        Devices.FirstOrDefault(d => string.Equals(d.Mac, mac, StringComparison.OrdinalIgnoreCase));

    public Device FindByIp(string ip) =>
        Devices.FirstOrDefault(d => string.Equals(d.Ip, ip, StringComparison.OrdinalIgnoreCase));

    /// <summary>Merges one scan batch into the registry. Safe to call from any thread.</summary>
    public void ApplyScanResults(List<ScanResult> results, string gatewayIp, string localMac)
    {
        if (results == null) return;

        foreach (var r in results)
        {
            var finalMac = NetworkHelper.NormalizeMac(r.Mac);
            Device d = null;

            if (!string.IsNullOrEmpty(finalMac))
                d = FindByMac(finalMac);
            if (d == null && !string.IsNullOrEmpty(r.Ip))
            {
                // Same IP with a different MAC: the lease moved to new hardware.
                var stale = FindByIp(r.Ip);
                if (stale != null && stale.Online &&
                    !string.Equals(stale.Mac, finalMac, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Online = false;
                    Post(() =>
                    {
                        stale.Online = false;
                        DeviceChanged?.Invoke(stale);
                    });
                    LogHistory(new HistoryEvent { Mac = stale.Mac, Ip = stale.Ip, Type = "Disconnected",
                        Message = $"{stale.DisplayName} replaced by new device at {r.Ip}" });
                    var ip = r.Ip;
                    var oldMac = stale.Mac;
                    var newMac = finalMac;
                    Post(() => IpMacConflict?.Invoke(ip, oldMac, newMac));
                }
            }

            bool isNew = d == null;
            if (isNew)
            {
                d = new Device
                {
                    Mac = string.IsNullOrEmpty(finalMac) ? $"UNKNOWN-{r.Ip}" : finalMac,
                    FirstSeen = DateTime.Now
                };
                Post(() => Devices.Add(d));
            }

            bool wasOnline = d.Online;
            string oldIp = d.Ip;

            if (!string.IsNullOrEmpty(r.Ip)) d.Ip = r.Ip;
            // Prefer stronger name sources: NetBIOS > DNS. Never overwrite a name we
            // already have with an empty result, and never touch the display name.
            if (!string.IsNullOrEmpty(r.Hostname))
            {
                bool better = string.IsNullOrEmpty(d.Hostname)
                    || (r.NameSource == "NetBIOS" && !string.Equals(d.NameSource, "NetBIOS", StringComparison.Ordinal));
                if (better)
                {
                    d.Hostname = r.Hostname;
                    d.NameSource = r.NameSource;
                }
            }
            if (string.IsNullOrEmpty(d.Vendor) && !string.IsNullOrEmpty(finalMac))
                d.Vendor = _oui.LookupOrDefault(finalMac);
            d.LastSeen = DateTime.Now;
            d.Online = true;
            if (string.Equals(d.Mac, localMac, StringComparison.OrdinalIgnoreCase)) d.IsLocal = true;
            if (!string.IsNullOrEmpty(gatewayIp) && d.Ip == gatewayIp) d.IsGateway = true;
            if (!isNew && !d.Known && (DateTime.Now - d.FirstSeen).TotalMinutes > 5) d.Known = true;

            _db.UpsertDevice(d);

            var dev = d;
            Post(() =>
            {
                DeviceChanged?.Invoke(dev);
                if (isNew)
                {
                    NewDeviceDiscovered?.Invoke(dev);
                    DeviceCameOnline?.Invoke(dev);
                }
                else if (!wasOnline)
                {
                    DeviceCameOnline?.Invoke(dev);
                }
            });

            if (isNew)
                LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "Connected",
                    Message = $"New device appeared: {d.DisplayName} ({d.Ip})" });
            else if (!wasOnline)
                LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "Connected",
                    Message = $"{d.DisplayName} came online ({d.Ip})" });
            else if (!string.IsNullOrEmpty(oldIp) && oldIp != d.Ip)
                LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "IpChanged",
                    Message = $"{d.DisplayName} changed IP: {oldIp} -> {d.Ip}" });
        }

        // Mark devices that were not in this scan batch as offline.
        var seenMacs = results
            .Select(r => NetworkHelper.NormalizeMac(r.Mac))
            .Where(m => !string.IsNullOrEmpty(m))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offlineNow = Devices.Where(d => d.Online && !d.IsLocal &&
            (string.IsNullOrEmpty(d.Mac) || d.Mac.StartsWith("UNKNOWN-") || !seenMacs.Contains(d.Mac)))
            .ToList();

        foreach (var d in offlineNow)
        {
            d.Online = false;
            _db.UpsertDevice(d);
            var dev = d;
            Post(() =>
            {
                DeviceChanged?.Invoke(dev);
                DeviceWentOffline?.Invoke(dev);
            });
            LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "Disconnected",
                Message = $"{d.DisplayName} went offline" });
        }
    }

    /// <summary>Manually marks a device offline (user action).</summary>
    public void ForceOffline(Device d)
    {
        if (d == null || !d.Online) return;
        d.Online = false;
        _db.UpsertDevice(d);
        Post(() =>
        {
            DeviceChanged?.Invoke(d);
            DeviceWentOffline?.Invoke(d);
        });
        LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "Disconnected",
            Message = $"{d.DisplayName} marked offline manually" });
    }

    // ----- Management operations (call on UI thread; they persist immediately) -----

    /// <summary>
    /// Applies classifier output (OS guess, model, confidence, sources, auto-category).
    /// Auto-category only fills in when the user has not picked a category themselves.
    /// </summary>
    public void ApplyIdentity(Device d, Classification c)
    {
        if (d == null || c == null) return;
        bool changed = false;

        if (!string.Equals(d.OsGuess, c.OsGuess, StringComparison.Ordinal)) { d.OsGuess = c.OsGuess; changed = true; }
        if (!string.Equals(d.Model, c.Model, StringComparison.Ordinal)) { d.Model = c.Model; changed = true; }
        if (d.Confidence != c.Confidence) { d.Confidence = c.Confidence; changed = true; }
        var src = string.Join(",", c.Sources);
        if (!string.Equals(d.Sources, src, StringComparison.Ordinal)) { d.Sources = src; changed = true; }
        if (c.Type != DeviceCategory.Other && d.Category == DeviceCategory.Other)
        {
            d.Category = c.Type;
            changed = true;
        }
        if (changed)
        {
            _db.UpsertDevice(d);
            Post(() => DeviceChanged?.Invoke(d));
        }
    }

    public void Rename(Device d, string name)
    {
        d.CustomName = name?.Trim() ?? "";
        _db.UpsertDevice(d);
        DeviceChanged?.Invoke(d);
        LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "Manual",
            Message = $"Renamed device to '{d.CustomName}'" });
    }

    public void SetCategory(Device d, DeviceCategory c)
    {
        d.Category = c;
        _db.UpsertDevice(d);
        DeviceChanged?.Invoke(d);
    }

    public void SetTrusted(Device d, bool trusted)
    {
        d.Trusted = trusted;
        if (trusted) d.Known = true;
        _db.UpsertDevice(d);
        DeviceChanged?.Invoke(d);
        LogHistory(new HistoryEvent { Mac = d.Mac, Ip = d.Ip, Type = "Manual",
            Message = $"{d.DisplayName} marked as {(trusted ? "trusted" : "untrusted")}" });
    }

    public void SetNotes(Device d, string notes)
    {
        d.Notes = notes ?? "";
        _db.UpsertDevice(d);
        DeviceChanged?.Invoke(d);
    }

    public void LogHistory(HistoryEvent e)
    {
        e.Timestamp = DateTime.Now;
        try { _db.AddHistory(e); } catch { }
    }

    private void Post(Action a) => _sync.Post(_ => a(), null);
}
