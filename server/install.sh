#!/usr/bin/env bash
# ==============================================================================
# OpenMesh Gaming - 1-Click Server Installer
# Turn any Linux PC or Raspberry Pi into a ZeroTier Moon & Gaming Mesh Controller
# ==============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ANSI Color Codes
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

clear 2>/dev/null || true
echo -e "${CYAN}"
echo "  ██████╗ ██████╗ ███████╗███╗   ██╗███╗   ███╗███████╗███████╗██╗  ██╗"
echo " ██╔═══██╗██╔══██╗██╔════╝████╗  ██║████╗ ████║██╔════╝██╔════╝██║  ██║"
echo " ██║   ██║██████╔╝█████╗  ██╔██╗ ██║██╔████╔██║█████╗  ███████╗███████║"
echo " ██║   ██║██╔═══╝ ██╔══╝  ██║╚██╗██║██║╚██╔╝██║██╔══╝  ╚════██║██╔══██║"
echo " ╚██████╔╝██║     ███████╗██║ ╚████║██║ ╚═╝ ██║███████╗███████║██║  ██║"
echo "  ╚═════╝ ╚═╝     ╚══════╝╚═╝  ╚═══╝╚═╝     ╚═╝╚══════╝╚══════╝╚═╝  ╚═╝"
echo -e "${NC}"
echo -e "         ${GREEN}⚡ 1-Click Autonomous ZeroTier Gaming Mesh Controller ⚡${NC}\n"

if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}[-] Error: This script must be run as root.${NC}"
  echo "    Please run: sudo bash install.sh"
  exit 1
fi

# 1. Install Dependencies
echo -e "${CYAN}[1/6] Installing necessary system packages...${NC}"
apt-get update -qq
apt-get install -y -qq curl jq python3 iproute2 procps >/dev/null

# 2. Install ZeroTier One if missing
echo -e "${CYAN}[2/6] Checking ZeroTier One installation...${NC}"
if ! command -v zerotier-one >/dev/null 2>&1; then
    echo -e "${YELLOW}[*] Installing ZeroTier One via official repository...${NC}"
    curl -s https://install.zerotier.com | bash
else
    echo -e "${GREEN}[+] ZeroTier One is already installed.${NC}"
fi

systemctl enable zerotier-one >/dev/null 2>&1 || true
systemctl start zerotier-one >/dev/null 2>&1 || true

# Wait for ZeroTier daemon and authtoken
echo -e "${CYAN}[3/6] Initializing ZeroTier Controller & Identities...${NC}"
ZT_DIR="/var/lib/zerotier-one"
TOKEN_FILE="${ZT_DIR}/authtoken.secret"

for i in {1..15}; do
    if [ -f "$TOKEN_FILE" ] && zerotier-cli status >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

if [ ! -f "$TOKEN_FILE" ]; then
    echo -e "${RED}[-] Error: Unable to read ZeroTier authtoken at ${TOKEN_FILE}.${NC}"
    exit 1
fi

TOKEN=$(cat "$TOKEN_FILE")
NODE_STATUS=$(zerotier-cli status 2>/dev/null || true)
NODE_ID=$(echo "$NODE_STATUS" | awk '{print $3}')

if [ -z "$NODE_ID" ]; then
    echo -e "${RED}[-] Error: Could not determine ZeroTier local Node ID.${NC}"
    exit 1
fi
echo -e "${GREEN}[+] Detected Server Node ID: ${NODE_ID}${NC}"

# Generate 16-hex Network ID based on Node ID (NodeID + 000001)
NETWORK_ID="${NODE_ID}000001"
echo -e "${GREEN}[+] Autonomous Gaming Network ID: ${NETWORK_ID}${NC}"

# Mesh subnet (must not clash with your real LAN)
if [ -z "$MESH_SUBNET" ]; then
    if [ -t 0 ]; then
        read -p "Enter Mesh subnet [default: 10.147.20.0/24]: " MESH_SUBNET
    fi
    MESH_SUBNET=${MESH_SUBNET:-10.147.20.0/24}
fi

# Derive the address pool from the subnet
MESH_PREFIX=$(echo "$MESH_SUBNET" | cut -d/ -f1)          # e.g. 10.147.20.0
MESH_BITS=$(echo "$MESH_SUBNET" | cut -d/ -f2)           # e.g. 24
MESH_OCTET1=$(echo "$MESH_PREFIX" | cut -d. -f1)
MESH_OCTET2=$(echo "$MESH_PREFIX" | cut -d. -f2)
MESH_OCTET3=$(echo "$MESH_PREFIX" | cut -d. -f3)
POOL_START="${MESH_OCTET1}.${MESH_OCTET2}.${MESH_OCTET3}.1"
POOL_END="${MESH_OCTET1}.${MESH_OCTET2}.${MESH_OCTET3}.254"

