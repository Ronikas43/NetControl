# NetControl

**A NetCut-inspired network discovery, monitoring, and management application for Windows 10 and Windows 11.**

NetControl is a desktop application built with **C# / .NET 8 WPF** that allows you to discover, identify, monitor, and manage devices connected to your local network.

It combines network scanning, device identification, connection monitoring, bandwidth tracking, security alerts, and internet access control in one application.

NetControl is designed to work independently of router administration access for its core network discovery and monitoring features. Optional router integration and network enforcement provide additional functionality.

---

## Quick Start

### Option A: Run the prebuilt application

1. Install the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
2. For ARP-related functionality, install [Npcap](https://npcap.com).
3. Extract `NetControl-win-x64.zip`.
4. Run `NetControl.exe`.

### Option B: Build from source

Requirements:
- Windows 10 1809 or newer.
- .NET 8 SDK.
- Visual Studio 2022 or another compatible development environment (optional).

Run the included build script:

```bat
build.bat
```

Or build manually:

```bat
dotnet publish src\NetControl\NetControl.csproj -c Release -r win-x64 --self-contained false
```

The published application will be located in the project's publish directory.

### Administrator Permissions

NetControl can run without administrator privileges for everyday network scanning, monitoring, and dashboard functionality.

Administrator privileges are required for certain advanced operations, including:
- ARP enforcement.
- Changing or restoring a network adapter's MAC address.

NetControl checks its privilege level and reports the available functionality in the application.

---

## Features

| Feature | Description |
|---|---|
| Network Scanner | Discover devices using ARP scanning, ICMP ping, ARP cache information, and reverse DNS. |
| Device Identification | Identify manufacturers, hostnames, MAC addresses, device categories, and other available information. |
| Device Management | Rename devices, assign categories, mark trusted devices, and add notes. |
| Internet Access Control | Block and restore device connectivity through supported enforcement methods. |
| OpenWrt Integration | Manage per-device firewall rules through OpenWrt router integration. |
| ARP Enforcement | Optional network enforcement engine using Npcap. |
| UPnP Integration | Discover compatible gateways and retrieve available WAN information. |
| Bandwidth Monitoring | Monitor live upload/download speeds and network traffic history. |
| Connection History | Track device connections, disconnections, IP changes, and management actions. |
| Scheduler | Configure recurring device access schedules. |
| Security Monitoring | Detect unknown devices and suspicious ARP activity. |
| MAC Address Tools | View adapter MAC addresses, identify manufacturers, and manage supported MAC changes. |
| Dashboard | View network statistics, device information, traffic, and recent activity. |
| Notifications | Receive Windows notifications and in-app alerts. |
| Settings | Configure scanning, notifications, adapters, appearance, and other application preferences. |

---

## Device Discovery & Identification

NetControl combines multiple discovery methods to identify devices on your local network.

Available identification techniques include:

- ARP scanning.
- ICMP ping discovery.
- ARP cache inspection.
- Reverse DNS lookup.
- Hostname discovery.
- MAC address identification.
- IEEE OUI manufacturer lookup.
- Network protocol discovery.
- Device categorization.

The application can organize discovered devices into categories such as:

- Computers and laptops.
- Smartphones and tablets.
- Gaming consoles.
- Smart TVs.
- Printers.
- Routers and network equipment.
- IoT devices.

Identification accuracy depends on the information exposed by each device. Exact models and operating systems cannot always be determined.

---

## Internet Access Control

NetControl supports multiple approaches to managing device internet connectivity.

### OpenWrt Integration

Connect to a compatible OpenWrt router and manage device access through firewall rules.

Features include:
- Per-device blocking and restoring.
- MAC-based firewall rules.
- Firewall rule management.
- Rule status verification.
- OpenWrt SQM capability detection.

Requires compatible OpenWrt router access.

### ARP Enforcement

NetControl also includes an optional ARP-based enforcement engine using Npcap.

This functionality requires administrator privileges and compatible network conditions.

The application reports enforcement status and restoration results rather than assuming an operation succeeded.

### Automatic Restoration

Active enforcement sessions are tracked, with automatic cleanup attempted when the application exits.

---

## Security Monitoring

NetControl includes network security monitoring features designed to help identify unexpected devices and suspicious ARP activity.

Features include:
- Unknown-device alerts.
- Known-device tracking.
- Gateway MAC baseline learning.
- ARP spoofing detection.
- Periodic gateway verification.
- Security event history.
- Windows and in-app notifications.

Security alerts are based on observed network activity and should not be treated as guaranteed proof of an attack.

---

## Bandwidth Monitoring

Monitor network adapter traffic directly from your Windows computer.

Features include:
- Live upload speed.
- Live download speed.
- Network traffic history.
- Session traffic totals.
- Live speed charts.

Router-dependent bandwidth policies require compatible OpenWrt SQM functionality.

---

## Connection History & Scheduling

Keep track of network activity and device management events.

Connection history includes:
- Device connections and disconnections.
- IP address changes.
- Block and restore events.
- Scheduling events.
- Manual management actions.

The scheduler supports recurring access windows, weekday selection, and overnight schedules, with automatic restoration when a scheduled window ends.

---

## Technical Stack

| Technology | Purpose |
|---|---|
| C# 12 | Main programming language |
| .NET 8 | Application framework |
| WPF | Windows desktop interface |
| CommunityToolkit.Mvvm | MVVM architecture |
| Microsoft.Data.Sqlite | Local database |
| SharpPcap | Packet capture and ARP functionality |
| Npcap | Network packet access |
| System.Management | Windows network adapter information |
| Windows Toast Notifications | Desktop notifications |
| IEEE OUI Database | MAC manufacturer identification |

The application uses SQLite for persistent device information, connection history, security events, and scheduling data.

---

## System Requirements

- Windows 10 version 1809 or newer.
- Windows 11 supported.
- x64 processor.
- .NET 8 Desktop Runtime.
- Network adapter with local network connectivity.

Additional requirements:
- Npcap for ARP packet capture and enforcement.
- Administrator privileges for certain advanced operations.
- Compatible OpenWrt router for router-based access control and bandwidth policies.

---

## Limitations

Some functionality depends on network hardware, drivers, permissions, and router compatibility.

- Device identification cannot always determine exact hardware models.
- MAC randomization can make manufacturer identification less reliable.
- Firewalls may prevent certain discovery methods.
- Non-OpenWrt routers are not supported for OpenWrt-specific firewall management.
- Per-device bandwidth information requires compatible router support.
- MAC address changes depend on network adapter driver support.
- Network enforcement may not work on every network configuration.

NetControl aims to report actual results and limitations rather than displaying misleading success messages.

---

## Privacy

NetControl is designed to operate locally on your computer.

- Device records are stored locally.
- Application settings are stored in `%APPDATA%\NetControl`.
- SQLite is used for local data persistence.
- No telemetry is intentionally collected.
- No cloud account is required.
- Router communication only occurs when you configure and use router integration.

---

## Developer

NetControl is an independently developed project maintained by a single developer.

---

## Disclaimer & Responsible Use

NetControl is provided for educational purposes and legitimate network administration.

By using this software, you acknowledge that:

- You are responsible for ensuring you have authorization to scan, monitor, or manage the network on which you use NetControl.
- You must not use the application to interfere with networks or devices without permission.
- Public Wi-Fi networks, cafés, schools, workplaces, and other shared networks may have restrictions on network scanning and management.
- You are solely responsible for your actions and how you use the software.
- I, as the sole developer of NetControl, am not responsible for misuse, unauthorized activity, damages, losses, or other consequences resulting from the use of this application.

---

## License

This project is distributed under the [The Unlicense](LICENSE).

See the `LICENSE` file for details.

Made With AI
