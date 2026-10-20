#!/usr/bin/env bash
# ==============================================================================
# OpenMesh Gaming - 1-Click Update Script
# ==============================================================================

set -e

if [ "$EUID" -ne 0 ]; then
  echo "[-] Please run as root: sudo bash update.sh"
  exit 1
fi

echo "========================================================"
echo "          OpenMesh Gaming - Server Update Tool          "
echo "========================================================"
echo ""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/opt/openmesh"

# 1. If inside a git repository, pull latest changes
if [ -d "${SCRIPT_DIR}/../.git" ]; then
    echo "[*] Checking for updates from Git repository..."
    cd "${SCRIPT_DIR}/.."
    git pull || echo "[!] Notice: git pull skipped or failed, continuing with local update..."
    cd "${SCRIPT_DIR}"
fi

# 2. Check if OpenMesh is installed
if [ ! -d "$INSTALL_DIR" ]; then
    echo "[-] OpenMesh directory ($INSTALL_DIR) not found."
    echo "[*] Running full installer instead..."
    bash "${SCRIPT_DIR}/install.sh"
    exit 0
fi

# 3. Backup existing configuration and database
echo "[*] Safeguarding configuration and member database..."
if [ -f "${INSTALL_DIR}/config.json" ]; then
    cp -f "${INSTALL_DIR}/config.json" "${INSTALL_DIR}/config.json.bak"
fi
if [ -f "${INSTALL_DIR}/banned_members.json" ]; then
    cp -f "${INSTALL_DIR}/banned_members.json" "${INSTALL_DIR}/banned_members.json.bak"
fi

# 4. Copy updated server script
echo "[*] Deploying updated OpenMesh server engine..."
cp -f "${SCRIPT_DIR}/server.py" "${INSTALL_DIR}/server.py"
chmod +x "${INSTALL_DIR}/server.py"

# 5. Update systemd service definition if changed
if [ -f "${SCRIPT_DIR}/openmesh-server.service" ]; then
    cp -f "${SCRIPT_DIR}/openmesh-server.service" /etc/systemd/system/openmesh-server.service
    systemctl daemon-reload
fi

# 6. Restart OpenMesh Service
echo "[*] Restarting OpenMesh service..."
systemctl restart openmesh-server.service

sleep 2

if systemctl is-active --quiet openmesh-server.service; then
    echo ""
    echo "========================================================"
    echo " [+] OpenMesh Server updated and running successfully!  "
    echo "========================================================"
else
    echo ""
    echo "[!] Warning: Service failed to restart. Checking logs..."
    journalctl -u openmesh-server.service -n 15 --no-pager
    exit 1
fi