# Server static virtual IP inside the mesh
if [ -z "$SERVER_VIRTUAL_IP" ]; then
    if [ -t 0 ]; then
        read -p "Enter Server Virtual IP [default: ${MESH_OCTET1}.${MESH_OCTET2}.${MESH_OCTET3}.177]: " SERVER_VIRTUAL_IP
    fi
    SERVER_VIRTUAL_IP=${SERVER_VIRTUAL_IP:-${MESH_OCTET1}.${MESH_OCTET2}.${MESH_OCTET3}.177}
fi

echo -e "${GREEN}[+] Mesh subnet: ${MESH_SUBNET} (pool ${POOL_START}-${POOL_END})${NC}"
echo -e "${GREEN}[+] Server virtual IP: ${SERVER_VIRTUAL_IP}${NC}"

# 3. Create Network on Local ZeroTier Controller
echo -e "${CYAN}[4/6] Provisioning Network on local ZeroTier Controller...${NC}"
AUTH_HEADER="X-ZT1-Auth: ${TOKEN}"

NET_PAYLOAD=$(cat <<EOF
{
  "name": "OpenMesh-Gaming",
  "private": false,
  "v4AssignMode": {"zt": true},
  "ipAssignmentPools": [
    {
      "ipRangeStart": "${POOL_START}",
      "ipRangeEnd": "${POOL_END}"
    }
  ],
  "routes": [
    {
      "target": "${MESH_SUBNET}",
      "via": null
    }
  ]
}
EOF
)

curl -s -X POST -H "$AUTH_HEADER" -H "Content-Type: application/json" \
  -d "$NET_PAYLOAD" \
  "http://127.0.0.1:9993/controller/network/${NETWORK_ID}" >/dev/null

# Join server node itself to the network
zerotier-cli join "$NETWORK_ID" >/dev/null 2>&1 || true

# Authorize server node and assign its static virtual IP
MEMBER_PAYLOAD=$(cat <<EOF
{
  "authorized": true,
  "ipAssignments": ["${SERVER_VIRTUAL_IP}"]
}
EOF
)
curl -s -X POST -H "$AUTH_HEADER" -H "Content-Type: application/json" \
  -d "$MEMBER_PAYLOAD" \
  "http://127.0.0.1:9993/controller/network/${NETWORK_ID}/member/${NODE_ID}" >/dev/null 2>&1 || true

# 4. Generate Autonomous Moon Root
echo -e "${CYAN}[5/6] Building ZeroTier Moon Root Server...${NC}"
cd "$ZT_DIR"

# Clean local.conf
echo '{"settings": {"primaryPort": 9993}}' > "${ZT_DIR}/local.conf"

raw_moon=$(zerotier-idtool initmoon identity.public)
echo "$raw_moon" > moon.json

# Extract active IP addresses
IP_LIST=()
for ip in $(ip -br a | grep -E '^(eth|wlan|en|wl)' | awk '{print $3}' | cut -d/ -f1); do
    if [ "$ip" != "127.0.0.1" ]; then
        IP_LIST+=("\"$ip/9993\"")
    fi
done

SYS_HOST=$(hostname 2>/dev/null)
if [ -n "$SYS_HOST" ]; then
    IP_LIST+=("\"${SYS_HOST}.local/9993\"")
fi

ENDPOINTS_JSON=$(IFS=, ; echo "${IP_LIST[*]}")

python3 -c "
import json
with open('moon.json', 'r') as f:
    d = json.load(f)
d['roots'][0]['stableEndpoints'] = [${ENDPOINTS_JSON}]
with open('moon.json', 'w') as f:
    json.dump(d, f, indent=2)
"

zerotier-idtool genmoon moon.json >/dev/null
mkdir -p moons.d
MOON_FILE=$(ls 000000*.moon 2>/dev/null | head -n 1)
if [ -n "$MOON_FILE" ]; then
    cp "$MOON_FILE" moons.d/
    echo -e "${GREEN}[+] Moon file generated: ${MOON_FILE}${NC}"
fi

systemctl restart zerotier-one >/dev/null 2>&1 || true

# 5. Configuration Setup
echo -e "\n${CYAN}================================================================${NC}"
echo -e "${YELLOW}           OpenMesh Dashboard Configuration Setup               ${NC}"
echo -e "${CYAN}================================================================${NC}"

# Admin User
if [ -z "$ADMIN_USER" ]; then
    if [ -t 0 ]; then
        read -p "Enter Admin Username [default: admin]: " ADMIN_USER
    fi
    ADMIN_USER=${ADMIN_USER:-admin}
fi

# Admin Password
if [ -z "$ADMIN_PASS" ]; then
    GEN_PASS=$(head /dev/urandom | tr -dc A-Za-z0-9 | head -c 12 ; echo '')
    if [ -t 0 ]; then
        read -p "Enter Admin Password [press Enter to use generated: ${GEN_PASS}]: " ADMIN_PASS
    fi
    ADMIN_PASS=${ADMIN_PASS:-$GEN_PASS}
