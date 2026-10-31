using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NetControl.Models;

public enum DeviceCategory
{
    Other = 0,
    PC = 1,
    Phone = 2,
    Tablet = 3,
    Console = 4,
    Router = 5,
    IoT = 6,
    Printer = 7,
    TV = 8
}

public class Device : INotifyPropertyChanged
{
    private string _mac = "";
    private string _ip = "";
    private string _hostname = "";
    private string _nameSource = "";
    private string _vendor = "";
    private DeviceCategory _category = DeviceCategory.Other;
    private string _customName = "";
    private bool _trusted;
    private bool _blocked;
    private bool _bandwidthLimited;
    private string _notes = "";
    private string _osGuess = "";
    private string _model = "";
    private int _confidence;
    private string _sources = "";
    private string _friendlyNameSeen = "";
    private DateTime _firstSeen = DateTime.Now;
    private DateTime _lastSeen = DateTime.Now;
    private bool _online;
    private bool _isLocal;
    private bool _isGateway;
    private bool _known;

    public string Mac { get => _mac; set => Set(ref _mac, value); }
    public string Ip { get => _ip; set => Set(ref _ip, value); }
    public string Hostname { get => _hostname; set { Set(ref _hostname, value); Raise(nameof(DisplayName)); Raise(nameof(Subtitle)); } }
    /// <summary>In-memory only: "NetBIOS", "DNS", "Local" or "" - used to prefer stronger name sources.</summary>
    public string NameSource { get => _nameSource; set => Set(ref _nameSource, value); }
    public string Vendor { get => _vendor; set { Set(ref _vendor, value); Raise(nameof(VendorShort)); Raise(nameof(DisplayName)); Raise(nameof(Subtitle)); } }
    public DeviceCategory Category { get => _category; set => Set(ref _category, value); }
    public string CustomName { get => _customName; set { Set(ref _customName, value); Raise(nameof(DisplayName)); } }
    public bool Trusted { get => _trusted; set => Set(ref _trusted, value); }
    public bool Blocked { get => _blocked; set => Set(ref _blocked, value); }
    public bool BandwidthLimited { get => _bandwidthLimited; set => Set(ref _bandwidthLimited, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    /// <summary>Best-effort operating system guess, e.g. "Windows family (TTL 128)". Empty = unknown.</summary>
    public string OsGuess { get => _osGuess; set => Set(ref _osGuess, value); }
    /// <summary>Model name from mDNS/SSDP evidence, e.g. "Chromecast Ultra". Empty = unknown.</summary>
    public string Model { get => _model; set { Set(ref _model, value); Raise(nameof(Subtitle)); } }
    /// <summary>Identification confidence 0-100 (labelled High/Medium/Low/None).</summary>
    public int Confidence { get => _confidence; set => Set(ref _confidence, value); }
    /// <summary>Comma-separated identity sources, e.g. "NetBIOS,OUI,mDNS".</summary>
    public string Sources { get => _sources; set => Set(ref _sources, value); }
    /// <summary>Friendly name advertised via mDNS/SSDP (shown in details; never overrides user naming).</summary>
    public string FriendlyNameSeen { get => _friendlyNameSeen; set => Set(ref _friendlyNameSeen, value); }

    public string ConfidenceLabel => Confidence >= 70 ? "High" : Confidence >= 40 ? "Medium" : Confidence > 0 ? "Low" : "None";
    public DateTime FirstSeen { get => _firstSeen; set => Set(ref _firstSeen, value); }
    public DateTime LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    public bool Online { get => _online; set => Set(ref _online, value); }
    public bool IsLocal { get => _isLocal; set { Set(ref _isLocal, value); Raise(nameof(Label)); Raise(nameof(Subtitle)); } }
    public bool IsGateway { get => _isGateway; set { Set(ref _isGateway, value); Raise(nameof(Label)); Raise(nameof(Subtitle)); } }
    public bool Known { get => _known; set => Set(ref _known, value); }

    /// <summary>
    /// NetCut-style friendly name: custom name → hostname → vendor brand → MAC.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName;
            var hn = CleanHostname(Hostname);
            if (!string.IsNullOrWhiteSpace(hn)) return hn;
            if (!string.IsNullOrWhiteSpace(VendorShort)) return VendorShort;
            return Mac;
        }
    }

    /// <summary>First word of the OUI vendor, e.g. "Huawei Technologies Co.,Ltd" → "Huawei".</summary>
    public string VendorShort
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Vendor)) return "";
            var word = Vendor.Trim().Split(new[] { ' ', ',', '/' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? "";
            return word.Length > 24 ? word.Substring(0, 24) : word;
        }
    }

    /// <summary>NetCut-style role label, e.g. "This computer" or "Network gateway".</summary>
    public string Label => IsLocal ? "This computer" : IsGateway ? "Network gateway" : "";

    /// <summary>Small gray second line under the name in the device list.</summary>
    public string Subtitle
    {
        get
        {
            var label = Label;
            if (!string.IsNullOrEmpty(label))
            {
                var hn = CleanHostname(Hostname);
                return string.IsNullOrWhiteSpace(hn) ? label : $"{label} · {hn}";
            }
            var full = (Hostname ?? "").Trim();
            var shortName = CleanHostname(full);
            if (!string.IsNullOrEmpty(full) && !string.Equals(full, shortName, StringComparison.OrdinalIgnoreCase))
                return full; // short name is shown as DisplayName; full FQDN goes here
            if (!string.IsNullOrEmpty(Model))
                return Model;
            if (!string.IsNullOrEmpty(VendorShort) && !string.Equals(DisplayName, VendorShort, StringComparison.Ordinal))
                return Vendor;
            return "";
        }
    }

    /// <summary>Strips local suffixes (.local, .lan, .fritz.box ...) and name-server junk from a hostname.</summary>
    public static string CleanHostname(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return "";
        var name = hostname.Trim().TrimEnd('.');
        var lower = name.ToLowerInvariant();
        string[] suffixes =
        {
            ".local", ".lan", ".home", ".internal", ".localdomain", ".localdomain",
            ".fritz.box", ".box", ".homenet", ".lan.local", ".workgroup", ".mshome.net"
        };
        foreach (var s in suffixes)
        {
            if (lower.EndsWith(s, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - s.Length);
                break;
            }
        }
        // NetBIOS names are 15 chars padded with spaces - remove any padding.
        name = name.TrimEnd();
        return name;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mac { get; set; } = "";
    public string OriginalMac { get; set; } = "";
    public string[] Ips { get; set; } = Array.Empty<string>();
    public string Gateway { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string DnsServers { get; set; } = "";
    public bool IsUp { get; set; }
    public string Type { get; set; } = "";
    public long Speed { get; set; }
    public string DhcpEnabled { get; set; } = "";
}

public class RouterStatus
{
    public string Kind { get; set; } = "None";
    public bool Connected { get; set; }
    public string Message { get; set; } = "Not configured";
    public string WanIp { get; set; } = "";
    public long TotalBytesSent { get; set; } = -1;
    public long TotalBytesReceived { get; set; } = -1;
    public bool SupportsBlock { get; set; }
    public bool SupportsBandwidth { get; set; }
    public DateTime? LastCheck { get; set; }
}

public class BandwidthLimitInfo
{
    public bool Active { get; set; }
    public string Source { get; set; } = "";
    public long DownKbps { get; set; }
    public long UpKbps { get; set; }
    public string Message { get; set; } = "";
}
