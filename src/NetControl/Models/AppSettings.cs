using System;
using System.Collections.Generic;

namespace NetControl.Models;

public class NotificationSettings
{
    public bool ToastEnabled { get; set; } = true;
    public bool NewDevice { get; set; } = true;
    public bool DeviceOffline { get; set; } = false;
    public bool UnknownDevice { get; set; } = true;
    public bool ArpAlert { get; set; } = true;
    public bool ScheduleActions { get; set; } = true;
    public bool OperationFailed { get; set; } = true;
}

public class RouterSettings
{
    public bool Enabled { get; set; } = false;
    public string Kind { get; set; } = "OpenWrt";    // OpenWrt | UPnP
    public string Ip { get; set; } = "";
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    public bool UseHttps { get; set; } = false;
}

public class ArpSettings
{
    public bool EnforcementEnabled { get; set; } = false; // master switch for ARP spoofing mode
    public int SpoofIntervalMs { get; set; } = 1000;
    public string InterfaceMac { get; set; } = "";        // which NIC to send from; empty = auto
    public bool PassiveDetection { get; set; } = true;
}

public class AppSettings
{
    public bool AutoScan { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 30;
    public int PingTimeoutMs { get; set; } = 600;
    public int ScanConcurrency { get; set; } = 64;
    public bool DetectHostnames { get; set; } = true;
    public bool DetectMdns { get; set; } = true;        // mDNS/DNS-SD (Chromecast, AirPlay, printers...)
    public bool DetectSsdp { get; set; } = true;        // SSDP/UPnP root devices (TVs, NAS, routers...)
    public bool DetectWsd { get; set; } = true;         // WS-Discovery (Windows PCs, network printers)
    public bool UseDhcpLeases { get; set; } = true;     // DHCP hostnames from the OpenWrt lease table
    public bool ShowOfflineDevices { get; set; } = true;
    public string SelectedAdapterMac { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public int HistoryRetentionDays { get; set; } = 30;
    public NotificationSettings Notifications { get; set; } = new();
    public RouterSettings Router { get; set; } = new();
    public ArpSettings Arp { get; set; } = new();
    public List<string> TrustedVendors { get; set; } = new();

    public static string FilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetControl", "settings.json");
}
