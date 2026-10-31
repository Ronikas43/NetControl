using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// Orchestrates scanning: adapter selection, auto-refresh timer, manual rescan,
/// gateway learning and ARP engine wiring. All results flow through DeviceService.
/// </summary>
public class ScanManager : IDisposable
{
    private readonly NetworkScanner _scanner = new();
    private readonly DeviceService _devices;
    private readonly SettingsService _settings;
    private readonly ArpService _arp;
    private readonly TrafficMonitor _traffic;
    private System.Timers.Timer _autoTimer;
    private CancellationTokenSource _scanCts;
    private readonly object _scanGate = new();
    private bool _scanning;

    public AdapterInfo CurrentAdapter { get; private set; }
    public string GatewayIp { get; private set; } = "";

    public event Action<string> StatusChanged;
    public event Action<bool> ScanStateChanged;   // true = scanning started, false = finished
    public event Action<AdapterInfo> AdapterChanged;

    public NetworkScanner Scanner => _scanner;

    public ScanManager(DeviceService devices, SettingsService settings, ArpService arp, TrafficMonitor traffic)
    {
        _devices = devices;
        _settings = settings;
        _arp = arp;
        _traffic = traffic;

        _scanner.ScanFailed += m => { StatusChanged?.Invoke("Scan failed: " + m); ScanStateChanged?.Invoke(false); };
        _scanner.StatusChanged += s => StatusChanged?.Invoke(s);
    }

    public void Start()
    {
        RefreshAdapters();
        RestartAutoTimer();
    }

    public void RestartAutoTimer()
    {
        _autoTimer?.Dispose();
        _autoTimer = null;
        var s = _settings.Settings;
        if (!s.AutoScan) return;
        int interval = Math.Clamp(s.ScanIntervalSeconds, 5, 3600) * 1000;
        _autoTimer = new System.Timers.Timer(interval) { AutoReset = true };
        _autoTimer.Elapsed += async (_, _) => await ScanNowAsync(triggeredByAuto: true);
        _autoTimer.Start();
    }

    public void RefreshAdapters()
    {
        var adapters = NetworkHelper.GetAdapters();
        var wanted = _settings.Settings.SelectedAdapterMac;
        var pick = adapters.FirstOrDefault(a => a.Mac == wanted)
                   ?? adapters.FirstOrDefault(a => !string.IsNullOrEmpty(a.Gateway))
                   ?? adapters.FirstOrDefault();

        SetAdapter(pick, adapters.FirstOrDefault(a => !string.IsNullOrEmpty(a.Gateway))?.Gateway ?? "");
    }

    public void SetAdapter(AdapterInfo adapter, string gatewayIp = null)
    {
        var previous = CurrentAdapter;
        CurrentAdapter = adapter;
        if (adapter == null)
        {
            GatewayIp = "";
            StatusChanged?.Invoke("No active network adapter found.");
            return;
        }

        // Gateway: explicit hint or first ARP cache entry hint; learned precisely during scans.
        GatewayIp = gatewayIp ?? previous?.Gateway ?? "";
        if (string.IsNullOrEmpty(GatewayIp) && adapter.Gateway.Length > 0)
            GatewayIp = adapter.Gateway;

        _traffic.SetAdapter(adapter);
        if (_arp.NpcapAvailable)
        {
            _arp.BindAdapter(adapter);
            if (_arp.IsBound)
            {
                _arp.LearnGatewayMac(GatewayIp);
                if (_settings.Settings.Arp.PassiveDetection)
                    _arp.StartMonitoring();
            }
        }

        // Flag local + gateway devices.
        var localMac = NetworkHelper.NormalizeMac(adapter.Mac);
        foreach (var d in _devices.Devices)
        {
            d.IsLocal = !string.IsNullOrEmpty(localMac) && d.Mac == localMac;
            d.IsGateway = !string.IsNullOrEmpty(GatewayIp) && d.Ip == GatewayIp;
        }

        StatusChanged?.Invoke($"Adapter: {adapter.Name} ({adapter.Type}) - subnet {adapter.Subnet}");
        AdapterChanged?.Invoke(adapter);
    }

