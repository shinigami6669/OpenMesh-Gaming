# ⚡ OpenMesh Gaming

<p align="center">
  <b>Autonomous, Self-Hosted Virtual LAN Mesh & Low-Latency Gaming Platform</b><br>
  <i>Resilient to Internet Outages • Zero Cloud Dependency • 1-Click Setup</i>
</p>

<p align="center">
  <a href="README.md"><b>English</b></a> • <a href="README.fa.md"><b>فارسی</b></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square" alt="License: MIT">
  <img src="https://img.shields.io/badge/Platform-Linux%20%7C%20Raspberry%20Pi-E95420.svg?style=flat-square&logo=linux" alt="Platform: Linux">
  <img src="https://img.shields.io/badge/Client-Windows%20Forms%20(C%23)-0078D4.svg?style=flat-square&logo=windows" alt="Client: Windows">
  <img src="https://img.shields.io/badge/Core-ZeroTier%20Autonomous%20Mesh-orange.svg?style=flat-square" alt="ZeroTier Autonomous Mesh">
  <img src="https://img.shields.io/badge/Language-Python%203%20%26%20C%23-2b5b84.svg?style=flat-square" alt="Languages">
</p>

---

## 📖 Overview / معرفی پروژه

**OpenMesh Gaming** is an open-source, production-ready virtual LAN mesh networking suite engineered for high-performance LAN multiplayer gaming, remote co-op, and offline peer-to-peer connectivity.

Traditional solutions (Hamachi, Radmin VPN, or ZeroTier Cloud) rely heavily on centralized cloud servers in other countries. When high ping, network congestion, or strict DPI firewalls cause disconnections, your game sessions drop. 

**OpenMesh Gaming eliminates all third-party cloud dependencies**:
- Your Linux server (e.g., Raspberry Pi) acts as an **autonomous Network Controller** and a **Custom Moon Root Node**.
- Players connect directly to each other via peer-to-peer UDP hole punching.
- Local LAN traffic routes at 0–1 ms wire speed, while friends connecting over the Internet via router port forwarding experience rock-solid, direct gaming connections.

> **توضیح فارسی:**
> این پروژه یک پلتفرم گیمینگ و شبکه مش همتا‌به‌همتا (P2P) کاملاً مستقل و متن‌باز است که روی رزبری پای یا سرور لینوکس نصب می‌شود. برخلاف ردمین یا هاماچی، به هیچ سرور خارجی یا خارجی وابسته نیست؛ سرور و کنترلر تماماً روی سیستم خودتان اجرا شده و از یک روت مستقل (Moon) استفاده می‌کند تا در شرایط اختلال اینترنت یا شبکه محلی، پینگ مینیمم (کمتر از ۵ میلی‌ثانیه در شبکه محلی و اتصال مستقیم روی اینترنت) و پایداری ۱۰۰ درصدی فراهم کند.

---

## ✨ Features

- **🚀 1-Click Linux Installer (`install.sh`):**
  - Fully automated setup for Raspberry Pi OS, Debian, Ubuntu, and Linux Mint.
  - Automatically provisions the ZeroTier controller daemon, assigns network IP pools (`10.147.20.0/24`), creates a signed Moon root, and sets up a `systemd` background service.
- **🛡️ 100% Autonomous & Offline-Ready:**
  - Operates flawlessly on isolated local offline LANs (no public internet needed).
  - Simultaneously supports remote players over the Internet via Static WAN IP or DDNS.
- **🎛️ Cyber Dark Web Management Dashboard:**
  - Modern, responsive HTML5 web interface (no Node.js or heavy frameworks required; powered by pure Python 3).
  - Real-time connected players table with IP copy buttons, ping indicators, and online status.
  - **Kick vs. Ban separation:** Kick players from current session without banning them, or permanently Ban/Unban rule-breakers with 1 click.
  - Moon auto-regeneration modal with dual LAN/WAN endpoint coexistence.
  - Built-in router port forwarding helper.
- **🖥️ 1-Click C# Windows Client:**
  - Cyber-dark UI with real-time ping monitors for Server Core, Host/Peer, and assigned Virtual IP.
  - Auto-discovers server across local subnets or connects via configured public IP/domain.
  - Automatically downloads and syncs the server's signed Moon root without manual configuration.
  - Builds in seconds using the native C# compiler pre-installed on Windows (`csc.exe`).

---

## 🏗️ Architecture

