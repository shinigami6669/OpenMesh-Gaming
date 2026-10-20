# Router Port Forwarding Guide (WAN Internet Access)

When running **OpenMesh Gaming** across the public Internet (allowing friends to connect from their homes while the server is hosted at your location), your router/modem needs to route incoming traffic to your server (e.g., Raspberry Pi or Linux machine).

---

## 1. Required Ports

| Port | Protocol | Purpose | Destination IP |
| :--- | :--- | :--- | :--- |
| **`9993`** | **UDP** | **ZeroTier Mesh & Moon Root** (Primary gaming traffic & P2P relay) | Server LAN IP (e.g. `192.168.1.100`) |
| **`8080`** | **TCP** | **Web Dashboard & API** (Handshake, Moon download, live telemetry) | Server LAN IP (e.g. `192.168.1.100`) |

> [!NOTE]
> If you customize the Web Dashboard port in `/opt/openmesh/config.json` (e.g., to port `9090` or `80`), forward that custom TCP port instead of `8080`.

---

## 2. Step-by-Step Router Configuration

### Step A: Find Your Server's Local LAN IP
On your Raspberry Pi / Linux server, run:
```bash
ip -4 addr show
# or
hostname -I
```
Look for your active interface (e.g., `eth0` or `wlan0`). Example LAN IP: `192.168.1.100`.

### Step B: Reserve a Static DHCP Lease
To prevent your router from assigning a different LAN IP to your server when it reboots:
1. Open your browser and navigate to your router's gateway (usually `http://192.168.1.1` or `http://192.168.0.1`).
2. Log in with your router credentials (often printed on the bottom of the modem).
3. Find **DHCP Server** -> **Static Lease** / **Address Reservation**.
4. Bind your server's MAC address to its current LAN IP (e.g., `192.168.1.100`).

### Step C: Add Port Forwarding Rules
1. Navigate to **Forwarding**, **Port Forwarding**, **Virtual Server**, or **NAT** in your router menu.
2. Add **Rule 1: ZeroTier Mesh Traffic**
   - **Service Name:** `OpenMesh-UDP`
   - **External Port:** `9993`
   - **Internal Port:** `9993`
   - **Internal IP:** `192.168.1.100` (Your server's LAN IP)
   - **Protocol:** `UDP` (or `ALL / BOTH`)
   - **Status:** `Enabled`
3. Add **Rule 2: Web Dashboard & Handshake API**
   - **Service Name:** `OpenMesh-Web`
   - **External Port:** `8080`
   - **Internal Port:** `8080`
   - **Internal IP:** `192.168.1.100` (Your server's LAN IP)
   - **Protocol:** `TCP`
   - **Status:** `Enabled`
4. Click **Apply** or **Save**.

---

## 3. Public Static IP vs Dynamic DNS (DDNS)

### Scenario 1: You have a Public Static IP (Recommended)
If you purchased a Static Public IP from your Internet Service Provider (ISP):
1. In the OpenMesh Server Web Dashboard (`http://<server-ip>:8080`), go to **Server Settings** -> **Public WAN IP / Domain**.
2. Enter your Public Static IP (e.g., `203.0.113.195`).
3. Click **Regenerate Moon File**. The server will generate a signed Moon definition advertising both your local LAN IP and your Public IP.
4. Remote clients enter your Public Static IP into their client settings and connect directly.

### Scenario 2: Dynamic IP (DDNS)
If your ISP assigns dynamic WAN IPs that change periodically:
1. Use a free DDNS service (e.g., No-IP, DuckDNS, Cloudflare DDNS).
2. Set your DDNS hostname in your router or via a lightweight updater script on your server (e.g., `mygamingmesh.duckdns.org`).
3. Enter `mygamingmesh.duckdns.org` in the OpenMesh Dashboard as your Public Host/Domain.

---

## 4. Verification & Testing

To verify your port forwarding is operational:

### Check TCP Web Port:
Use an external port-checking tool (e.g., [canyouseeme.org](https://canyouseeme.org/)):
- Enter port `8080`.
- If successful, it will report: **Success: I can see your service on port 8080**.

### Test with Windows Client:
1. Open `OpenMeshGaming.exe` on a computer connected to a different network (e.g., mobile hotspot).
2. Click the top-right button `⚙️ ...` and enter your Public Static IP or DDNS hostname.
3. Click **CONNECT TO GAMING LAN**.
4. The client will download the Moon root, join your network, and receive a virtual IP (e.g., `10.147.20.xxx`) within seconds!
