# NetControl

A complete, NetCut-inspired network management application for **Windows 11** (also runs on Windows 10 1809+),
built with **C# / .NET 8 WPF**.

NetControl discovers every device on your local network by itself (ARP sweep + ICMP ping + reverse DNS,
independent of any router), lets you manage devices, and offers internet access control through
**OpenWrt router firewall rules** or an opt-in **ARP enforcement engine** (Npcap), with honest status
reporting everywhere - an operation either really happened, or NetControl tells you it failed.

---

## Quick start

### Option A - run the prebuilt binary (recommended)
1. Install the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) - the
   "Desktop Runtime" (not the SDK) is required because the shipped build is framework-dependent.
2. Optional but recommended for ARP features: install [Npcap](https://npcap.com) (choose "Install in
   WinPcap API-compatible mode" during setup).
3. Unzip `NetControl-win-x64.zip` and run `NetControl.exe`.

### Option B - build from source
```bat
:: on Windows with the .NET 8 SDK installed
build.bat          :: restores, builds Release and publishes to publish\NetControl-win-x64
```
or manually:
```bat
dotnet publish src\NetControl\NetControl.csproj -c Release -r win-x64 --self-contained false
```
The application binary is `src\NetControl\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\NetControl.exe`.

### Administrator rights
NetControl runs fine as a normal user for **scanning, monitoring and the dashboard**.
Administrator rights are required only for:
- ARP enforcement (sending crafted ARP packets),
- changing/restoring an adapter MAC address.

NetControl detects the privilege level itself and shows it in Security and Settings pages.

---

## Feature overview

| Area | What it does | Works without router? | Works without admin? |
|---|---|---|---|
| Device scanner | ARP sweep + ICMP ping + ARP-cache merge + reverse DNS, auto-refresh (configurable interval) and manual rescan; detects new/left devices | Yes | Yes |
| Device management | Custom names, categories (PC/Phone/Tablet/Console/Router/IoT/Printer/TV), trusted flag, notes, first/last seen, copy info, SQLite persistence | Yes | Yes |
| Internet access control | Block/restore per device or multi-select; restore-all; scheduling; real status display | Router rules or ARP needed | See ARP |
| Router integration (OpenWrt) | ubus JSON-RPC login, per-MAC firewall DROP rules, uci commit + apply, rule lookup & removal, SQM capability detection | No (router feature) | Yes |
| UPnP IGD | SSDP discovery, WAN status, IGD2 total byte counters (informational; UPnP cannot block per device - stated in UI) | No (router feature) | Yes |
| Bandwidth | Live up/down speeds + 10-minute chart from real adapter counters, session totals, router-based limit policy (requires OpenWrt SQM - honestly reported when unavailable) | Speeds yes | Yes |
| Connection history | Connect/disconnect, IP changes, block/unblock, schedules, manual actions; search, type/date filters, per-device view, clear, retention pruning | Yes | Yes |
| Scheduler | Recurring block/allow windows per device, weekday selection, overnight windows supported, persists in SQLite, auto-revert at window end, active/applied status | Enforcement via router/ARP | See ARP |
| Security | Unknown-device alerts, known-device list, ARP spoofing detection (passive capture + periodic active gateway probes + baseline learning), security event log | Yes (SendARP-based probes work without Npcap) | Yes |
| MAC tools | Local adapter MACs, discovered device MACs, copy, OUI vendor lookup (40,000+ entries bundled from the IEEE database), MAC change/restore via driver registry value + adapter restart | Yes | Change needs admin |
| Dashboard | Totals (all/online/offline/unknown/trusted/blocked), live speed chart, session traffic, recent activity, security alerts, enforcement status | Yes | Yes |
| Notifications | Windows 11 toast notifications + in-app banner, per-event-type toggles | Yes | Yes |
| Settings | Auto scan + interval, ping timeout, concurrency, adapter selection, notifications, dark/light theme, history retention, router config, ARP config, all persisted as JSON | Yes | Yes |

### Internet access control paths (in priority order)
1. **OpenWrt firewall** - NetControl creates a `firewall` uci rule (`src_mac` of the device, `DROP` to
   `wan`), commits and applies it. OpenWrt 21.02+ recommended. Requires rpcd/LuCI RPC access for the
   configured user (root by default).
2. **ARP enforcement** (NetCut-style) - requires Npcap + Administrator. NetControl sends crafted ARP
   replies that make the victim's traffic and the gateway's return traffic stop at this PC, and sends
   corrective ARP packets when you restore access. Every session is visible on the Security page and a
   "Stop all" happens automatically on app exit.
3. **Neither available?** The block/restore operation **fails with a clear message** - it never fakes success.

### ARP spoofing detection
- Passive: while a Npcap capture is bound, any ARP announcement claiming the gateway IP with a foreign
  MAC raises a **Critical** security event and toast.
- Active: every 60 seconds NetControl re-probes the gateway with `SendARP` and compares the answer with
  the learned baseline (works even without Npcap).
- NetControl ignores its own ARP frames so enforcement never triggers its own detector.

---

## Project structure

```
NetControl/
├─ NetControl.sln
├─ build.bat / build.ps1          # one-command Windows build
├─ README.md
├─ data/oui.csv                   # (source copy) IEEE OUI vendor database, 40k entries
├─ publish/NetControl-win-x64/    # ready-to-run framework-dependent build (in the released zip)
└─ src/NetControl/
   ├─ NetControl.csproj           # net8.0-windows10.0.19041.0, WPF, x64
   ├─ App.manifest                # per-monitor DPI, asInvoker (admin checked at runtime)
   ├─ App.xaml / App.xaml.cs      # composition root, theme switching, service wiring
   ├─ Themes/Dark.xaml            # full dark theme (Win11-style palette, restyled controls)
   ├─ Themes/Light.xaml           # light theme variant
   ├─ Themes/Controls.xaml        # converters + shared styles
   ├─ Models/                     # Device (INPC), HistoryEvent, SecurityEvent, Schedule, AppSettings...
   ├─ Services/
   │  ├─ AppServices.cs           # service graph / composition
   │  ├─ NetworkHelper.cs         # SendARP P/Invoke, adapters, arp -a parsing, process helper
   │  ├─ NetworkScanner.cs        # subnet expansion, parallel ARP/ping sweep, hostnames
   │  ├─ DeviceService.cs         # device registry, online/offline + IP-change tracking, history
   │  ├─ ScanManager.cs           # auto-scan timer, adapter selection, gateway learning
   │  ├─ ArpService.cs            # Npcap engine: spoofing/blocking, restore, passive detection
   │  ├─ RouterService.cs         # OpenWrt ubus client (login, uci firewall rules, SQM detect)
   │  ├─ UpnpService.cs           # SSDP + SOAP IGD client (status, WAN byte counters)
   │  ├─ AccessControlService.cs  # composite enforcement with honest failure reporting
   │  ├─ SchedulerService.cs      # recurring windows, apply/revert, persistence
   │  ├─ SecurityService.cs       # unknown devices, ARP alerts, probes, event log
   │  ├─ TrafficMonitor.cs        # 1 Hz adapter byte counters -> speeds + history ring buffer
   │  ├─ NotificationService.cs   # Windows toast notifications
   │  ├─ MacService.cs            # MAC change/restore via driver registry + netsh restart
   │  ├─ OuiService.cs            # IEEE OUI lookup
   │  ├─ SettingsService.cs       # JSON settings persistence
   │  └─ Database.cs              # SQLite schema + queries (devices/history/events/schedules)
   ├─ ViewModels/                 # MVVM (CommunityToolkit.Mvvm), 8 page VMs + MainViewModel
   ├─ Views/                      # MainWindow (sidebar nav) + 8 pages
   └─ Controls/                   # SpeedChart (custom-drawn), converters
```

## Technologies & dependencies
- .NET 8 (WPF, `net8.0-windows10.0.19041.0`), C# 12, MVVM via **CommunityToolkit.Mvvm 8.2.2**
- **Microsoft.Data.Sqlite 8.0.8** - device/history/schedule/event persistence
- **SharpPcap 6.1.0** (Npcap) - ARP packet send/capture for enforcement & detection
- **Microsoft.Toolkit.Uwp.Notifications 7.1.3** - Windows 10/11 toasts
- **System.Management 8.0.0** - adapter information
- IEEE OUI database (bundled, 40,424 manufacturer entries)
- No charts library - the speed chart is a custom-drawn `FrameworkElement` (smaller, theme-aware)

## Features requiring router/AP support
- Blocking/restoring internet **without** ARP enforcement (OpenWrt firewall integration)
- Router bandwidth policies (OpenWrt with `sqm-scripts`; detected at runtime, never faked)
- UPnP WAN traffic counters (any router with a UPnP IGD2 gateway)

Device discovery, monitoring, history, scheduling engine, security detection, MAC tools and the whole
UI work with **no router access at all**.

## Honest-limitation notes (by design, not by accident)
- Non-OpenWrt routers: NetControl does not pretend to block through them. It reports "not supported" and
  suggests OpenWrt or ARP enforcement.
- Per-device bandwidth usage is only shown when the router can provide it; otherwise the page says so.
- MAC changing depends on the NIC driver accepting the `NetworkAddress` registry value; NetControl reads
  back the result and reports the actual MAC.
- The Windows toast subsystem may be unavailable in exotic configurations; banners are always shown in-app.

## Privacy
Everything stays on your machine: SQLite database and settings live in `%APPDATA%\NetControl`.
No telemetry, no cloud calls except the router(s) you configure.

## License / responsible use
Use NetControl only on networks you own or are authorized to administer. ARP enforcement is a powerful
network administration technique (the same one NetCut uses) and interfering with devices on networks
you do not control may be illegal in your jurisdiction.