```
                      +---------------------------------------+
                      |         OpenMesh Server Node          |
                      |  (Raspberry Pi / Linux Server)        |
                      |                                       |
                      |  • Embedded Controller (10.147.20.0)  |
                      |  • Custom Moon Root (Port UDP 9993)   |
                      |  • Web API & Dashboard (Port TCP 8080)|
                      +-------------------+-------------------+
                                          |
                         +----------------+----------------+
                         |                                 |
                         v                                 v
        +---------------------------------+   Direct P2P  +---------------------------------+
        |     Gaming Client PC #1         | <===========> |     Gaming Client PC #2         |
        | (Windows 10/11 - 10.147.20.101) |   (< 5ms)     | (Windows 10/11 - 10.147.20.102) |
        +---------------------------------+               +---------------------------------+
```
*For deep technical architecture details, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).*

---

## 🚀 Quick Start Guide

### 1. Server Installation (Linux / Raspberry Pi)

Run the automated installer on your server:

```bash
git clone https://github.com/your-username/OpenMesh-Gaming.git
cd OpenMesh-Gaming/server
sudo chmod +x install.sh uninstall.sh
sudo ./install.sh
```

During installation, you will be prompted for:
1. **Admin Username & Password:** Used to log into the web dashboard.
2. **Dashboard Web Port:** Default is `8080`.
3. **Public WAN IP or Domain (Optional):** Enter your public static IP or DDNS if players will connect from other houses over the Internet.

Once installed, the dashboard will be live at:
```
http://<your-server-ip>:8080
```

To manage the server service:
```bash
sudo systemctl status openmesh-server
sudo systemctl restart openmesh-server
```

To update OpenMesh Server to the latest version at any time:
```bash
cd OpenMesh-Gaming/server
sudo ./update.sh
```

---

### 2. Router Port Forwarding (For WAN / Internet Play)

If friends are connecting to your server from their homes over the Internet, forward the following ports in your router settings to your server's local LAN IP:

| Port | Protocol | Purpose |
| :--- | :--- | :--- |
| **`9993`** | **UDP** | ZeroTier Mesh Traffic & Moon Root |
| **`8080`** | **TCP** | Web Dashboard & Client Handshake API |

*For step-by-step router configuration instructions, see [docs/PORT_FORWARDING.md](docs/PORT_FORWARDING.md).*

---

### 3. Windows Client Setup

#### Option A: Build with 1-Click
No Visual Studio or external SDK required! Simply run the build script in `client/`:

```cmd
cd client
build.bat
```
The script uses Windows' built-in C# compiler (`csc.exe`) and generates `OpenMeshGaming.exe`.

#### Option B: Run and Connect
1. Launch `OpenMeshGaming.exe` (run as Administrator).
2. If connecting locally on the same Wi-Fi, the client will **auto-discover** the server.
3. If connecting from outside over the Internet:
   - Click the **`⚙️ Settings`** button in the top right.
   - Enter your host's **Public Static IP or DDNS Domain**.
   - Click **Save Settings**.
4. Click **`CONNECT TO GAMING LAN`**.
5. The client downloads the server's Moon root, joins the network, and assigns your virtual gaming IP (e.g. `10.147.20.xxx`).

---

## 📁 Repository Structure

```
OpenMesh-Gaming/
├── LICENSE                    # MIT License
├── README.md                  # Comprehensive Documentation
├── .gitignore                 # Binary, credential & secret exclusions
├── docs/
│   ├── ARCHITECTURE.md        # Technical Mesh & Moon Deep Dive
│   └── PORT_FORWARDING.md     # Step-by-step Router NAT & Port Forwarding Guide
├── server/
│   ├── install.sh             # 1-Click Automated Linux Installer
│   ├── uninstall.sh           # Clean Service & Config Uninstaller
│   ├── server.py              # Zero-Dependency Python 3 Dashboard & Web API
│   ├── config.example.json    # Example configuration file
│   └── openmesh-server.service# Systemd Unit File template
└── client/
    ├── build.bat              # Automated C# compiler batch script
    ├── config.example.json    # Client settings template
    └── src/
        ├── app.manifest       # UAC Elevation Manifest
        └── Program.cs         # Cyber Dark Windows Forms C# Client Source
```

---

## 🔒 Security & Privacy Notice

- **No Data Leakage:** This repository is clean of any hardcoded private keys, passwords, node IDs, or personal IP addresses.
- **Admin Password:** Your dashboard credentials are stored locally in `/opt/openmesh/config.json`.
- **Zero Cloud Tracking:** All telemetry, member lists, and network keys remain strictly on your own hardware.

---

## 📜 License

This project is licensed under the **MIT License** - see the [LICENSE](LICENSE) file for details.
