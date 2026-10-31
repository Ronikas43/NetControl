using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NetControl.Services;

/// <summary>
/// MAC address OUI (Organizationally Unique Identifier) lookup against the bundled
/// IEEE oui.csv (MA-L entries). The CSV is copied next to the executable.
/// </summary>
public class OuiService
{
    private readonly Dictionary<string, string> _vendors = new(StringComparer.OrdinalIgnoreCase);
    public int EntryCount => _vendors.Count;

    public OuiService() => Load();

    private void Load()
    {
        string path = FindCsv();
        if (path == null) return;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                // Format: Registry,Assignment,"Organization Name","Address"
                var comma = line.IndexOf(',');
                if (comma <= 0) continue;
                var rest = line.Substring(comma + 1);
                var comma2 = rest.IndexOf(',');
                if (comma2 <= 0) continue;
                var assignment = rest.Substring(0, comma2).Trim();
                var namePart = rest.Substring(comma2 + 1);
                if (assignment.Length != 6) continue;
                string name;
                if (namePart.StartsWith("\""))
                {
                    var endQ = namePart.IndexOf('"', 1);
                    if (endQ < 0) continue;
                    name = namePart.Substring(1, endQ - 1);
                }
                else
                {
                    var c = namePart.IndexOf(',');
                    name = c < 0 ? namePart.Trim() : namePart.Substring(0, c).Trim();
                }
                if (name.Length > 0 && !_vendors.ContainsKey(assignment))
                    _vendors[assignment] = name;
            }
        }
        catch
        {
            // Lookup simply returns empty strings if the file is unreadable.
        }
    }

    private static string FindCsv()
    {
        string baseDir = "";
        try { baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ""; } catch { }
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", "oui.csv"),
            Path.Combine(AppContext.BaseDirectory, "oui.csv"),
            Path.Combine(baseDir, "data", "oui.csv"),
            Path.Combine(baseDir, "oui.csv")
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Returns the manufacturer name for a MAC address (any common format), or "".</summary>
    public string Lookup(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";
        var hex = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpperInvariant();
        if (hex.Length < 6) return "";
        return _vendors.TryGetValue(hex.Substring(0, 6), out var v) ? v : "";
    }

    public string LookupOrDefault(string mac)
    {
        var v = Lookup(mac);
        return string.IsNullOrEmpty(v) ? "Unknown manufacturer" : v;
    }
}
