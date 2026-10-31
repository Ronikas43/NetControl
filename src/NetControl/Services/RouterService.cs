using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// OpenWrt router integration over the LuCI ubus JSON-RPC endpoint (/ubus).
/// Supports: session login, firewall rule add/delete for per-MAC internet blocking,
/// bandwidth capability detection (via ubus object listing).
/// Requires OpenWrt 21.02+ with rpcd and uci ubus access for the login user (root by default).
/// </summary>
public class RouterService
{
    private HttpClient _http;
    private string _session = "";
    private readonly object _gate = new();
    private AppSettings _settings;

    // rule section ids learned while adding rules (mac -> uci section id)
    private readonly Dictionary<string, string> _ruleSections = new(StringComparer.OrdinalIgnoreCase);

    public RouterStatus LastStatus { get; private set; } = new();

    /// <summary>Raised whenever LastStatus was updated (routed to the UI thread by subscribers).</summary>
    public event Action LastStatusChanged;

    public RouterService(AppSettings settings)
    {
        _settings = settings;
        var handler = new HttpClientHandler();
        if (settings.Router.UseHttps)
        {
            // Self-signed certificates are the norm on home routers.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void ReloadSettings(AppSettings settings)
    {
        _settings = settings;
        lock (_gate) { _session = ""; }
    }

    private string BaseUrl
    {
        get
        {
            var s = _settings.Router;
            var scheme = s.UseHttps ? "https" : "http";
            var ip = string.IsNullOrWhiteSpace(s.Ip) ? "192.168.1.1" : s.Ip.Trim();
            return $"{scheme}://{ip}/ubus";
        }
    }

    private static string RuleNameForMac(string mac) =>
        "NetControl_Block_" + mac.Replace(":", "").ToUpperInvariant();

    private async Task<JsonElement?> CallAsync(string session, string objectPath, string method, object payload)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "call",
                @params = new object[] { session, objectPath, method, payload ?? new { } }
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(BaseUrl, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = resp.Content.ReadAsStream();
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            // result[0] is the ubus status code (0 = OK), result[1] is the payload
            if (result.GetArrayLength() < 2 || result[0].GetInt32() != 0) return null;
            return result[1].Clone();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> EnsureSessionAsync()
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_session)) return true;
        }
        var s = _settings.Router;
        var login = await CallAsync("", "session", "login",
            new { username = s.Username, password = s.Password, timeout = 3600 }).ConfigureAwait(false);
        if (login == null) return false;

        string sid = "";
        try
        {
            if (login.Value.TryGetProperty("ubus_rpc_session", out var sidEl))
                sid = sidEl.GetString() ?? "";
        }
        catch { }
        if (string.IsNullOrEmpty(sid)) return false;

