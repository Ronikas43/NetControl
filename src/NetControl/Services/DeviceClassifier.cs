using System;
using System.Collections.Generic;
using System.Linq;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>Result of classifying one device.</summary>
public class Classification
{
    public string OsGuess { get; set; } = "";
    public string Model { get; set; } = "";
    public DeviceCategory Type { get; set; } = DeviceCategory.Other;
    public int Confidence { get; set; }                 // 0-100
    public string ConfidenceLabel => Confidence >= 70 ? "High" : Confidence >= 40 ? "Medium" : Confidence > 0 ? "Low" : "None";
    public List<string> Sources { get; } = new();
}

/// <summary>
/// Combines every identity signal into a device type, OS guess, model and a confidence level:
/// OUI vendor, hostnames (NetBIOS/DNS/DHCP), mDNS/SSDP/WS-Discovery evidence and ICMP TTL
/// fingerprinting. Evidence-only: when nothing matches, the result is honest "unknown".
/// </summary>
public static class DeviceClassifier
{
    private static readonly (string prefix, DeviceCategory cat)[] VendorTypes =
    {
        ("Roku", DeviceCategory.TV), ("Sonos", DeviceCategory.IoT),
        ("Philips Lighting", DeviceCategory.IoT), ("Signify", DeviceCategory.IoT),
        ("Tuya", DeviceCategory.IoT), ("Espressif", DeviceCategory.IoT),
        ("Shenzhen", DeviceCategory.IoT),
        ("Sony Interactive", DeviceCategory.Console), ("Nintendo", DeviceCategory.Console),
        ("Razer", DeviceCategory.PC), ("Raspberry", DeviceCategory.PC),
        ("Hon Hai", DeviceCategory.PC), ("Wistron", DeviceCategory.PC),
        ("Quanta", DeviceCategory.PC), ("Compal", DeviceCategory.PC),
        ("Pegatron", DeviceCategory.PC), ("Inventec", DeviceCategory.PC)
    };

    private static readonly string[] PrinterVendors =
        { "HP", "Hewlett", "Canon", "Epson", "Brother", "Lexmark", "Xerox", "Kyocera", "Ricoh", "Zebra", "Oki" };

    private static readonly string[] PhoneVendors =
        { "Xiaomi", "Oppo", "Vivo", "OnePlus", "Realme", "Honor", "Motorola", "ZTE", "Huawei", "Google", "Wistron" };