fi

# Public IP (Optional)
if [ -z "$PUBLIC_IP" ]; then
    if [ -t 0 ]; then
        read -p "Enter Public Static IP / Domain (optional, leave empty for LAN-only): " PUBLIC_IP
    fi
fi
PUBLIC_IP=$(echo "$PUBLIC_IP" | tr -d '[:space:]')

# Web Port
if [ -z "$WEB_PORT" ]; then
    if [ -t 0 ]; then
        read -p "Enter Web Dashboard Port [default: 8080]: " WEB_PORT
    fi
    WEB_PORT=${WEB_PORT:-8080}
fi

# 6. Deploy Service and Files
echo -e "\n${CYAN}[6/6] Deploying OpenMesh Dashboard & Service...${NC}"
APP_DIR="/opt/openmesh"
mkdir -p "$APP_DIR"

cp "${SCRIPT_DIR}/server.py" "${APP_DIR}/server.py"
chmod +x "${APP_DIR}/server.py"

if [ -n "$MOON_FILE" ] && [ -f "${ZT_DIR}/${MOON_FILE}" ]; then
    cp "${ZT_DIR}/${MOON_FILE}" "${APP_DIR}/${MOON_FILE}"
fi

# Generate a bootstrap token. Clients need it to reach the three endpoints
# they use before they have joined the mesh (client_check, client_connect and
# the moon download). It grants nothing beyond those endpoints.
BOOTSTRAP_TOKEN="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"

# Write config.json
cat > "${APP_DIR}/config.json" <<EOF
{
  "web_port": ${WEB_PORT},
  "zt_port": 9993,
  "public_ip": "${PUBLIC_IP}",
  "admin_user": "${ADMIN_USER}",
  "admin_pass": "${ADMIN_PASS}",
  "network_id": "${NETWORK_ID}",
  "server_virtual_ip": "${SERVER_VIRTUAL_IP}",
  "bootstrap_token": "${BOOTSTRAP_TOKEN}"
}
EOF

# Install systemd service
cp "${SCRIPT_DIR}/openmesh-server.service" /etc/systemd/system/openmesh-server.service
systemctl daemon-reload
systemctl enable openmesh-server.service >/dev/null 2>&1
systemctl restart openmesh-server.service

# If Public IP was provided, update moon now
if [ -n "$PUBLIC_IP" ]; then
    python3 -c "
import sys; sys.path.append('${APP_DIR}')
import server
server.update_moon_for_ip(public_ip='${PUBLIC_IP}')
" >/dev/null 2>&1 || true
fi

PRIMARY_IP=$(ip route get 8.8.8.8 2>/dev/null | awk '{print $7}' | head -n 1)
PRIMARY_IP=${PRIMARY_IP:-"127.0.0.1"}

echo -e "\n${GREEN}================================================================${NC}"
echo -e "${GREEN}   🎉 OpenMesh Gaming Server Installed & Started Successfully!   ${NC}"
echo -e "${GREEN}================================================================${NC}"
echo -e "  🌐 Web Dashboard:    ${CYAN}http://${PRIMARY_IP}:${WEB_PORT}${NC}"
if [ -n "$PUBLIC_IP" ]; then
echo -e "  🌍 Internet WAN:      ${CYAN}http://${PUBLIC_IP}:${WEB_PORT}${NC}"
fi
echo -e "  👤 Admin Username:   ${YELLOW}${ADMIN_USER}${NC}"
echo -e "  🔑 Admin Password:   ${YELLOW}${ADMIN_PASS}${NC}"
echo -e "  🎮 Gaming Network ID:${CYAN}${NETWORK_ID}${NC}"
echo -e "  🖧 Server Virtual IP:${CYAN}${SERVER_VIRTUAL_IP}${NC}"
echo -e "  🌐 Mesh Subnet:      ${CYAN}${MESH_SUBNET}${NC}"
echo -e "  🌙 Moon File:        ${CYAN}http://${PRIMARY_IP}:${WEB_PORT}/download/moon${NC}"
echo -e "  🔌 Bootstrap Token:  ${YELLOW}${BOOTSTRAP_TOKEN}${NC}"
echo -e "${GREEN}================================================================${NC}"
echo -e "${YELLOW}💡 Bootstrap Token Note:${NC}"
echo -e "   Enter this token in the client (Settings -> Bootstrap Token) so it can"
echo -e "   reach the server before it has joined the mesh. It is also stored in"
echo -e "   ${APP_DIR}/config.json and can be rotated there."
echo -e "${YELLOW}💡 Router Port Forwarding Notice:${NC}"
echo -e "   For friends to connect over the Internet, forward in your router:"
echo -e "   - ${CYAN}UDP 9993${NC} ➔ ${PRIMARY_IP}"
echo -e "   - ${CYAN}TCP ${WEB_PORT}${NC} ➔ ${PRIMARY_IP}\n"