        lock (_gate) { _session = sid; }
        return true;
    }

    public async Task<RouterStatus> TestAsync()
    {
        var status = new RouterStatus { Kind = "OpenWrt", LastCheck = DateTime.Now };
        var s = _settings.Router;
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Ip))
        {
            status.Message = "Router integration disabled in Settings.";
            LastStatus = status;
            LastStatusChanged?.Invoke();
            return status;
        }

        lock (_gate) { _session = ""; }
        if (!await EnsureSessionAsync().ConfigureAwait(false))
        {
            status.Message = $"Login to {s.Ip} failed. Check IP, username, password and that rpcd/LuCI RPC is reachable.";
            LastStatus = status;
            LastStatusChanged?.Invoke();
            return status;
        }

        status.Connected = true;
        status.SupportsBlock = true;
        status.Message = "Connected to OpenWrt (ubus session OK). Per-device blocking available.";

        // Detect optional bandwidth packages (sqm). Not present on stock installs.
        var sqm = await CallAsync(_session, "sqm", "list", null).ConfigureAwait(false);
        status.SupportsBandwidth = sqm != null;
        if (sqm == null)
            status.Message += " Bandwidth limiting requires the 'sqm-scripts' package (not detected).";

        LastStatus = status;
        LastStatusChanged?.Invoke();
        return status;
    }

    public async Task<(bool ok, string message)> BlockMacAsync(string mac)
    {
        if (!await EnsureSessionAsync().ConfigureAwait(false))
            return (false, "Router session unavailable (login failed).");

        var name = RuleNameForMac(mac);
        var add = await CallAsync(_session, "uci", "add", new
        {
            config = "firewall",
            type = "rule",
            values = new Dictionary<string, object>
            {
                ["name"] = name,
                ["src"] = "lan",
                ["src_mac"] = mac,
                ["dest"] = "wan",
                ["proto"] = "all",
                ["target"] = "DROP",
                ["enabled"] = "1"
            }
        }).ConfigureAwait(false);

        if (add == null)
            return (false, "uci add failed - firewall rule was NOT created.");

        try
        {
            if (add.Value.TryGetProperty("section", out var sec))
                _ruleSections[mac] = sec.GetString() ?? "";
        }
        catch { }

        return await CommitAndApplyAsync().ConfigureAwait(false)
            ? (true, $"Firewall rule '{name}' active - internet access revoked for {mac}.")
            : (false, "Rule added and committed, but the firewall reload could not be triggered automatically.");
    }

    public async Task<(bool ok, string message)> UnblockMacAsync(string mac)
    {
        if (!await EnsureSessionAsync().ConfigureAwait(false))
            return (false, "Router session unavailable (login failed).");

        // 1) Try the section id we recorded when adding the rule.
        string section = _ruleSections.TryGetValue(mac, out var s0) ? s0 : "";

        // 2) Otherwise find the rule by listing uci config.
        if (string.IsNullOrEmpty(section))
            section = await FindRuleSectionAsync(RuleNameForMac(mac)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(section))
            return (false, "No NetControl firewall rule found for this device on the router.");

        var del = await CallAsync(_session, "uci", "delete",
            new { config = "firewall", section }).ConfigureAwait(false);
        if (del == null)
            return (false, "uci delete failed - the rule is still active.");

        _ruleSections.Remove(mac);
        return await CommitAndApplyAsync().ConfigureAwait(false)
            ? (true, $"Firewall rule removed - internet access restored for {mac}.")
            : (false, "Rule deleted and committed, but the firewall reload could not be triggered automatically.");
    }

    public async Task<(bool ok, string message)> RestoreAllAsync(IEnumerable<string> macs)
    {
        var failures = new List<string>();
        foreach (var mac in macs)
        {
            var (ok, _) = await UnblockMacAsync(mac).ConfigureAwait(false);
            if (!ok) failures.Add(mac);
        }
        return failures.Count == 0
            ? (true, "All managed devices restored.")
            : (false, $"Restored with failures for: {string.Join(", ", failures)}");
    }

    /// <summary>
    /// Reads the DHCP lease table from the router (/tmp/dhcp.leases via ubus file access).
    /// Gives the hostname each device asked for when it joined - useful where NetBIOS and
    /// mDNS are silent (Android phones). Returns an empty list when unavailable (honest).
    /// </summary>
    public async Task<List<(string mac, string ip, string hostname)>> GetDhcpLeasesAsync()
    {
        var leases = new List<(string, string, string)>();
        try
        {
            if (!await EnsureSessionAsync().ConfigureAwait(false)) return leases;
            var read = await CallAsync(_session, "file", "read",
                new { path = "/tmp/dhcp.leases" }).ConfigureAwait(false);
            if (read == null) return leases;

            string data = "";
            try
            {
                if (read.Value.TryGetProperty("data", out var dEl)) data = dEl.GetString() ?? "";
            }
            catch { }
            if (string.IsNullOrEmpty(data)) return leases;

            foreach (var rawLine in data.Split('\n'))
            {
                var line = rawLine.Trim();
                // Format: <expiry> <mac> <ip> <hostname> [clientid]
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                var mac = NetworkHelper.NormalizeMac(parts[1]);
                if (mac.Length == 0) continue;
                var host = parts[3] == "*" ? "" : parts[3];
                leases.Add((mac, parts[2], host));
            }
        }
        catch { }
        return leases;
    }

    private async Task<string> FindRuleSectionAsync(string ruleName)
    {
        var get = await CallAsync(_session, "uci", "get",
            new { config = "firewall", type = "rule" }).ConfigureAwait(false);
        if (get == null) return "";

        try
        {
            var el = get.Value;
            // Newer uci2 shape: { "sections": [ { "name": "...", "values": { "name": "..." } } ] }
            if (el.TryGetProperty("sections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in arr.EnumerateArray())
                {
                    string id = sec.TryGetProperty("name", out var n) ? n.GetString() : "";
                    if (sec.TryGetProperty("values", out var vals) &&
                        vals.TryGetProperty("name", out var vn) &&
                        string.Equals(vn.GetString(), ruleName, StringComparison.OrdinalIgnoreCase))
                        return id ?? "";
                }
            }
            // Older shape: { "values": { "cfg0abc": { "name": "...", ... } } }
            if (el.TryGetProperty("values", out var obj) && obj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object &&
                        prop.Value.TryGetProperty("name", out var vn) &&
                        string.Equals(vn.GetString(), ruleName, StringComparison.OrdinalIgnoreCase))
                        return prop.Name;
                }
            }
        }
        catch { }
        return "";
    }

    private async Task<bool> CommitAndApplyAsync()
    {
        var commit = await CallAsync(_session, "uci", "commit",
            new { config = "firewall" }).ConfigureAwait(false);
        if (commit == null) return false;

        // Apply triggers a firewall reload on OpenWrt 21+; on older versions the
        // commit is enough after the next reload, so failure here is not fatal.
        await CallAsync(_session, "uci", "apply",
            new { rollback = false, timeout = 10 }).ConfigureAwait(false);
        return true;
    }
}
