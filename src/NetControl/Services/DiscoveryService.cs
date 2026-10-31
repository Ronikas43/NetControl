using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// One device's identity data recovered from mDNS, SSDP or WS-Discovery.
/// Any field may be empty - everything here is optional evidence.
/// </summary>
public class DiscoveryInfo
{
    public string Ip { get; set; } = "";
    public string Hostname { get; set; } = "";      // e.g. "livingroom-tv" (no .local suffix)
    public string Model { get; set; } = "";         // e.g. "Chromecast Ultra", "MacBookAir7,2"
    public string Manufacturer { get; set; } = "";  // e.g. "Google Inc.", "Apple Inc."
    public string FriendlyName { get; set; } = "";  // SSDP friendlyName, if different from hostname
    public DeviceCategory TypeHint { get; set; } = DeviceCategory.Other;
    public string OsHint { get; set; } = "";        // e.g. "Windows", "Android", "macOS"
    /// <summary>Raised protocols that produced evidence: "mDNS", "SSDP", "WS-Discovery".</summary>
    public List<string> Sources { get; } = new();
    /// <summary>Human-readable evidence, e.g. "_ipp._tcp", "upnp:MediaRenderer".</summary>
    public List<string> Evidence { get; } = new();
}

/// <summary>
/// Network-wide service discovery: mDNS/DNS-SD (Bonjour/Chromecast/AirPrint/HomeKit),
/// SSDP/UPnP root devices (TVs, speakers, NAS, printers, routers) and WS-Discovery
/// (Windows PCs, network printers/scanners). All results are best-effort evidence;
/// a device that answers none of these is simply not fingerprinted by them.
/// </summary>
public class DiscoveryService
{
    private static readonly string[] MdnsServices =
    {
        "_googlecast._tcp.local",       // Chromecast / Android TV -> TV
        "_androidtvremote2._tcp.local", // Android TV -> TV
        "_airplay._tcp.local",          // Apple TV / AirPlay devices -> TV or PC
        "_raop._tcp.local",             // AirPlay audio -> IoT (speaker)
        "_ipp._tcp.local",              // AirPrint printers -> Printer
        "_printer._tcp.local",
        "_pdl-datastream._tcp.local",
        "_scanner._tcp.local",
        "_uscan._tcp.local",
        "_hap._tcp.local",              // HomeKit accessories -> IoT
        "_spotify-connect._tcp.local",  // speakers -> IoT
        "_workstation._tcp.local",      // macOS / Linux desktops -> PC
        "_smb._tcp.local",              // file sharing (Mac/Linux/NAS) -> PC
        "_adisk._tcp.local",
        "_device-info._tcp.local",      // macOS model announcements (model=MacBookAir...)
        "_esphomelib._tcp.local",       // ESPHome IoT
        "_hue._tcp.local",              // Philips Hue bridge -> IoT
        "_sonos._tcp.local",            // Sonos speakers -> IoT
        "_sleep-proxy._tcp.local",
        "_companion-link._tcp.local"    // Apple devices
    };

