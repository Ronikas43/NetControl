using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>
/// Local adapter MAC management. Changing a MAC on Windows works via the
/// per-driver "NetworkAddress" registry value plus an adapter restart -
/// requires Administrator rights and driver support (stated honestly otherwise).
/// </summary>
public class MacService
{
    private const string NicClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    private static string OverridesPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetControl", "mac_overrides.json");

    public bool IsAdmin => NetworkHelper.IsAdmin();

    public (bool ok, string message) ChangeMac(AdapterInfo adapter, string newMac, out string storedOriginal)
    {
        storedOriginal = "";
        if (!IsAdmin)
            return (false, "Administrator rights are required to change a MAC address. " +
                           "Restart NetControl with 'Run as administrator' and try again.");

        var normalized = NetworkHelper.NormalizeMac(newMac);
        if (string.IsNullOrEmpty(normalized))
            return (false, "The entered MAC address is not a valid 12-digit hex address.");

        if (NetworkHelper.IsBroadcastMac(normalized) || normalized.StartsWith("01:00:5E") || normalized.StartsWith("33:33"))
            return (false, "Multicast/broadcast MAC addresses cannot be assigned to an adapter.");

        var subKey = FindDriverKey(adapter);
        if (subKey == null)
            return (false, $"The driver configuration key for '{adapter.Description}' was not found. " +
                           "This adapter (or its driver) does not support MAC changing through the registry.");

        // Remember the original MAC exactly once.
        SaveOriginalIfNeeded(adapter);

        try
        {
            using var driverKey = Registry.LocalMachine.OpenSubKey(NicClassKey + "\\" + subKey, true);
            if (driverKey == null) return (false, "Could not open the driver key for writing (access denied).");
            driverKey.SetValue("NetworkAddress", normalized.Replace(":", ""), RegistryValueKind.String);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied writing to the registry. Run NetControl as Administrator.");
        }
        catch (Exception ex)
        {
            return (false, $"Registry write failed: {ex.Message}");
        }

        var restart = RestartAdapter(adapter);
        if (!restart.ok)
            return (false, restart.message + " The address is set and applies on the next adapter restart.");

        // Read back what Windows actually reports.
        System.Threading.Thread.Sleep(1500);
        var nowMac = ReadAdapterMac(adapter);
        storedOriginal = GetOriginalMac(adapter);
        return (true, $"MAC address set to {nowMac}. (Original was {storedOriginal})");
    }

    public (bool ok, string message) RestoreMac(AdapterInfo adapter)
    {
        if (!IsAdmin)
            return (false, "Administrator rights are required to restore a MAC address.");

        var original = GetOriginalMac(adapter);
        var subKey = FindDriverKey(adapter);
        if (subKey == null)
            return (false, "Driver configuration key not found.");

        try
        {
            using var driverKey = Registry.LocalMachine.OpenSubKey(NicClassKey + "\\" + subKey, true);
            if (driverKey == null) return (false, "Could not open the driver key (access denied).");
            if (driverKey.GetValue("NetworkAddress") != null)
                driverKey.DeleteValue("NetworkAddress");
        }
        catch (Exception ex)
        {
            return (false, $"Registry update failed: {ex.Message}");
        }

        var restart = RestartAdapter(adapter);
        return (restart.ok, restart.ok
            ? $"Original MAC address {original} restored for '{adapter.Name}'."
            : restart.message);
    }

    public string GetOriginalMac(AdapterInfo adapter)
    {
        try
        {
            var map = LoadOverrides();
            if (map.TryGetValue(AdapterKey(adapter), out var rec)) return rec.OriginalMac;
        }
        catch { }
        return adapter.Mac;
    }

    private (bool ok, string message) RestartAdapter(AdapterInfo adapter)
    {
        // netsh requires the interface name; disable then enable to reload the driver.
        var (out1, code1) = NetworkHelper.RunProcess("netsh.exe",
            $"interface set interface name=\"{adapter.Name}\" admin=disable", 10000);
        System.Threading.Thread.Sleep(1500);
        var (out2, code2) = NetworkHelper.RunProcess("netsh.exe",
            $"interface set interface name=\"{adapter.Name}\" admin=enable", 15000);
        if (code1 != 0)
            return (false, $"Could not disable the adapter (netsh exit {code1}). {out1}".Trim());
        if (code2 != 0)
            return (false, $"Adapter was disabled but could not be re-enabled automatically (exit {code2}). " +
                           "Please re-enable it manually in network connections.");
        return (true, "");
    }

    private string ReadAdapterMac(AdapterInfo adapter)
    {
        try
        {
            var ni = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == adapter.Id || n.Name == adapter.Name);
            return ni == null ? adapter.Mac : NetworkHelper.FormatMac(ni.GetPhysicalAddress()?.GetAddressBytes());
        }
        catch { return adapter.Mac; }
    }

    /// <summary>Returns the registry sub-key name under the NIC class key, or null.</summary>
    private string FindDriverKey(AdapterInfo adapter)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(NicClassKey);
            if (root == null) return null;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;
                var desc = k.GetValue("DriverDesc") as string;
                var cfgId = k.GetValue("NetCfgInstanceId") as string;
                if (string.Equals(desc, adapter.Description, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(cfgId) && string.Equals(cfgId, adapter.Id, StringComparison.OrdinalIgnoreCase)))
                    return sub;
            }
        }
        catch { }
        return null;
    }

    // ---- original MAC memory ----

    private record MacRecord(string OriginalMac, string ChangedTo, DateTime ChangedAt);
    private Dictionary<string, MacRecord> LoadOverrides()
    {
        try
        {
            if (File.Exists(OverridesPath))
                return JsonSerializer.Deserialize<Dictionary<string, MacRecord>>(File.ReadAllText(OverridesPath))
                       ?? new Dictionary<string, MacRecord>();
        }
        catch { }
        return new Dictionary<string, MacRecord>();
    }

    private void SaveOriginalIfNeeded(AdapterInfo adapter)
    {
        try
        {
            var map = LoadOverrides();
            var key = AdapterKey(adapter);
            if (!map.ContainsKey(key))
            {
                map[key] = new MacRecord(adapter.Mac, "", DateTime.Now);
                Directory.CreateDirectory(Path.GetDirectoryName(OverridesPath));
                File.WriteAllText(OverridesPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    private static string AdapterKey(AdapterInfo a) => string.IsNullOrEmpty(a.Id) ? a.Name : a.Id;
}