    public async Task ScanNowAsync(bool triggeredByAuto = false)
    {
        if (CurrentAdapter == null) RefreshAdapters();
        if (CurrentAdapter == null) return;

        lock (_scanGate)
        {
            if (_scanning) return;
            _scanning = true;
            _scanCts = new CancellationTokenSource();
        }

        ScanStateChanged?.Invoke(true);
        try
        {
            // Start service discovery (mDNS/SSDP/WS-Discovery) in parallel with the scan
            // so it costs no extra wall-clock time.
            var s = _settings.Settings;
            Task<Dictionary<string, DiscoveryInfo>> discoTask = null;
            if (s.DetectMdns || s.DetectSsdp || s.DetectWsd)
            {
                var svc = new DiscoveryService();
                bool mdns = s.DetectMdns, ssdp = s.DetectSsdp, wsd = s.DetectWsd;
                discoTask = Task.Run(() => svc.DiscoverAllAsync(mdns, ssdp, wsd, 3000));
            }

            var results = await _scanner.ScanAsync(CurrentAdapter, _settings.Settings, _scanCts.Token);

            // NetCut-style: always show "This computer" and the gateway in the list,
            // even when neither answers ARP/ping in this pass.
            var localIp = CurrentAdapter.Ips.FirstOrDefault() ?? "";
            var localMac = NetworkHelper.NormalizeMac(CurrentAdapter.Mac);
            if (!string.IsNullOrEmpty(localIp) && !results.Any(r => r.Ip == localIp))
            {
                results.Add(new ScanResult
                {
                    Ip = localIp,
                    Mac = localMac,
                    Hostname = Environment.MachineName,
                    NameSource = "Local",
                    Pingable = true,
                    ArpResolved = true
                });
            }

            // Learn/refresh gateway identity.
            var gwCandidate = results.FirstOrDefault(r => r.Ip == CurrentAdapter.Gateway)?.Ip
                              ?? CurrentAdapter.Gateway;
            if (!string.IsNullOrEmpty(gwCandidate)) GatewayIp = gwCandidate;
            if (_arp.NpcapAvailable) _arp.LearnGatewayMac(GatewayIp);

            if (!string.IsNullOrEmpty(GatewayIp) && !results.Any(r => r.Ip == GatewayIp))
            {
                var gwMac = NetworkHelper.ResolveMac(GatewayIp);
                results.Add(new ScanResult
                {
                    Ip = GatewayIp,
                    Mac = gwMac ?? "",
                    Pingable = true,
                    ArpResolved = !string.IsNullOrEmpty(gwMac)
                });
            }

            _devices.ApplyScanResults(results, GatewayIp, localMac);

            // Identity enrichment: DHCP lease names, mDNS/SSDP/WS-Discovery evidence,
            // device type, OS fingerprint and confidence level.
            if (discoTask != null)
            {
                var infoByIp = await discoTask.ConfigureAwait(false);
                await EnrichIdentitiesAsync(results, infoByIp).ConfigureAwait(false);
            }

            // Ensure ARP enforcement matches Blocked flags after every scan.
            await SyncEnforcementWithFlagsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Scan cancelled.");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Scan error: {ex.Message}");
        }
        finally
        {
            lock (_scanGate) _scanning = false;
            ScanStateChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// Runs after a scan: merges DHCP lease hostnames, applies mDNS/SSDP/WS-Discovery
    /// evidence, then classifies every device (type, OS, model, confidence).
    /// </summary>
    private async Task EnrichIdentitiesAsync(List<ScanResult> results, Dictionary<string, DiscoveryInfo> infoByIp)
    {
        try
        {
            var s = _settings.Settings;

            // 1) DHCP lease hostnames from the router (helps where NetBIOS/mDNS are silent).
            if (s.UseDhcpLeases)
            {
                var leases = await AppServices.Router.GetDhcpLeasesAsync().ConfigureAwait(false);
                foreach (var (mac, _, hostname) in leases)
                {
                    if (string.IsNullOrEmpty(hostname)) continue;
                    var d = _devices.FindByMac(mac);
                    if (d == null) continue;
                    // Priority NetBIOS > DHCP > DNS: replace weaker names only.
                    if (string.IsNullOrEmpty(d.Hostname) || d.NameSource is "" or "DNS")
                    {
                        d.Hostname = hostname;
                        d.NameSource = "DHCP";
                    }
                }
            }

            // 2) Apply discovery evidence + classification.
            foreach (var r in results)
            {
                var d = _devices.FindByMac(r.Mac) ?? _devices.FindByIp(r.Ip);
                if (d == null) continue;

                infoByIp.TryGetValue(r.Ip, out var info);
                if (info != null)
                {
                    // Discovery hostname (e.g. "<name>.local") fills gaps only.
                    if (!string.IsNullOrEmpty(info.Hostname) && string.IsNullOrEmpty(d.Hostname))
                    {
                        d.Hostname = info.Hostname;
                        d.NameSource = "mDNS";
                    }
                    if (!string.IsNullOrEmpty(info.FriendlyName) && string.IsNullOrEmpty(d.CustomName) &&
                        !string.Equals(d.FriendlyNameSeen, info.FriendlyName, StringComparison.Ordinal))
                    {
                        d.FriendlyNameSeen = info.FriendlyName; // shown in details, never overrides user naming
                    }
                }

                var cls = DeviceClassifier.Classify(d, info, r.Ttl);
                _devices.ApplyIdentity(d, cls);
            }
        }
        catch { }
    }

    /// <summary>After each scan, make sure devices flagged Blocked are actually being enforced.</summary>
    public async Task SyncEnforcementWithFlagsAsync()
    {
        if (!_settings.Settings.Arp.EnforcementEnabled) return;
        if (!_arp.CanEnforce(null)) return;
        foreach (var d in _devices.Devices.Where(d => d.Blocked && !d.IsLocal && !d.IsGateway))
        {
            if (_arp.SpoofedMacs.All(m => !m.Equals(d.Mac, StringComparison.OrdinalIgnoreCase)))
                _arp.StartSpoofing(d.Mac, d.Ip);
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _autoTimer?.Dispose();
        try { _scanCts?.Cancel(); } catch { }
    }
}
