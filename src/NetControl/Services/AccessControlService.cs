using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

public class AccessResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Method { get; set; } = "";   // "Router (OpenWrt)" / "ARP enforcement" / "None"
}

/// <summary>
/// Composite internet-access enforcer. Priority:
///   1. OpenWrt router firewall rules (no admin needed, most reliable).
///   2. ARP enforcement (Npcap + admin, NetCut-style) when the user enabled it.
/// If neither path is available the operation FAILS with an honest message - it never
/// pretends that blocking succeeded.
/// </summary>
public class AccessControlService
{
    private readonly RouterService _router;
    private readonly ArpService _arp;
    private readonly SettingsService _settings;
    private readonly DeviceService _devices;

    public event Action<string, string> OperationFailed;   // (context, reason)

    public AccessControlService(RouterService router, ArpService arp, SettingsService settings, DeviceService devices)
    {
        _router = router;
        _arp = arp;
        _settings = settings;
        _devices = devices;
    }

    public string EnforcementSummary
    {
        get
        {
            var parts = new List<string>();
            if (_router.LastStatus.Connected && _router.LastStatus.SupportsBlock)
                parts.Add("Router (OpenWrt): ready");
            else if (_settings.Settings.Router.Enabled)
                parts.Add("Router (OpenWrt): not connected");
            else
                parts.Add("Router: not configured");

            if (_settings.Settings.Arp.EnforcementEnabled)
                parts.Add(_arp.CanEnforce(null) ? "ARP enforcement: ready" : $"ARP enforcement: {_arp.EnforceBlockStatus}");
            else
                parts.Add("ARP enforcement: off (enable in Settings)");
            return string.Join(" | ", parts);
        }
    }

    public async Task<AccessResult> SetInternetAccessAsync(Device device, bool allow)
    {
        if (device == null || device.IsLocal)
            return new AccessResult { Success = false, Message = "The local machine cannot be blocked from itself.", Method = "None" };

        if (device.IsGateway)
            return new AccessResult { Success = false, Message = "Refusing to block the network gateway.", Method = "None" };

        var s = _settings.Settings;

        // ---- Path 1: OpenWrt firewall ----
        if (s.Router.Enabled && _router.LastStatus.Connected && _router.LastStatus.SupportsBlock)
        {
            var (ok, msg) = allow
                ? await _router.UnblockMacAsync(device.Mac).ConfigureAwait(false)
                : await _router.BlockMacAsync(device.Mac).ConfigureAwait(false);
            if (ok)
            {
                device.Blocked = !allow;
                _devices.SetNotes(device, device.Notes); // no-op persist trigger
                _devices.LogHistory(new HistoryEvent
                {
                    Mac = device.Mac, Ip = device.Ip, Type = allow ? "Unblocked" : "Blocked",
                    Message = $"{device.DisplayName}: internet {(allow ? "restored" : "revoked")} via router firewall"
                });
                return new AccessResult { Success = true, Message = msg, Method = "Router (OpenWrt)" };
            }
            OperationFailed?.Invoke(allow ? "Restore access" : "Block access", msg);
            return new AccessResult { Success = false, Message = msg, Method = "Router (OpenWrt)" };
        }

        // ---- Path 2: ARP enforcement ----
        if (s.Arp.EnforcementEnabled)
        {
            if (!_arp.CanEnforce(device.Mac))
            {
                var msg = $"ARP enforcement unavailable: {_arp.EnforceBlockStatus}";
                OperationFailed?.Invoke(allow ? "Restore access" : "Block access", msg);
                return new AccessResult { Success = false, Message = msg, Method = "ARP enforcement" };
            }
            bool ok = allow
                ? _arp.StopSpoofing(device.Mac, device.Ip)
                : _arp.StartSpoofing(device.Mac, device.Ip);
            if (ok)
            {
                device.Blocked = !allow;
                _devices.LogHistory(new HistoryEvent
                {
                    Mac = device.Mac, Ip = device.Ip, Type = allow ? "Unblocked" : "Blocked",
                    Message = $"{device.DisplayName}: internet {(allow ? "restored" : "revoked")} via ARP enforcement"
                });
                return new AccessResult
                {
                    Success = true,
                    Message = allow
                        ? $"{device.DisplayName}: ARP caches repaired, internet restored."
                        : $"{device.DisplayName}: internet revoked via ARP enforcement (spoofing active at {_arp.ActiveSpoofCount} device(s)).",
                    Method = "ARP enforcement"
                };
            }
            OperationFailed?.Invoke(allow ? "Restore access" : "Block access", _arp.LastError);
            return new AccessResult { Success = false, Message = _arp.LastError, Method = "ARP enforcement" };
        }

        // ---- No path available: honest failure ----
        var reason = "No enforcement method available. Configure an OpenWrt router in Settings, " +
                     "or enable ARP enforcement (requires Npcap + Administrator). " +
                     "Device discovery and monitoring still work without router access.";
        OperationFailed?.Invoke(allow ? "Restore access" : "Block access", reason);
        return new AccessResult { Success = false, Message = reason, Method = "None" };
    }

    public async Task<AccessResult> RestoreAllAsync()
    {
        var blocked = _devices.Devices.Where(d => d.Blocked).ToList();
        var messages = new List<string>();
        bool allOk = true;
        foreach (var d in blocked)
        {
            var r = await SetInternetAccessAsync(d, true).ConfigureAwait(false);
            if (!r.Success) allOk = false;
            messages.Add($"{d.DisplayName}: {(r.Success ? "restored" : "FAILED")}");
        }
        return new AccessResult
        {
            Success = allOk,
            Message = blocked.Count == 0
                ? "No managed devices are currently blocked."
                : string.Join("; ", messages)
        };
    }

    /// <summary>
    /// Bandwidth limits: only meaningful through the router (OpenWrt SQM) today.
    /// Returns an honest inactive result when unsupported.
    /// </summary>
    public Task<AccessResult> SetBandwidthLimitAsync(Device device, long downKbps, long upKbps)
    {
        device.BandwidthLimited = false;
        if (_router.LastStatus.Connected && _router.LastStatus.SupportsBandwidth)
        {
            // sqm ubus object present - per-MAC limit scripting still depends on router config.
            return Task.FromResult(new AccessResult
            {
                Success = false,
                Message = "SQM detected on the router, but per-MAC limits must be defined in the router's SQM config. See README.",
                Method = "Router (OpenWrt)"
            });
        }
        return Task.FromResult(new AccessResult
        {
            Success = false,
            Message = "Bandwidth limiting requires router support (OpenWrt with SQM/QoS packages). " +
                      "NetControl does not fake this - the limit was NOT applied.",
            Method = "None"
        });
    }

    public Task<AccessResult> RemoveBandwidthLimitAsync(Device device)
    {
        device.BandwidthLimited = false;
        return Task.FromResult(new AccessResult
        {
            Success = true,
            Message = device.BandwidthLimited
                ? "Limit removed."
                : "No active limit through NetControl (limits require OpenWrt SQM support).",
            Method = "None"
        });
    }
}