    /// <summary>
    /// Runs mDNS, SSDP and WS-Discovery probes concurrently. Returns evidence per IP.
    /// Never throws; failures in one protocol do not affect the others.
    /// </summary>
    public async Task<Dictionary<string, DiscoveryInfo>> DiscoverAllAsync(bool useMdns, bool useSsdp, bool useWsd, int windowMs = 3000)
    {
        var merged = new ConcurrentDictionary<string, DiscoveryInfo>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task>();

        if (useMdns) tasks.Add(Task.Run(() => MdnsSweep(merged, windowMs)));
        if (useSsdp) tasks.Add(Task.Run(() => SsdpSweep(merged, windowMs)));
        if (useWsd) tasks.Add(Task.Run(() => WsdSweep(merged, Math.Max(2000, windowMs - 500))));

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

        // Devices can answer many packets - keep each source/evidence once.
        foreach (var info in merged.Values)
        {
            var src = info.Sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            info.Sources.Clear();
            info.Sources.AddRange(src);
            var ev = info.Evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            info.Evidence.Clear();
            info.Evidence.AddRange(ev);
        }

        return merged.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ mDNS

    private void MdnsSweep(ConcurrentDictionary<string, DiscoveryInfo> merged, int windowMs)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353)); }
            catch { udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); } // legacy unicast fallback
            try { udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251")); } catch { }
            udp.Client.ReceiveTimeout = 500;

            // One DNS query packet carrying all service questions (QU bit off -> multicast responses).
            var query = BuildDnsQuery(MdnsServices);
            udp.Send(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));

            var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
            while (DateTime.UtcNow < deadline)
            {
                UdpReceiveResult res;
                try { res = udp.Receive(ref deadline); }
                catch (SocketException) { continue; }               // timeout tick
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                try { ParseMdnsResponse(res.Buffer, res.RemoteEndPoint.Address, merged); } catch { }
            }
        }
        catch { /* mDNS unavailable - silently skip this evidence source */ }
    }

    private static byte[] BuildDnsQuery(string[] services)
    {
        using var ms = new System.IO.MemoryStream();
        // Header: ID=0, QR=0, one shot
        ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(0);                 // QDCOUNT filled below
        ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(0);
        ushort qd = (ushort)services.Length;
        var head = ms.ToArray();
        head[4] = (byte)(qd >> 8); head[5] = (byte)(qd & 0xFF);

        var body = new System.IO.MemoryStream();
        foreach (var svc in services)
        {
            foreach (var label in svc.Split('.'))
            {
                if (label.Length == 0) continue;
                body.WriteByte((byte)label.Length);
                var b = Encoding.ASCII.GetBytes(label);
                body.Write(b, 0, b.Length);
            }
            body.WriteByte(0);                            // root label
            body.WriteByte(0); body.WriteByte(12);        // QTYPE = PTR
            body.WriteByte(0); body.WriteByte(1);         // QCLASS = IN
        }
        return head.Concat(body.ToArray()).ToArray();
    }

    private static void ParseMdnsResponse(byte[] buffer, IPAddress from, ConcurrentDictionary<string, DiscoveryInfo> merged)
    {
        if (buffer.Length < 12) return;
        int qd = ReadU16(buffer, 4), an = ReadU16(buffer, 6);
        int pos = 12;
        // Skip questions.
        for (int i = 0; i < qd && pos < buffer.Length; i++)
        {
            pos = SkipName(buffer, pos);
            pos += 4;
        }

        var info = GetOrCreate(merged, from.ToString());
        info.Sources.Add("mDNS");

        for (int i = 0; i < an && pos < buffer.Length; i++)
        {
            string rrName = ReadName(buffer, ref pos);
            if (pos + 10 > buffer.Length) break;
            int type = ReadU16(buffer, pos);
            int rdLen = ReadU16(buffer, pos + 8);
            int rdata = pos + 10;
            if (rdata + rdLen > buffer.Length) break;

            switch (type)
            {
                case 12: // PTR - service instance name
                    pos = rdata;
                    var inst = ReadName(buffer, ref pos).Split('.')[0];
                    if (!string.IsNullOrEmpty(inst) && string.IsNullOrEmpty(info.FriendlyName))
                        info.FriendlyName = inst;
                    info.Evidence.Add(ServiceHint(rrName, info));
                    break;
                case 33: // SRV - target host + port
                    if (rdLen >= 6)
                    {
                        int tmp = rdata + 4;
                        string target = ReadName(buffer, ref tmp);
                        if (!string.IsNullOrEmpty(info.Hostname) == false && !string.IsNullOrEmpty(target))
                            info.Hostname = target.Replace(".local", "").TrimEnd('.');
                    }
                    pos = rdata + rdLen;
                    break;
                case 16: // TXT - model / manufacturer / OS evidence
                    var txt = ParseTxt(buffer, rdata, rdLen);
                    ApplyTxt(txt, info);
                    pos = rdata + rdLen;
                    break;
                case 1: // A record - we already know the responder IP
                    pos = rdata + rdLen;
                    break;
                default:
                    pos = rdata + rdLen;
                    break;
            }
        }
    }

    private static string ServiceHint(string rrName, DiscoveryInfo info)
    {
        if (rrName.Contains("_ipp") || rrName.Contains("_printer") || rrName.Contains("_pdl") ||
            rrName.Contains("_uscan") || rrName.Contains("_scanner"))
        {
            info.TypeHint = DeviceCategory.Printer;
            return "ipp/printer service";
        }
        if (rrName.Contains("_googlecast") || rrName.Contains("_androidtv"))
        {
            info.TypeHint = DeviceCategory.TV;
            info.OsHint ??= "Android TV";
            return "chromecast/androidtv";
        }
        if (rrName.Contains("_airplay") || rrName.Contains("_raop"))
        {
            if (info.TypeHint == DeviceCategory.Other) info.TypeHint = DeviceCategory.TV;
            return "airplay";
        }
        if (rrName.Contains("_hap")) { info.TypeHint = DeviceCategory.IoT; return "homekit"; }
        if (rrName.Contains("_spotify")) { info.TypeHint = DeviceCategory.IoT; return "spotify-connect"; }
        if (rrName.Contains("_esphomelib") || rrName.Contains("_hue") || rrName.Contains("_sonos"))
        { info.TypeHint = DeviceCategory.IoT; return "smart home"; }
        if (rrName.Contains("_workstation") || rrName.Contains("_smb") || rrName.Contains("_adisk"))
        {
            if (info.TypeHint == DeviceCategory.Other) info.TypeHint = DeviceCategory.PC;
            return "file/workstation sharing";
        }
        if (rrName.Contains("_device-info") || rrName.Contains("_companion-link")) return "apple device";
        return rrName.Split('.')[0].TrimStart('_');
    }

    private static void ApplyTxt(Dictionary<string, string> txt, DiscoveryInfo info)
    {
        if (txt.TryGetValue("md", out var md) && !string.IsNullOrEmpty(md))          // Google Cast model
            info.Model = Best(info.Model, md);
        if (txt.TryGetValue("am", out var am) && !string.IsNullOrEmpty(am))          // AirPlay model
            info.Model = Best(info.Model, am);
        if (txt.TryGetValue("model", out var model) && !string.IsNullOrEmpty(model)) // macOS/ESPHome model
            info.Model = Best(info.Model, model);
        if (txt.TryGetValue("manufacturer", out var man) && !string.IsNullOrEmpty(man))
            info.Manufacturer = Best(info.Manufacturer, man);
        if (txt.TryGetValue("fn", out var fn) && !string.IsNullOrEmpty(fn) && string.IsNullOrEmpty(info.FriendlyName))
            info.FriendlyName = fn;                                                  // friendly name
        if (txt.TryGetValue("name", out var nm) && !string.IsNullOrEmpty(nm) && string.IsNullOrEmpty(info.FriendlyName))
            info.FriendlyName = nm;
        if (txt.TryGetValue("bs", out var bs) && bs.Contains("google", StringComparison.OrdinalIgnoreCase))
            info.Manufacturer = Best(info.Manufacturer, "Google");
        if (txt.TryGetValue("deviceid", out _) && txt.ContainsKey("am"))
        {
            // AirPlay model names: AppleTV → TV, MacBook/iMac → PC, iPhone/iPad → Phone
            var m = info.Model ?? "";
            if (m.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase)) info.TypeHint = DeviceCategory.TV;
            else if (m.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)) info.TypeHint = DeviceCategory.Phone;
            else if (m.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)) info.TypeHint = DeviceCategory.Tablet;
            else if (m.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("iMac", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("MacMini", StringComparison.OrdinalIgnoreCase))
            {
                info.TypeHint = DeviceCategory.PC;
                info.OsHint = Best(info.OsHint, "macOS");
            }
        }
    }

    private static string Best(string current, string candidate) =>
        string.IsNullOrEmpty(current) ? candidate : current;

    private static Dictionary<string, string> ParseTxt(byte[] buf, int off, int len)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int p = off, end = off + len;
        while (p < end)
        {
            int segLen = buf[p]; p++;
            if (segLen == 0 || p + segLen > end) break;
            var seg = Encoding.UTF8.GetString(buf, p, segLen);
            p += segLen;
            int eq = seg.IndexOf('=');
            if (eq > 0) map[seg.Substring(0, eq)] = seg.Substring(eq + 1);
        }
        return map;
    }

    // ------------------------------------------------------------------ SSDP

    private async void SsdpSweep(ConcurrentDictionary<string, DiscoveryInfo> merged, int windowMs)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 400;
            var msearch =
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(msearch);
            await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stByIp = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
            while (DateTime.UtcNow < deadline)
            {
                UdpReceiveResult res;
                try { res = udp.Receive(ref deadline); }
                catch (SocketException) { continue; }
                catch (OperationCanceledException) { break; }
                var text = Encoding.ASCII.GetString(res.Buffer);
                var lines = text.Split('\n').Select(l => l.Trim()).ToList();
                var loc = lines.FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
                var st = lines.FirstOrDefault(l => l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase));
                var ip = res.RemoteEndPoint.Address.ToString();
                if (st != null)
                {
                    var stVal = st.Substring(3).Trim();
                    if (!stVal.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase) && stVal.Length > 0)
                    {
                        if (!stByIp.TryGetValue(ip, out var list)) stByIp[ip] = list = new List<string>();
                        if (!list.Contains(stVal)) list.Add(stVal);
                    }
                }
                if (loc != null) locations.Add(loc.Substring("LOCATION:".Length).Trim());
            }

            // Fetch device description XMLs (models, manufacturers, friendly names).
            var fetches = locations.Select(async loc =>
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    var xml = await http.GetStringAsync(loc).ConfigureAwait(false);
                    var doc = XDocument.Parse(xml);
                    var root = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                    if (root == null) return;

                    string Get(string n) => root.Descendants().Where(e => e.Name.LocalName == n)
                        .Select(e => e.Value.Trim()).FirstOrDefault() ?? "";

                    var ip = new Uri(loc).Host;
                    var info = GetOrCreate(merged, ip);
                    info.Sources.Add("SSDP");
                    info.Manufacturer = Best(info.Manufacturer, Get("manufacturer"));
                    info.Model = Best(info.Model, Get("modelName"));
                    var fn = Get("friendlyName");
                    if (!string.IsNullOrEmpty(fn)) info.FriendlyName = Best(info.FriendlyName, fn);
                    var dType = Get("deviceType");
                    if (dType.Contains("InternetGatewayDevice")) info.TypeHint = DeviceCategory.Router;
                    else if (dType.Contains("MediaRenderer") || dType.Contains("MediaServer"))
                    {
                        if (info.TypeHint == DeviceCategory.Other) info.TypeHint = DeviceCategory.TV;
                    }
                    if (stByIp.TryGetValue(ip, out var sts))
                        foreach (var s in sts) info.Evidence.Add("upnp:" + ShortenSt(s));
                }
                catch { }
            }).ToList();
            try { await Task.WhenAll(fetches).ConfigureAwait(false); } catch { }
        }
        catch { }
    }

    private static string ShortenSt(string st)
    {
        var m = System.Text.RegularExpressions.Regex.Match(st, @"(?:device|service):([A-Za-z]+)");
        return m.Success ? m.Groups[1].Value : st;
    }

    // ---------------------------------------------------------- WS-Discovery

    private async void WsdSweep(ConcurrentDictionary<string, DiscoveryInfo> merged, int windowMs)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 400;
            var probe =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" " +
                "xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
                "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\">" +
                "<s:Header>" +
                "<a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>" +
                $"<a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID>" +
                "<a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>" +
                "</s:Header><s:Body><d:Probe/></s:Body></s:Envelope>";
            var bytes = Encoding.UTF8.GetBytes(probe);
            await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702)).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
            while (DateTime.UtcNow < deadline)
            {
                UdpReceiveResult res;
                try { res = udp.Receive(ref deadline); }
                catch (SocketException) { continue; }
                catch (OperationCanceledException) { break; }
                try
                {
                    var text = Encoding.UTF8.GetString(res.Buffer);
                    if (!text.Contains("ProbeMatches", StringComparison.OrdinalIgnoreCase)) continue;

                    var ip = res.RemoteEndPoint.Address.ToString();
                    var info = GetOrCreate(merged, ip);
                    info.Sources.Add("WS-Discovery");

                    // XAddrs - device service URL, contains the IP; Types - e.g. PrintDevice.
                    var xaddrs = System.Text.RegularExpressions.Regex.Match(
                        text, @"<d:XAddrs>([^<]+)</d:XAddrs>");
                    if (xaddrs.Success && string.IsNullOrEmpty(info.Hostname))
                    {
                        try { info.Hostname = new Uri(xaddrs.Groups[1].Value.Split(' ')[0]).Host; }
                        catch { }
                    }
                    var types = System.Text.RegularExpressions.Regex.Match(
                        text, @"<d:Types[^>]*>([^<]+)</d:Types>");
                    if (types.Success)
                    {
                        var t = types.Groups[1].Value;
                        if (t.Contains("PrintDevice", StringComparison.OrdinalIgnoreCase) ||
                            t.Contains("ScannerDevice", StringComparison.OrdinalIgnoreCase))
                        {
                            info.TypeHint = DeviceCategory.Printer;
                            info.Evidence.Add("wsd:print device");
                        }
                        else if (t.Contains("PubSubDevice") || t.Contains("NetworkVideo"))
                        {
                            if (info.TypeHint == DeviceCategory.Other) info.TypeHint = DeviceCategory.IoT;
                            info.Evidence.Add("wsd:device");
                        }
                        else
                        {
                            info.Evidence.Add("wsd:device");
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // ------------------------------------------------------------- utilities

    private static DiscoveryInfo GetOrCreate(ConcurrentDictionary<string, DiscoveryInfo> merged, string ip) =>
        merged.GetOrAdd(ip, _ => new DiscoveryInfo { Ip = ip });

    private static int ReadU16(byte[] buf, int off) => (buf[off] << 8) | buf[off + 1];

    private static int SkipName(byte[] buf, int pos)
    {
        while (pos < buf.Length)
        {
            int len = buf[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2; // compression pointer
            pos += len + 1;
        }
        return pos;
    }

    private static string ReadName(byte[] buf, ref int pos)
    {
        var sb = new StringBuilder();
        int jumps = 0;
        int end = -1;
        while (pos < buf.Length && jumps++ < 16)
        {
            int len = buf[pos];
            if (len == 0) { if (end < 0) end = pos + 1; pos = end; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (end < 0) end = pos + 2;
                pos = ((len & 0x3F) << 8) | buf[pos + 1];
                continue;
            }
            if (pos + 1 + len > buf.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(buf, pos + 1, len));
            pos += len + 1;
        }
        if (end >= 0) pos = end;
        return sb.ToString();
    }
}

/// <summary>Small helper: receive with an overall deadline on a UDP socket.</summary>
internal static class UdpClientExtensions
{
    public static UdpReceiveResult Receive(this UdpClient client, ref DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
        using var cts = new CancellationTokenSource(remaining);
        return client.ReceiveAsync(cts.Token).AsTask().GetAwaiter().GetResult();
    }

    public static UdpReceiveResult Receive(this UdpClient client, DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
        using var cts = new CancellationTokenSource(remaining);
        return client.ReceiveAsync(cts.Token).AsTask().GetAwaiter().GetResult();
    }
}
