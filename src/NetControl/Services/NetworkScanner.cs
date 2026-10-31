using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

public class ScanResult
{
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Hostname { get; set; } = "";
    /// <summary>Where the hostname came from: "NetBIOS", "DNS" or "".</summary>
    public string NameSource { get; set; } = "";
    /// <summary>ICMP reply TTL (-1 when not captured) - input for OS fingerprinting.</summary>
    public int Ttl { get; set; } = -1;
    public bool Pingable { get; set; }
    public bool ArpResolved { get; set; }
}

/// <summary>
/// Subnet discovery: ARP sweep (via SendARP) + ICMP ping sweep + reverse-DNS hostnames,
/// merged with the system ARP cache. Works independently of any router access.
/// </summary>
public class NetworkScanner
{
    public event Action<List<ScanResult>> ScanCompleted;
    public event Action<string> ScanFailed;
    public event Action<string> StatusChanged;

    /// <summary>Runs one full scan of the adapter's subnet. Returns the discovered hosts.</summary>
    public async Task<List<ScanResult>> ScanAsync(AdapterInfo adapter, AppSettings settings, CancellationToken ct)
    {
        var results = new List<ScanResult>();
        if (adapter == null || string.IsNullOrEmpty(adapter.Subnet))
        {
            ScanFailed?.Invoke("No active IPv4 network adapter with a valid subnet was found.");
            return results;
        }

        StatusChanged?.Invoke($"Scanning {adapter.Subnet} on {adapter.Name}...");

        var ips = ExpandSubnet(adapter.Subnet);
        if (ips.Count == 0)
        {
            ScanFailed?.Invoke($"Could not expand subnet {adapter.Subnet}.");
            return results;
        }

        int concurrency = Math.Clamp(settings.ScanConcurrency, 8, 256);
        var localIp = adapter.Ips.FirstOrDefault() ?? "";

        // 1) ARP sweep - catches devices that ignore ICMP.
        var arpHits = new ConcurrentDictionary<string, string>();
        await Task.Run(() =>
        {
            Parallel.ForEach(ips, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, ip =>
            {
                if (ip == localIp) return;
                ct.ThrowIfCancellationRequested();
                var mac = NetworkHelper.ResolveMac(ip);
                if (!string.IsNullOrEmpty(mac) && !NetworkHelper.IsBroadcastMac(mac))
                    arpHits[ip] = mac;
            });
        }, ct).ContinueWith(t => { }, ct); // swallow OCE for caller simplicity; ct checked below

        if (ct.IsCancellationRequested) return results;

        // 2) ICMP ping sweep (async, cheap) - TTL kept for OS fingerprinting.
        var pingHits = new ConcurrentDictionary<string, bool>();
        var ttlHits = new ConcurrentDictionary<string, int>();
        var pingTasks = ips.Where(ip => ip != localIp).Select(async ip =>
        {
            var (ok, ttl) = await NetworkHelper.PingDetailAsync(ip, settings.PingTimeoutMs).ConfigureAwait(false);
            if (ok)
            {
                pingHits[ip] = true;
                if (ttl > 0) ttlHits[ip] = ttl;
            }
        }).ToList();
        try { await Task.WhenAll(pingTasks).ConfigureAwait(false); } catch { }

        // 3) Merge with system ARP cache (learns from ordinary traffic).
        var arpTable = NetworkHelper.GetArpTable();
        foreach (var kv in arpTable)
        {
            if (ips.Contains(kv.Key) || kv.Key == localIp)
                arpHits[kv.Key] = kv.Value;
        }

        var knownIps = arpHits.Keys.Union(pingHits.Keys).Where(ip => ip != localIp).Distinct().ToList();

        // 4) Device names: NetBIOS first (real PC names, like NetCut), then reverse-DNS.
        var hostnames = new ConcurrentDictionary<string, string>();
        var nameSources = new ConcurrentDictionary<string, string>();
        if (settings.DetectHostnames && knownIps.Count > 0)
        {
            var nameGate = new SemaphoreSlim(16, 16); // nbtstat spawns a process - cap it
            var hnTasks = knownIps.Select(async ip =>
            {
                var (name, source) = await ResolveDeviceNameAsync(ip, nameGate).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(name))
                {
                    hostnames[ip] = name;
                    nameSources[ip] = source;
                }
            }).ToList();
            try { await Task.WhenAll(hnTasks).ConfigureAwait(false); } catch { }
        }

