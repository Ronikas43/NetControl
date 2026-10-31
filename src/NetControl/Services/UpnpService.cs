using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using NetControl.Models;

/// <summary>
/// Minimal UPnP IGD client: SSDP discovery, device description parsing,
/// WAN status + total byte counters (IGD2). Used for router-level traffic stats.
/// UPnP cannot block a device by MAC - blocking stays with OpenWrt/ARP; the UI states this honestly.
/// </summary>
namespace NetControl.Services
{
    public class UpnpService
    {
        private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };

        public string ControlUrl { get; private set; } = "";
        public string ServiceType { get; private set; } = "";
        public string FriendlyName { get; private set; } = "";

        public async Task<List<(string ip, string location)>> DiscoverAsync(int timeoutMs = 2500)
        {
            var found = new List<(string, string)>();
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = timeoutMs;
                var msearch =
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(msearch);
                await udp.SendAsync(bytes, bytes.Length, "239.255.255.250", 1900);

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var seen = new HashSet<string>();
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var res = await udp.ReceiveAsync().WaitAsync(deadline - DateTime.UtcNow);
                        var text = Encoding.ASCII.GetString(res.Buffer);
                        var loc = text.Split('\n')
                            .Select(l => l.Trim())
                            .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
                        if (loc == null) continue;
                        var url = loc.Substring("LOCATION:".Length).Trim();
                        if (seen.Add(url))
                            found.Add((res.RemoteEndPoint.Address.ToString(), url));
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                }
            }
            catch { }
            return found;
        }

        /// <summary>Connects to the first IGD found and reads its WAN connection service URL.</summary>
        public async Task<bool> ConnectAsync(string gatewayIpHint = null)
        {
            var devices = await DiscoverAsync().ConfigureAwait(false);
            if (devices.Count == 0) return false;

            if (!string.IsNullOrEmpty(gatewayIpHint))
                devices = devices.OrderByDescending(d => d.ip == gatewayIpHint ? 1 : 0).ToList();

            foreach (var (_, location) in devices)
            {
                try
                {
                    var xml = await _http.GetStringAsync(location).ConfigureAwait(false);
                    var doc = XDocument.Parse(xml);
                    var ns = doc.Root?.Name.Namespace ?? "";
                    FriendlyName = doc.Descendants(ns + "friendlyName").FirstOrDefault()?.Value ?? "Router";

                    var baseEl = doc.Descendants(ns + "URLBase").FirstOrDefault()?.Value;
                    var baseUri = !string.IsNullOrEmpty(baseEl) ? new Uri(baseEl) : new Uri(location);

                    foreach (var svc in doc.Descendants(ns + "service"))
                    {
                        var st = svc.Element(ns + "serviceType")?.Value ?? "";
                        var ctrl = svc.Element(ns + "controlURL")?.Value ?? "";
                        if (st.Contains("WANIPConnection") || st.Contains("WANPPPConnection"))
                        {
                            ServiceType = st;
                            ControlUrl = new Uri(baseUri, ctrl).ToString();
                            return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        private async Task<XDocument> SoapAsync(string action, string bodyXml)
        {
            if (string.IsNullOrEmpty(ControlUrl)) return null;
            var soap =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                bodyXml + "</s:Body></s:Envelope>";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ControlUrl);
                req.Headers.Add("SOAPACTION", $"\"{ServiceType}#{action}\"");
                req.Content = new StringContent(soap, Encoding.UTF8, "text/xml");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return XDocument.Parse(text);
            }
            catch { return null; }
        }

        private static string V(XDocument doc, string name)
        {
            if (doc == null) return "";
            return doc.Descendants()
                .Where(e => e.Name.LocalName == name)
                .Select(e => e.Value)
                .FirstOrDefault() ?? "";
        }

        public async Task<RouterStatus> GetStatusAsync()
        {
            var status = new RouterStatus { Kind = "UPnP", LastCheck = DateTime.Now };
            if (!await ConnectAsync().ConfigureAwait(false))
            {
                status.Message = "No UPnP Internet Gateway Device found on this network.";
                return status;
            }

            status.Connected = true;
            status.SupportsBlock = false;   // IGD has no per-MAC block - stated honestly in UI
            status.SupportsBandwidth = false;

            var resp = await SoapAsync("GetStatusInfo",
                $"<u:GetStatusInfo xmlns:u=\"{ServiceType}\"/>").ConfigureAwait(false);
            status.WanIp = "";
            status.Message = $"Connected to UPnP IGD '{FriendlyName}'. " +
                             "UPnP cannot revoke per-device internet access - use OpenWrt or ARP enforcement for blocking.";

            // IGD2 total traffic counters (best effort; IGD1 has none).
            var resp2 = await SoapAsync("GetTotalBytesSent",
                $"<u:GetTotalBytesSent xmlns:u=\"urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1\"/>").ConfigureAwait(false);
            var resp3 = await SoapAsync("GetTotalBytesReceived",
                $"<u:GetTotalBytesReceived xmlns:u=\"urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1\"/>").ConfigureAwait(false);
            var sent = V(resp2, "NewTotalBytesSent");
            var recv = V(resp3, "NewTotalBytesReceived");
            if (long.TryParse(sent, out var s)) status.TotalBytesSent = s;
            if (long.TryParse(recv, out var r)) status.TotalBytesReceived = r;

            return status;
        }
    }
}
