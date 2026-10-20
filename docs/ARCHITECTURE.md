# OpenMesh Gaming - Technical Architecture

OpenMesh Gaming is an autonomous, self-hosted peer-to-peer virtual LAN solution specifically designed for low-latency multiplayer gaming, LAN emulation, and censorship-resilient mesh networking.

---

## 1. System Overview

```
                      +---------------------------------------+
                      |       OpenMesh Server Node            |
                      | (Raspberry Pi / Linux Server)         |
                      |                                       |
                      |  +---------------------------------+  |
                      |  |     ZeroTier Controller         |  |
                      |  |   Subnet: 10.147.20.0/24        |  |
                      |  |   Network: <NodeID>000001       |  |
                      |  +---------------------------------+  |
                      |  |     Custom Moon Root Node       |  |
                      |  |   Stable Endpoints: LAN + WAN   |  |
                      |  +---------------------------------+  |
                      |  |     Python 3 Management API     |  |
                      |  |   Web UI & Telemetry (Port 8080)|  |
                      |  +---------------------------------+  |
                      +-------------------+-------------------+
                                          |
                        +-----------------+-----------------+
                        | (UDP 9993 / Moon Rendezvous)     | (TCP 8080 / API)
                        v                                   v
+-----------------------------------+   Direct P2P UDP    +-----------------------------------+
|      Windows Gaming Client A      | <=================> |      Windows Gaming Client B      |
|  (C# WinForms / ZeroTier Engine)  |   (Zero Latency)    |  (C# WinForms / ZeroTier Engine)  |
|  Virtual IP: 10.147.20.101        |                     |  Virtual IP: 10.147.20.102        |
+-----------------------------------+                     +-----------------------------------+
```

---

## 2. Autonomous Network Controller

Unlike standard ZeroTier setups that rely on `my.zerotier.com` cloud servers, OpenMesh Gaming hosts an **autonomous on-premise controller** embedded within the `zerotier-one` daemon on the server:

- **Network ID Derivation:** A ZeroTier network ID is always 16 hex characters: the 10-hex Node ID of the controller followed by a 6-hex network index (defaulting to `000001`).
- **REST Provisioning:** During installation (`install.sh`), the script calls the local controller REST endpoint:
  ```http
  POST http://127.0.0.1:9993/controller/network/<NodeID>000001
  X-ZT1-Auth: <authtoken.secret>
  ```
- **IP Assignment:** The controller manages an autonomous DHCP-like pool (e.g., `10.147.20.10` to `10.147.20.250`).
- **Zero Cloud Dependence:** Network membership, authentication, and IP allocation function 100% offline without requiring internet access.

---

## 3. Custom Moon Root Infrastructure

In ZeroTier terminology:
- **Planets:** The central root servers maintained by ZeroTier Inc. worldwide. If an internet outage, DPI firewall, or national internet blackout occurs, planets become unreachable.
- **Moons:** Custom federated root servers under your complete administrative control.

### How OpenMesh Moon Operates:
1. **Generation:** OpenMesh uses `zerotier-idtool initmoon` to generate a Moon identity definition from the server's local identity keys.
2. **Dual Endpoints (LAN + WAN):**
   ```json
   {
     "id": "10-digit-node-id",
     "roots": [
       {
         "identity": "...",
         "stableEndpoints": [
           "192.168.1.100/9993",
           "192.168.31.50/9993",
           "203.0.113.195/9993"
         ]
       }
     ]
   }
   ```
   Both local LAN IP addresses and public WAN addresses are embedded into the signed Moon root.
3. **Smart Routing:**
   - When players are on the **same local Wi-Fi / Ethernet**, packets route directly at wire speed (0–1 ms ping).
   - When remote players connect **over the Internet**, packets route via the public endpoint.
4. **Client Bootstrap:** When a Windows client launches, it downloads `/download/moon` from the server API, places it into `C:\ProgramData\ZeroTier\One\moons.d\`, and restarts the ZeroTier service.

---

## 4. NAT Traversal & Direct Peer-to-Peer Routing

OpenMesh utilizes UDP hole punching to establish direct, unmediated peer-to-peer tunnels between gaming PCs:

1. **Rendezvous Phase:** When Client A wishes to transmit to Client B, both nodes query the Moon root via UDP 9993 to discover each other's public and private socket endpoints.
2. **Hole Punching:** Both clients transmit synchronized UDP packets to each other's endpoints to open stateful NAT mappings in their respective routers.
3. **Direct P2P Link:** Once the NAT binding succeeds, traffic flows directly between Client A and Client B without touching the server CPU or bandwidth.
4. **Relay Fallback:** In symmetrical NAT scenarios where direct hole punching is mathematically blocked by router firewalls, the Moon server automatically acts as a deterministic, low-latency relay.

---

## 5. Security & Access Control Model

### Member Lifecycle
```
[ New Client Connects ] ──> Query /api/client_check
                               │
               ┌───────────────┴───────────────┐
               ▼                               ▼
       [ Is Node Banned? ]             [ Node Not Banned ]
               │                               │
        Yes: Block Access               Auto-Authorize via Controller API
        Display Ban Screen              Assign IP: 10.147.20.xxx
                                        Add to Active Players List
```

### Kick vs. Ban Architectural Distinction:
- **Kick (Temporary Session Ejection):**
  - Revokes authorization on the controller for 1 second.
  - Sends immediate `/api/client_check` event with timestamp.
  - Client leaves network gracefully and resets interface.
  - The player is free to click "Connect" again without administrative intervention.
- **Ban (Permanent Block):**
  - Node ID is written to `banned_members.json`.
  - Node authorization is permanently set to `authorized: false` on controller.
  - Client application locks into `⛔ BANNED BY ADMIN` state.
  - Reconnection attempts are rejected until administrator clicks **Unban** in dashboard.

---

## 6. Windows Client Architecture

The client is designed as a standalone, zero-dependency C# .NET Windows Forms executable:
- **Runtime Target:** Built for standard .NET Framework 4.0/4.5+ (natively pre-installed on every Windows 7, 8, 10, and 11 installation).
- **Embedded UAC Manifest:** Automatically requests elevation so it can control `ZeroTierOneService` and write to `ProgramData\ZeroTier\One\moons.d`.
- **Self-Healing Connectivity:**
  - Auto-detects server across LAN subnets via multi-threaded port probing.
  - Real-time ICMP ping engine monitoring host and core latency.
  - Background polling thread verifying member status every 3 seconds.