    public static Classification Classify(Device d, DiscoveryInfo info, int ttl)
    {
        var c = new Classification();
        var vendor = d.Vendor ?? "";
        var name = $"{d.CustomName} {d.Hostname} {info?.FriendlyName} {info?.Hostname}".ToLowerInvariant();
        var sources = new List<string>(d.Sources?.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)) ?? Enumerable.Empty<string>());

        // ---- OS fingerprinting -------------------------------------------------
        if (ttl > 0)
        {
            if (ttl > 64 && ttl <= 128) { c.OsGuess = "Windows family (TTL " + ttl + ")"; sources.Add("TTL"); }
            else if (ttl > 0 && ttl <= 64) { c.OsGuess = "Linux/Android/macOS family (TTL " + ttl + ")"; sources.Add("TTL"); }
            else if (ttl > 128) { c.OsGuess = "Network device (TTL " + ttl + ")"; sources.Add("TTL"); }
        }
        if (d.NameSource == "NetBIOS") { c.OsGuess = Best("Windows family (NetBIOS)", c.OsGuess); sources.Add("NetBIOS"); }
        if (name.Contains("desktop-")) { c.OsGuess = Best("Windows 10/11", c.OsGuess); sources.Add("hostname"); }
        if (name.Contains("macbook") || name.Contains("imac") || name.Contains("macmini"))
        { c.OsGuess = Best("macOS", c.OsGuess); sources.Add("hostname"); }
        if (name.Contains("iphone")) { c.OsGuess = Best("iOS", c.OsGuess); sources.Add("hostname"); }
        if (name.Contains("ipad")) { c.OsGuess = Best("iPadOS", c.OsGuess); sources.Add("hostname"); }
        if (name.Contains("android") || name.Contains("chromecast")) { c.OsGuess = Best("Android", c.OsGuess); sources.Add("hostname"); }

        if (info != null)
        {
            if (!string.IsNullOrEmpty(info.OsHint)) c.OsGuess = Best(info.OsHint, c.OsGuess);
            if (!string.IsNullOrEmpty(info.Model)) c.Model = info.Model;
            if (!string.IsNullOrEmpty(info.Manufacturer) && string.IsNullOrEmpty(vendor))
                vendor = info.Manufacturer;
            sources.AddRange(info.Sources);
        }

        // ---- Model -------------------------------------------------------------
        if (c.Model.Length == 0 && d.NameSource == "NetBIOS" && d.Hostname.Length > 0)
            c.Model = ""; // NetBIOS gives a name, not a model - keep empty, honest.

        // ---- Device type -------------------------------------------------------
        if (d.IsGateway) c.Type = DeviceCategory.Router;
        else if (info != null && info.TypeHint != DeviceCategory.Other) c.Type = info.TypeHint;
        else c.Type = GuessTypeFromNameAndVendor(name, vendor);

        // ---- Confidence scoring ------------------------------------------------
        int score = 0;
        if (!string.IsNullOrEmpty(d.CustomName)) score += 30;            // user-confirmed identity
        else
        {
            if (d.NameSource == "NetBIOS") score += 30;
            else if (d.NameSource == "DHCP") score += 25;
            else if (d.NameSource == "DNS") score += 15;
            if (info != null)
            {
                if (info.Sources.Contains("mDNS")) score += 30;
                if (info.Sources.Contains("SSDP")) score += 25;
                if (info.Sources.Contains("WS-Discovery")) score += 15;
            }
            if (!string.IsNullOrEmpty(vendor)) score += 10;
            if (!string.IsNullOrEmpty(c.OsGuess)) score += 10;
            if (!string.IsNullOrEmpty(c.Model)) score += 10;
        }
        if (d.IsLocal || d.IsGateway) score = Math.Max(score, 80);
        c.Confidence = Math.Clamp(score, 0, 100);

        foreach (var s in sources.Distinct(StringComparer.OrdinalIgnoreCase)) c.Sources.Add(s);
        return c;
    }

    /// <summary>Second pass when more evidence arrives: keeps the better of two classifications.</summary>
    public static string MergeOs(string current, string candidate) => Best(candidate, current);

    private static string Best(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(fallback) ? (preferred ?? "") : fallback;

    private static DeviceCategory GuessTypeFromNameAndVendor(string name, string vendor)
    {
        // Strong name signals first.
        if (name.Contains("iphone")) return DeviceCategory.Phone;
        if (name.Contains("ipad")) return DeviceCategory.Tablet;
        if (name.Contains("android")) return DeviceCategory.Phone;
        if (name.Contains("chromecast") || name.Contains("roku") || name.Contains("fire-tv") ||
            name.Contains("firetv") || name.Contains("appletv") || name.Contains("bravia") ||
            name.Contains("smart-tv") || name.Contains("smarttv") || name.EndsWith("-tv"))
            return DeviceCategory.TV;
        if (name.Contains("playstation") || name.Contains("ps4") || name.Contains("ps5") ||
            name.Contains("xbox") || name.Contains("switch") || name.Contains("nintendo"))
            return DeviceCategory.Console;
        if (name.Contains("printer") || name.Contains("laserjet") || name.Contains("pixma") ||
            name.Contains("epson") || name.Contains("scan"))
            return DeviceCategory.Printer;
        if (name.Contains("echo") || name.Contains("nest") || name.Contains("hue") ||
            name.Contains("sonos") || name.Contains("plug") || name.Contains("bulb") ||
            name.Contains("esp_") || name.Contains("shelly") || name.Contains("tuya") ||
            name.Contains("thermostat") || name.Contains("cam"))
            return DeviceCategory.IoT;
        if (name.Contains("macbook") || name.Contains("imac") || name.Contains("desktop-") ||
            name.Contains("pc-") || name.Contains("laptop") || name.Contains("notebook"))
            return DeviceCategory.PC;

        // Vendor signals.
        if (!string.IsNullOrEmpty(vendor))
        {
            foreach (var (prefix, cat) in VendorTypes)
                if (vendor.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return cat;
            if (PrinterVendors.Any(v => vendor.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                return DeviceCategory.Printer;   // these brands are overwhelmingly printers
            if (vendor.StartsWith("Apple", StringComparison.OrdinalIgnoreCase))
                return DeviceCategory.PC;        // generic Apple - refined by hostname/TTL signals
            if (PhoneVendors.Any(v => vendor.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                return DeviceCategory.Phone;     // phone-first vendors
            if (vendor.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase))
                return DeviceCategory.PC;
            if (vendor.StartsWith("Espressif", StringComparison.OrdinalIgnoreCase))
                return DeviceCategory.IoT;
        }
        return DeviceCategory.Other;
    }
}
