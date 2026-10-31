# NetControl

**A Windows network discovery and monitoring application that helps you identify devices connected to your local network.**

NetControl scans your local network, discovers connected devices, gathers identifying information, and helps you understand what's connected without requiring access to your router's administration panel.

## Features

- **Network Scanning** - Discover devices connected to your local network.
- **Device Identification** - Identify computers, laptops, smartphones, tablets, TVs, consoles, printers, and IoT devices.
- **MAC Address Lookup** - Identify device manufacturers using IEEE OUI databases.
- **Hostname Discovery** - Discover device names using available network protocols.
- **Operating System Detection** - Estimate operating systems through network fingerprinting.
- **Device Fingerprinting** - Gather available information about device types and models.
- **Protocol Discovery** - Support for ARP, mDNS, NetBIOS, SSDP/UPnP, and WS-Discovery.
- **Device Monitoring** - Track online/offline status and newly discovered devices.
- **Device History** - Keep track of previously discovered devices and last-seen timestamps.
- **Identification Confidence** - Display confidence levels based on collected device information.

## Requirements

- Windows 11
- Local network connection
- Administrator privileges may be required for certain scanning methods.

## How It Works

NetControl uses multiple network discovery and identification techniques to collect information about devices on your local network.

It combines available information such as IP addresses, MAC addresses, manufacturer data, hostnames, and network services to identify devices as accurately as possible.

No software installation on discovered devices is required.

## Limitations

Some devices may restrict network discovery or hide identifying information.

- Exact device models may not always be detectable.
- MAC randomization can make manufacturer identification less reliable.
- Firewalls may block certain discovery protocols.
- Operating system identification is not always guaranteed.

NetControl reports only information it can actually obtain.

## Project Goals

The goal of NetControl is to make local network discovery and device identification easier by providing a centralized interface for understanding and monitoring connected devices.

## Developer

NetControl is independently developed and maintained by a single developer.

## Disclaimer

NetControl is provided for educational purposes and authorized network monitoring only.

By using this software, you agree that:

- You are responsible for ensuring you have permission to scan and monitor the network you use NetControl on.
- You must not use this software for unauthorized access, disruption, or interference with networks or devices.
- I, as the sole developer of NetControl, am not responsible for any misuse, damage, loss, or consequences resulting from the use of this software.
- You use NetControl entirely at your own risk.

I am not responsible for actions performed by users with this software, including activities conducted on public Wi-Fi networks, cafés, schools, workplaces, or other networks without permission.

**Always use NetControl responsibly and only on networks you own or have explicit permission to monitor.**
