#!/usr/bin/env bash
# ==============================================================================
# OpenMesh Gaming - Clean Uninstaller Script
# ==============================================================================

set -e

if [ "$EUID" -ne 0 ]; then
  echo "[-] Please run as root: sudo bash uninstall.sh"
  exit 1
fi

echo "[*] Stopping OpenMesh service..."
systemctl stop openmesh-server.service 2>/dev/null || true
systemctl disable openmesh-server.service 2>/dev/null || true

echo "[*] Removing systemd service file..."
rm -f /etc/systemd/system/openmesh-server.service
systemctl daemon-reload

if [ "$1" == "-y" ] || [ "$1" == "--force" ] || [ ! -t 0 ]; then
    confirm="y"
else
    read -p "[?] Do you want to delete application files in /opt/openmesh? (y/N): " confirm
fi
if [[ "$confirm" =~ ^[Yy]$ ]]; then
    rm -rf /opt/openmesh
    echo "[+] /opt/openmesh removed."
else
    echo "[*] Kept /opt/openmesh."
fi

echo "[+] OpenMesh server uninstalled successfully."