        foreach (var ip in knownIps)
        {
            arpHits.TryGetValue(ip, out var mac);
            bool pingable = pingHits.TryGetValue(ip, out var p) && p;
            if (string.IsNullOrEmpty(mac) && !pingable) continue;

            results.Add(new ScanResult
            {
                Ip = ip,
                Mac = mac ?? "",
                Hostname = hostnames.TryGetValue(ip, out var h) ? h : "",
                NameSource = nameSources.TryGetValue(ip, out var src) ? src : "",
                Ttl = ttlHits.TryGetValue(ip, out var ttl) ? ttl : -1,
                Pingable = pingable,
                ArpResolved = !string.IsNullOrEmpty(mac)
            });
        }

        StatusChanged?.Invoke($"Scan finished: {results.Count} device(s) found.");
        ScanCompleted?.Invoke(results);
        return results;
    }

    /// <summary>
    /// Full device-name resolution for one IP: NetBIOS name table first (gives the real
    /// Windows/Mac/Samba computer name even without DNS), then reverse-DNS.
    /// Returns the name plus its source ("NetBIOS" or "DNS").
    /// </summary>
    public static async Task<(string Name, string Source)> ResolveDeviceNameAsync(string ip, SemaphoreSlim gate = null)
    {
        bool useGate = gate != null;
        try
        {
            if (useGate) await gate.WaitAsync().ConfigureAwait(false);
            var nb = await ResolveNetBiosNameAsync(ip).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(nb)) return (nb, "NetBIOS");
        }
        finally
        {
            if (useGate) gate.Release();
        }
        var dns = await ResolveHostnameAsync(ip).ConfigureAwait(false);
        return (dns, string.IsNullOrEmpty(dns) ? "" : "DNS");
    }

    /// <summary>
    /// NetBIOS name lookup via "nbtstat -A" - the same technique NetCut uses to show
    /// real PC names. Returns "" on non-Windows, on timeout, or when the target has
    /// no NetBIOS name table (most phones and IoT devices fall back to reverse-DNS).
    /// </summary>
    public static async Task<string> ResolveNetBiosNameAsync(string ip)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(ip)) return "";
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nbtstat",
                    Arguments = $"-A {ip}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                }
            };
            if (!proc.Start()) return "";
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var finished = await Task.WhenAny(outputTask, Task.Delay(3000)).ConfigureAwait(false);
            if (finished != outputTask)
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            var output = outputTask.Result;
            // Matches lines like:  OMARDAHY-7    <00>  UNIQUE      Registered
            var regex = new System.Text.RegularExpressions.Regex(
                @"^\s*(?<name>\S+)\s+<00>\s+UNIQUE\s+Registered",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var m = regex.Match(output);
            if (!m.Success) return "";
            var name = m.Groups["name"].Value.Trim();
            // Skip names that are pure junk (all same char, e.g. ISATAP placeholders).
            if (name.Length == 0 || name.All(ch => ch == name[0])) return "";
            return name;
        }
        catch { return ""; }
    }

    public static async Task<string> ResolveHostnameAsync(string ip)
    {
        try
        {
            var t = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(t, Task.Delay(1200)).ConfigureAwait(false);
            if (done != t) return "";
            var entry = t.Result;
            var name = entry.HostName;
            if (string.IsNullOrEmpty(name)) return "";
            // Skip raw reverse-DNS ISP names that equal the IP.
            if (name.Equals(ip, StringComparison.OrdinalIgnoreCase)) return "";
            return name;
        }
        catch { return ""; }
    }

    public static List<string> ExpandSubnet(string cidr)
    {
        var list = new List<string>();
        try
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) return list;
            if (!IPAddress.TryParse(parts[0], out var baseAddr)) return list;
            int prefix = int.Parse(parts[1]);
            if (prefix < 8 || prefix > 32) return list;

            uint ipU = NetworkHelper.IpToUint(parts[0]);
            uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            uint network = ipU & mask;
            uint broadcast = network | ~mask;
            long count = (long)(broadcast - network) - 1; // exclude network + broadcast
            if (count > 65536) return list;               // sanity cap

            for (uint i = 1; i <= count; i++)
                list.Add(NetworkHelper.UintToIp(network + i));
        }
        catch { }
        return list;
    }
}
