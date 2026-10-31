using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>Low-level Windows networking helpers (SendARP, adapters, ARP table, admin check).</summary>
public static class NetworkHelper
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int macLen);

    public static bool IsWindows =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsAdmin()
    {
        if (!IsWindows) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static uint IpToUint(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return 0;
        uint r = 0;
        foreach (var p in parts)
            r = (r << 8) | (uint)(byte.Parse(p) & 0xFF);
        return r; // network order for SendARP (big-endian)
    }

    public static string UintToIp(uint ip) =>
        $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    /// <summary>Resolves a MAC address for an IP via the Windows SendARP API. Returns "" on failure.</summary>
    public static string ResolveMac(string ip)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(ip)) return "";
        try
        {
            byte[] mac = new byte[6];
            int len = mac.Length;
            int r = SendARP(IpToUint(ip), 0, mac, ref len);
            if (r != 0 || len != 6) return "";
            return string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        catch { return ""; }
    }

    /// <summary>Quick TCP-free reachability probe using ICMP ping.</summary>
    public static bool Ping(string ip, int timeoutMs)
    {
        try
        {
            using var p = new System.Net.NetworkInformation.Ping();
            var reply = p.Send(ip, timeoutMs);
            return reply != null && reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    public static async System.Threading.Tasks.Task<bool> PingAsync(string ip, int timeoutMs)
    {
        try
        {
            using var p = new System.Net.NetworkInformation.Ping();
            var reply = await p.SendPingAsync(ip, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ping that also returns the reply IP-header TTL, used for OS fingerprinting
    /// (initial TTL 128 = Windows family, 64 = Linux/Android/macOS, 255 = network gear).
    /// Returns (false, -1) when unreachable.
    /// </summary>
    public static async System.Threading.Tasks.Task<(bool ok, int ttl)> PingDetailAsync(string ip, int timeoutMs)
    {
        try
        {
            using var p = new System.Net.NetworkInformation.Ping();
            var reply = await p.SendPingAsync(ip, timeoutMs).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success) return (false, -1);
            int ttl = -1;
            try { ttl = reply.Options?.Ttl ?? -1; } catch { }
            return (true, ttl);
        }
        catch { return (false, -1); }
    }

    /// <summary>Enumerates usable IPv4 adapters (Ethernet / Wi-Fi, up, with a gateway preferred).</summary>
    public static List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                var props = ni.GetIPProperties();
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null) continue;

                var info = new AdapterInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    Mac = FormatMac(ni.GetPhysicalAddress()?.GetAddressBytes()),
                    Ips = props.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()).ToArray(),
                    Gateway = gw?.Address.ToString() ?? "",
                    IsUp = true,
                    Type = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet",
                    Speed = ni.Speed
                };
                try
                {
                    var v4props = ni.GetIPProperties().GetIPv4Properties();
                    info.DhcpEnabled = v4props != null && v4props.IsDhcpEnabled ? "Yes" : "No";
                }
                catch { info.DhcpEnabled = ""; }

                if (v4.IPv4Mask != null && v4.IPv4Mask != IPAddress.Any)
                {
                    uint ipU = IpToUint(v4.Address.ToString());
                    uint maskU = IpToUint(v4.IPv4Mask.ToString());
                    int cidr = Convert.ToString((long)maskU, 2).Count(c => c == '1');
                    info.Subnet = $"{UintToIp(ipU & maskU)}/{cidr}";
                }

                try
                {
                    var dns = props.DnsAddresses
                        .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                        .Select(d => d.ToString());
                    info.DnsServers = string.Join(", ", dns);
                }
                catch { }

                list.Add(info);
            }
        }
        catch { }
        return list;
    }

    public static string FormatMac(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>Parses the output of `arp -a` into an IP → MAC map (normalized XX:XX... format).</summary>
    public static Dictionary<string, string> GetArpTable()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!IsWindows) return map;
        try
        {
            var (output, _) = RunProcess("arp.exe", "-a", 4000);
            if (string.IsNullOrEmpty(output)) return map;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                // Lines look like:  192.168.1.1          a4-2b-b0-11-22-33     dynamic
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!IPAddress.TryParse(parts[0], out var ipAddr)) continue;
                if (ipAddr.AddressFamily != AddressFamily.InterNetwork) continue;
                var mac = parts[1].Replace('-', ':');
                if (mac == "ff:ff:ff:ff:ff:ff" || mac == "00:00:00:00:00:00") continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(mac, @"^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$"))
                    map[parts[0]] = mac.ToUpperInvariant();
            }
        }
        catch { }
        return map;
    }

    /// <summary>Runs a process and captures stdout. Works cross-platform for compile-check purposes.</summary>
    public static (string output, int exitCode) RunProcess(string fileName, string args, int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return ("", -1);
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return (output, -1);
            }
            return (output, p.ExitCode);
        }
        catch
        {
            return ("", -1);
        }
    }

    /// <summary>Normalizes any MAC format to XX:XX:XX:XX:XX:XX uppercase. Returns "" if invalid.</summary>
    public static string NormalizeMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";
        var hex = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return "";
        hex = hex.ToUpperInvariant();
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public static bool IsBroadcastMac(string mac) =>
        string.Equals(mac, "FF:FF:FF:FF:FF:FF", StringComparison.OrdinalIgnoreCase);
}
