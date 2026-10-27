#!/usr/bin/env python3
"""
OpenMesh Gaming - Autonomous Server Dashboard & Moon Controller
Universal ZeroTier Mesh Manager for LAN & WAN Gaming.
"""

import http.server
import socketserver
import json
import urllib.request
import urllib.error
import urllib.parse
import subprocess
import os
import socket
import threading
import time
import shutil
import base64
import re
import hmac
import ipaddress
import secrets

# The config lives next to server.py unless the environment overrides it.
# Hard-coding a fixed path here meant that any deployment that kept its
# files somewhere else silently fell back to the built-in defaults - and
# therefore to the published default password.
APP_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.environ.get('OPENMESH_CONFIG', os.path.join(APP_DIR, 'config.json'))
ZT_DIR = '/var/lib/zerotier-one'
BANNED_FILE = os.path.join(APP_DIR, 'banned_members.json')
IP_TRACK_FILE = os.path.join(APP_DIR, 'current_ip.txt')

DEFAULT_ADMIN_USER = "admin"
DEFAULT_ADMIN_PASS = "ChangeThisPassword123!"
DEFAULT_MESH_SUBNET = "10.147.20.0/24"

SESSION_COOKIE_NAME = "openmesh_session"
SESSION_DURATION = 86400 * 7  # 7 days

active_sessions = {}
sessions_lock = threading.Lock()

def create_session(username):
    token = secrets.token_hex(32)
    with sessions_lock:
        active_sessions[token] = {
            "user": username,
            "created": time.time(),
            "expires": time.time() + SESSION_DURATION
        }
    return token

def validate_session(token):
    if not token or not isinstance(token, str):
        return None
    token = token.strip()
    with sessions_lock:
        info = active_sessions.get(token)
        if not info:
            return None
        if time.time() > info["expires"]:
            active_sessions.pop(token, None)
            return None
        return info

def destroy_session(token):
    if token:
        with sessions_lock:
            active_sessions.pop(token.strip(), None)

def load_config():
    defaults = {
        "web_port": 8080,
        "zt_port": 9993,
        "public_ip": "",
        "admin_user": DEFAULT_ADMIN_USER,
        "admin_pass": DEFAULT_ADMIN_PASS,
        "network_id": "",
        "server_virtual_ip": "",
        "mesh_subnet": DEFAULT_MESH_SUBNET,
        "bootstrap_token": ""
    }
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r') as f:
                data = json.load(f)
                defaults.update(data)
        except Exception as e:
            print(f"[Config Error] Failed to read {CONFIG_FILE}: {e}")
            # Falling back to `defaults` here would silently downgrade the
            # admin credential to the well-known public default. Serve from
            # the last known-good copy instead.
            backup = CONFIG_FILE + ".bak"
            if os.path.exists(backup):
                try:
                    with open(backup, 'r') as f:
                        data = json.load(f)
                    defaults.update(data)
                    print(f"[Config] Recovered configuration from {backup}")
                except Exception as e2:
                    print(f"[Config Error] Backup {backup} is also unreadable: {e2}")
                    defaults["admin_pass"] = ""
            else:
                # No recoverable copy: refuse to authenticate with the
                # published default password rather than downgrade silently.
                defaults["admin_pass"] = ""
    else:
        save_config(defaults)
    return defaults

# Runtime-only values that must never be persisted to the config file.
TRANSIENT_CONFIG_KEYS = ("lanIp", "lanIps")

def save_config(cfg):
    try:
        cfg = {k: v for k, v in cfg.items() if k not in TRANSIENT_CONFIG_KEYS}
        # os.makedirs('') raises when the config path has no directory part.
        cfg_dir = os.path.dirname(CONFIG_FILE)
        if cfg_dir:
            os.makedirs(cfg_dir, exist_ok=True)
        # Atomic write: a crash mid-write must not corrupt the live config.
        tmp_path = CONFIG_FILE + ".tmp"
        with open(tmp_path, 'w') as f:
            json.dump(cfg, f, indent=2)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, CONFIG_FILE)
    except Exception as e:
        print(f"[Config Error] Failed to save {CONFIG_FILE}: {e}")

cfg_initial = load_config()
PORT = cfg_initial.get("web_port", 8080)

# Shared mutable state. The IP-watcher thread and the request threads both
# touch these, so every access is guarded by state_lock.
state_lock = threading.Lock()
manual_disconnects = set()
recent_kicks = {}  # member_id -> timestamp
# Members this server itself de-authorized. Only these may be re-authorized
# automatically; an operator's manual revocation must be respected.
auto_authorized = set()

# A kick is only reported to the client for a short window; older entries are
# pruned so the dict cannot grow without bound.
KICK_TTL_SECONDS = 20

def prune_recent_kicks():
    """Drop kick entries that are past their TTL. Caller must hold state_lock."""
    now = time.time()
    stale = [mid for mid, ts in recent_kicks.items() if now - ts >= KICK_TTL_SECONDS]
    for mid in stale:
        recent_kicks.pop(mid, None)

def load_banned_members():
    if os.path.exists(BANNED_FILE):
        try:
            with open(BANNED_FILE, 'r') as f:
                return set(json.load(f))
        except Exception:
            pass
    return set()

def save_banned_members(banned_set):
    # Write to a temp file and rename so a crash mid-write cannot leave a
    # truncated ban list on disk.
    tmp_path = BANNED_FILE + ".tmp"
    try:
        with open(tmp_path, 'w') as f:
            json.dump(list(banned_set), f)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, BANNED_FILE)
    except Exception as e:
        print(f"[Banned Error] {e}")
        try:
            if os.path.exists(tmp_path):
                os.remove(tmp_path)
        except Exception:
            pass

def normalize_member_id(raw):
    """Validate a ZeroTier member id and return it lower-cased.

    A ZeroTier address is exactly 10 hexadecimal characters. Anything else is
    rejected before it reaches a controller URL path, which prevents path
    injection (e.g. "../controller/network") through the query string.
    """
    if not raw:
        return None
    cid = raw.strip().lower()
    if not re.fullmatch(r'[0-9a-f]{10}', cid):
        return None
    return cid

def get_auth_token():
    token_path = os.path.join(ZT_DIR, 'authtoken.secret')
    if os.path.exists(token_path):
        with open(token_path) as f:
            return f.read().strip()
    return ""

def zt_api_request(path, method='GET', data=None):
    token = get_auth_token()
    cfg = load_config()
    zt_port = cfg.get("zt_port", 9993)
    url = f"http://127.0.0.1:{zt_port}{path}"
    headers = {'X-ZT1-Auth': token, 'Content-Type': 'application/json'}
    try:
        req = urllib.request.Request(url, headers=headers, method=method)
        if data is not None:
            req.data = json.dumps(data).encode('utf-8')
        with urllib.request.urlopen(req, timeout=5) as resp:
            return json.loads(resp.read().decode('utf-8'))
    except Exception as e:
        return {"error": str(e)}

def get_peers_map():
    peers_raw = zt_api_request("/peer")
    now_ms = time.time() * 1000
    peer_map = {}
    if isinstance(peers_raw, list):
        for p in peers_raw:
            addr = p.get('address', '').lower()
            paths = p.get('paths', [])
            max_recv = 0
            for pt in paths:
                r = pt.get('lastReceive', 0)
                if r > max_recv:
                    max_recv = r
            diff_sec = (now_ms - max_recv) / 1000.0 if max_recv else 9999
            is_online = (diff_sec < 18) and (max_recv > 0)
            peer_map[addr] = {
                "online": is_online,
                "lastSeenSec": int(diff_sec),
                "latency": p.get('latency', -1)
            }
    return peer_map

def get_lan_ip():
    s = None
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        return s.getsockname()[0]
    except Exception:
        pass
    finally:
        if s is not None:
            try:
                s.close()
            except Exception:
                pass
    try:
        return socket.gethostbyname(socket.gethostname())
    except Exception:
        return "127.0.0.1"

def get_all_lan_ips():
    ips = []
    try:
        out = subprocess.check_output(['ip', '-br', 'a']).decode()
        for m in re.finditer(r'(?:eth|wlan|en|wl)\S*\s+UP\s+(\d+\.\d+\.\d+\.\d+)', out):
            ip = m.group(1)
            if ip not in ips and not ip.startswith('127.'):
                ips.append(ip)
    except Exception:
        pass
    if not ips:
        fallback = get_lan_ip()
        if fallback and fallback != '127.0.0.1':
            ips.append(fallback)
    return ips

def get_server_network_id():
    cfg = load_config()
    net_id = cfg.get("network_id", "").strip()
    if net_id:
        return net_id
    # Auto-detect from local controller. The endpoint returns a list of
    # network OBJECTS, so we must pull the id out of the first entry.
    nets = zt_api_request("/controller/network")
    if isinstance(nets, list) and len(nets) > 0:
        first = nets[0]
        if isinstance(first, dict):
            return first.get("id") or first.get("nwid") or ""
        if isinstance(first, str):
            return first
    return ""

def get_server_virtual_ip():
    """Resolve the server's own virtual (ZeroTier) IP.

    Order of preference:
      1. Explicit `server_virtual_ip` from config.json.
      2. The address ZeroTier actually assigned to this node.
      3. Empty string when the node has not joined a network yet.
    """
    cfg = load_config()
    configured = cfg.get("server_virtual_ip", "").strip()
    if configured:
        return configured

    # Ask the local ZeroTier daemon which address it holds on the mesh.
    # The controller's OWN member entry is the one whose id matches this
    # node's address - other members must not be mistaken for the server.
    try:
        status = zt_api_request("/status")
        own_id = ""
        if isinstance(status, dict):
            own_id = (status.get("address") or "").lower()

        nets = zt_api_request("/controller/network")
        if isinstance(nets, list):
            for n in nets:
                net_id = n.get("id", "") if isinstance(n, dict) else ""
                if not net_id:
                    continue
                member = zt_api_request(f"/controller/network/{net_id}/member")
                if isinstance(member, dict) and own_id:
                    own_entry = member.get(own_id)
                    if isinstance(own_entry, dict):
                        for ip in own_entry.get("ipAssignments", []):
                            if ip:
                                return ip
    except Exception:
        pass

    # Fall back to the addresses ZeroTier assigned to this node.
    # `zerotier-cli listnetworks` reports the assigned IP as a dotted quad in
    # the "address" column of the plain-text output.
    try:
        out = subprocess.check_output(
            ['zerotier-cli', 'listnetworks'],
            stderr=subprocess.DEVNULL
        ).decode('utf-8', 'ignore')
        for line in out.splitlines():
            if ' listnetworks ' in line:
                continue
            parts = line.split()
            for tok in parts:
                if re.fullmatch(r'\d+\.\d+\.\d+\.\d+/\d+', tok):
                    return tok.split('/')[0]
    except Exception:
        pass

    return ""

def update_moon_for_ip(new_ip=None, zt_port=None, public_ip=None):
    cfg = load_config()
    if zt_port is None:
        zt_port = cfg.get("zt_port", 9993)
    if public_ip is None:
        public_ip = cfg.get("public_ip", "").strip()
    all_lan = get_all_lan_ips()

    # Work inside a scratch directory instead of os.chdir() so the process-wide
    # working directory is never mutated (it would corrupt relative paths used
    # by other threads and by IP_TRACK_FILE / APP_DIR writes).
    workdir = os.path.join(ZT_DIR, "openmesh-moonwork")
    try:
        print(f"[Auto-IP] Updating Moon for LAN IPs: {all_lan}, Public IP: '{public_ip}' on ZeroTier port: {zt_port}")
        os.makedirs(workdir, exist_ok=True)

        conf_path = os.path.join(ZT_DIR, 'local.conf')
        local_conf = {"settings": {"primaryPort": int(zt_port)}}
        with open(conf_path, 'w') as f:
            json.dump(local_conf, f, indent=2)
            f.write("\n")

        identity_public = os.path.join(ZT_DIR, "identity.public")
        raw_moon = subprocess.check_output(
            ['zerotier-idtool', 'initmoon', identity_public],
            cwd=workdir
        ).decode()
        moon_data = json.loads(raw_moon)

        endpoints = []
        if public_ip:
            endpoints.append(f"{public_ip}/{zt_port}")
        for lip in all_lan:
            ep = f"{lip}/{zt_port}"
            if ep not in endpoints:
                endpoints.append(ep)

        hostname = socket.gethostname()
        endpoints.append(f"{hostname}.local/{zt_port}")
        moon_data['roots'][0]['stableEndpoints'] = endpoints

        moon_json_path = os.path.join(workdir, 'moon.json')
        with open(moon_json_path, 'w') as f:
            json.dump(moon_data, f, indent=2)

        subprocess.check_call(['zerotier-idtool', 'genmoon', moon_json_path], cwd=workdir)

        # Publish the freshly signed moon to every location ZeroTier reads it from.
        moons_d = os.path.join(ZT_DIR, "moons.d")
        os.makedirs(moons_d, exist_ok=True)
        for fname in os.listdir(workdir):
            if fname.startswith('000000') and fname.endswith('.moon'):
                src = os.path.join(workdir, fname)
                shutil.copy(src, os.path.join(moons_d, fname))
                shutil.copy(src, os.path.join(APP_DIR, fname))
                print(f"[Auto-IP] Installed new moon: {fname}")

        subprocess.check_call(['systemctl', 'restart', 'zerotier-one'])

        # Store the same address the watcher compares against, otherwise the
        # two values diverge on multi-homed hosts and the moon is rebuilt in a
        # loop.
        primary_ip = get_lan_ip()
        set_tracked_ip(primary_ip)

        print("[Auto-IP] Moon update completed successfully!")
        return True, "Moon updated successfully"
    except Exception as e:
        print(f"[Auto-IP ERROR] {e}")
        return False, str(e)

def get_tracked_ip():
    try:
        if os.path.exists(IP_TRACK_FILE):
            with open(IP_TRACK_FILE) as f:
                return f.read().strip()
    except Exception:
        pass
    return ""

def set_tracked_ip(ip):
    try:
        with open(IP_TRACK_FILE, 'w') as f:
            f.write(ip or "")
    except Exception as e:
        print(f"[Watcher] Failed to persist tracked IP: {e}")

def ip_monitor_loop():
    while True:
        try:
            # Use exactly the same source of truth that update_moon_for_ip()
            # persists, otherwise the comparison never converges and the moon
            # is regenerated (and ZeroTier restarted) on every single tick.
            current_ip = get_lan_ip()
            last_ip = get_tracked_ip()

            if current_ip and current_ip != "127.0.0.1" and current_ip != last_ip:
                print(f"[Watcher] LAN IP change detected! Old: '{last_ip}' -> New: '{current_ip}'")
                # Persist BEFORE regenerating so a failure inside
                # update_moon_for_ip() cannot cause an endless retry loop.
                set_tracked_ip(current_ip)
                update_moon_for_ip()
                
            net_id = get_server_network_id()
            if net_id:
                # Reconcile authorizations. The member LIST already carries
                # each member's authorization flag, so one controller call per
                # tick is enough - querying every member separately made this
                # an N+1 pattern that hammered the controller every 10s.
                banned = load_banned_members()
                members_raw = zt_api_request(f"/controller/network/{net_id}/member")
                if isinstance(members_raw, dict) and "error" not in members_raw:
                    for mid, minfo in members_raw.items():
                        if not isinstance(minfo, dict):
                            continue
                        m_lower = mid.lower()
                        curr_auth = minfo.get("authorized", False)
                        if m_lower in banned:
                            if curr_auth:
                                zt_api_request(f"/controller/network/{net_id}/member/{mid}",
                                               method='POST', data={"authorized": False})
                                with state_lock:
                                    auto_authorized.discard(m_lower)
                        else:
                            # Only re-authorize a member that WE de-authorized.
                            # A member the operator revoked on purpose (in the
                            # ZeroTier controller UI, for example) must stay
                            # revoked - the watcher used to undo that every tick.
                            if not curr_auth and m_lower in auto_authorized:
                                zt_api_request(f"/controller/network/{net_id}/member/{mid}",
                                               method='POST', data={"authorized": True})
                                auto_authorized.discard(m_lower)
        except Exception as e:
            print(f"[Watcher Exception] {e}")
            
        time.sleep(10)

threading.Thread(target=ip_monitor_loop, daemon=True).start()

HTML_PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>OpenMesh - Gaming Core Dashboard</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
            background-color: #0b0d14;
            color: #f8fafc;
            min-height: 100vh;
            padding: 30px 20px;
        }
        .container { max-width: 1050px; margin: 0 auto; }
        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-bottom: 1px solid #1e293b;
            padding-bottom: 20px;
            margin-bottom: 25px;
        }
        .logo h1 { font-size: 24px; font-weight: 800; color: #00e5ff; letter-spacing: -0.5px; }
        .logo p { font-size: 13px; color: #64748b; margin-top: 4px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin-bottom: 25px; }
        .card {
            background: #141824;
            border: 1px solid #1e293b;
            border-radius: 12px;
            padding: 16px 20px;
            box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.3);
        }
        .card-title { font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; color: #94a3b8; font-weight: 700; margin-bottom: 6px; }
        .card-val { font-size: 20px; font-weight: 700; font-family: monospace; }
        .card-sub { font-size: 11px; color: #64748b; margin-top: 4px; }
        .section-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 14px; }
        .section-header h2 { font-size: 17px; font-weight: 700; color: #f1f5f9; }
        table { width: 100%; border-collapse: collapse; background: #141824; border: 1px solid #1e293b; border-radius: 12px; overflow: hidden; }
        th, td { padding: 12px 16px; text-align: left; font-size: 13px; border-bottom: 1px solid #1e293b; }
        th { background: #0f172a; color: #94a3b8; font-weight: 600; text-transform: uppercase; font-size: 11px; letter-spacing: 0.5px; }
        tr:hover { background: rgba(255, 255, 255, 0.02); }
        .btn {
            font-size: 12px; font-weight: 600; padding: 6px 12px; border-radius: 6px; border: none; cursor: pointer;
            transition: all 0.2s ease; display: inline-flex; align-items: center; justify-content: center;
        }
        .btn-primary { background: #00e5ff; color: #0b0d14; }
        .btn-primary:hover { background: #38bdf8; }
        .btn-warning { background: rgba(245, 158, 11, 0.2); color: #f59e0b; border: 1px solid rgba(245, 158, 11, 0.4); }
        .btn-warning:hover { background: #f59e0b; color: #0b0d14; }
        .btn-danger { background: rgba(239, 68, 68, 0.2); color: #ef4444; border: 1px solid rgba(239, 68, 68, 0.4); }
        .btn-danger:hover { background: #ef4444; color: #fff; }
        .btn-success { background: rgba(16, 185, 129, 0.2); color: #10b981; border: 1px solid rgba(16, 185, 129, 0.4); }
        .btn-success:hover { background: #10b981; color: #0b0d14; }
        .btn-secondary { background: #1e293b; color: #94a3b8; }
        .btn-secondary:hover { background: #334155; color: #fff; }
        .ip-pill { font-family: monospace; background: #0b0d14; padding: 3px 8px; border-radius: 4px; border: 1px solid #1e293b; font-size: 12px; color: #38bdf8; }
        .badge-online { color: #10b981; font-weight: 700; background: rgba(16, 185, 129, 0.15); padding: 4px 10px; border-radius: 6px; border: 1px solid rgba(16, 185, 129, 0.4); font-size: 12px; }
        .badge-kicked { color: #f59e0b; font-weight: 700; background: rgba(245, 158, 11, 0.18); padding: 4px 10px; border-radius: 6px; border: 1px solid rgba(245, 158, 11, 0.45); font-size: 12px; }
        .badge-offline { color: #94a3b8; font-weight: 600; background: rgba(148, 163, 184, 0.1); padding: 4px 10px; border-radius: 6px; border: 1px solid rgba(148, 163, 184, 0.25); font-size: 12px; }
        .badge-banned { color: #ef4444; font-weight: 700; background: rgba(239, 68, 68, 0.18); padding: 4px 10px; border-radius: 6px; border: 1px solid rgba(239, 68, 68, 0.45); font-size: 12px; }
        .actions-bar { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 24px; }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div class="logo">
                <h1>⚡ OpenMesh Gaming Dashboard</h1>
                <p>Autonomous ZeroTier Mesh Controller & Moon Root Server</p>
            </div>
            <div class="user-tag" style="display: flex; align-items: center; gap: 10px;">
                <span id="auth-status">👤 Logged in as: <strong id="lbl-admin-user">admin</strong></span>
                <button class="btn btn-danger" onclick="logout()" style="padding: 4px 10px; font-size: 11px;">🚪 Logout / خروج</button>
                <span style="background: #0f172a; padding: 4px 10px; border-radius: 6px; border: 1px solid #334155; font-size: 11px; color: #38bdf8;">🌐 Web: <strong id="lbl-web-port">8080</strong> | 🎮 Mesh: <strong id="lbl-zt-port">9993</strong> | 🌍 WAN: <strong id="lbl-wan-ip" style="color:#10b981;">LAN Only</strong></span>
            </div>
        </header>

        <div class="grid">
            <div class="card">
                <div class="card-title">Network ID</div>
                <div class="card-val" id="net-id" style="color: #00e5ff; font-size: 18px;">Loading...</div>
                <div class="card-sub" id="net-name">OpenMesh-Gaming</div>
            </div>
            <div class="card">
                <div class="card-title">Server Virtual IP</div>
                <div class="card-val" id="server-virt-ip" style="color: #38bdf8;">--</div>
                <div class="card-sub">Core Gaming Node</div>
            </div>
            <div class="card">
                <div class="card-title">Local LAN IP(s)</div>
                <div class="card-val" id="lan-ip" style="color: #a78bfa; font-size: 16px;">Detecting...</div>
                <div class="card-sub" id="wan-ip-card">Public: Not set (LAN Only)</div>
            </div>
            <div class="card">
                <div class="card-title">Connected Players</div>
                <div class="card-val" id="member-count" style="color: #10b981;">--</div>
                <div class="card-sub">Active in Gaming Mesh</div>
            </div>
        </div>

        <div class="section-header">
            <h2>👥 Connected Members & Players</h2>
            <button class="btn btn-primary" onclick="fetchData()">🔄 Refresh</button>
        </div>

        <table>
            <thead>
                <tr>
                    <th>Member ID</th>
                    <th>Virtual Gaming IP</th>
                    <th>Physical IP (Endpoint)</th>
                    <th>Connection Status</th>
                    <th>Actions</th>
                </tr>
            </thead>
            <tbody id="members-body">
                <tr><td colspan="5" style="text-align: center; color: #64748b;">Loading players...</td></tr>
            </tbody>
        </table>

        <div class="actions-bar">
            <button class="btn btn-primary" onclick="triggerMoonUpdate()">⚡ Re-Sync Moon to Current IP(s)</button>
            <a href="/download/moon" class="btn" style="background: #1e293b; color: #e2e8f0; text-decoration: none;">📥 Download Latest .moon file</a>
            <button class="btn" style="background: #6366f1; color: white;" onclick="openSettingsModal()">⚙️ Server & Network Settings</button>
        </div>

        <!-- SETTINGS MODAL -->
        <div id="settingsModal" style="display:none; position:fixed; z-index:9999; left:0; top:0; width:100%; height:100%; background:rgba(0,0,0,0.75); backdrop-filter:blur(4px); align-items:center; justify-content:center;">
            <div style="background:#141824; border:1px solid #334155; border-radius:14px; width:90%; max-width:500px; padding:24px; box-shadow:0 20px 40px rgba(0,0,0,0.8);">
                <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:18px;">
                    <h3 style="font-size:18px; color:#00e5ff;">⚙️ Server & Network Configuration</h3>
                    <span style="font-size:24px; cursor:pointer; color:#94a3b8;" onclick="closeSettingsModal()">&times;</span>
                </div>
                
                <div style="margin-bottom:14px;">
                    <label style="display:block; font-size:12px; font-weight:700; color:#cbd5e1; margin-bottom:6px; text-transform:uppercase;">Public Static IP / Domain (Internet / WAN)</label>
                    <input type="text" id="settingPublicIp" placeholder="e.g. 203.0.113.50 or mygame.ddns.net" style="width:100%; background:#0b0d14; border:1px solid #334155; border-radius:8px; padding:10px 14px; color:#10b981; font-family:monospace; font-size:14px; font-weight:700;">
                    <p style="font-size:11px; color:#64748b; margin-top:4px;">Enter your router's Public Static IP so friends can connect from their homes over the Internet (leave empty for LAN-only).</p>
                </div>

                <div style="margin-bottom:14px;">
                    <label style="display:block; font-size:12px; font-weight:700; color:#cbd5e1; margin-bottom:6px; text-transform:uppercase;">Web Dashboard Port (HTTP)</label>
                    <input type="number" id="settingWebPort" min="1" max="65535" style="width:100%; background:#0b0d14; border:1px solid #334155; border-radius:8px; padding:10px 14px; color:#38bdf8; font-family:monospace; font-size:15px; font-weight:700;">
                    <p style="font-size:11px; color:#64748b; margin-top:4px;">Port for accessing this Web Panel and client auto-discovery (Default: 8080).</p>
                </div>

                <div style="margin-bottom:16px;">
                    <label style="display:block; font-size:12px; font-weight:700; color:#cbd5e1; margin-bottom:6px; text-transform:uppercase;">ZeroTier Core Mesh Port (UDP)</label>
                    <input type="number" id="settingZtPort" min="1" max="65535" style="width:100%; background:#0b0d14; border:1px solid #334155; border-radius:8px; padding:10px 14px; color:#a78bfa; font-family:monospace; font-size:15px; font-weight:700;">
                    <p style="font-size:11px; color:#64748b; margin-top:4px;">UDP port used for low-latency peer-to-peer gaming traffic & Moon relay (Default: 9993).</p>
                </div>

                <div style="background:rgba(56, 189, 248, 0.08); border-left:3px solid #00e5ff; padding:10px 14px; border-radius:6px; margin-bottom:16px; font-size:11px; color:#94a3b8; line-height:1.5;">
                    💡 <b>Modem Port Forwarding:</b> If you use a Public Static IP, forward <b style="color:#a78bfa;">UDP <span class="lblFwdZt">9993</span></b> and <b style="color:#38bdf8;">TCP <span class="lblFwdWeb">8080</span></b> in your modem settings to Raspberry Pi LAN IP (<span id="lblModalLanIp">192.168.x.x</span>).
                </div>

                <div id="settingsFeedback" style="display:none; padding:10px 14px; border-radius:8px; font-size:12px; margin-bottom:16px;"></div>

                <div style="display:flex; justify-content:flex-end; gap:10px;">
                    <button class="btn btn-secondary" onclick="closeSettingsModal()">Cancel</button>
                    <button class="btn btn-primary" id="btnSaveSettings" onclick="saveSettings()">💾 Save & Apply</button>
                </div>
            </div>
        </div>
    </div>

    <script>
        let currentWebPort = 8080;
        let currentZtPort = 9993;
        let currentPublicIp = '';
        let currentLanIp = '';

        async function logout() {
            try {
                await fetch('/api/logout', { method: 'POST' });
            } catch (e) {}
            location.href = '/login';
        }

        async function fetchData() {
            try {
                const res = await fetch('/api/status');
                if (res.status === 401) {
                    location.href = '/login';
                    return;
                }
                const data = await res.json();
                
                currentLanIp = data.lanIp;
                document.getElementById('lan-ip').innerText = data.lanIp;
                document.getElementById('net-id').innerText = data.networkId || 'Controller Active';
                if (data.serverVirtualIp) document.getElementById('server-virt-ip').innerText = data.serverVirtualIp;
                if (data.adminUser) document.getElementById('lbl-admin-user').innerText = data.adminUser;
                
                if (data.webPort) {
                    currentWebPort = data.webPort;
                    const el = document.getElementById('lbl-web-port');
                    if (el) el.innerText = currentWebPort;
                }
                if (data.ztPort) {
                    currentZtPort = data.ztPort;
                    const el = document.getElementById('lbl-zt-port');
                    if (el) el.innerText = currentZtPort;
                }
                if (data.publicIp) {
                    currentPublicIp = data.publicIp;
                    const elWan = document.getElementById('lbl-wan-ip');
                    if (elWan) elWan.innerText = data.publicIp;
                    const elCard = document.getElementById('wan-ip-card');
                    if (elCard) elCard.innerHTML = `Public: <strong style="color:#10b981;">${data.publicIp}</strong>`;
                } else {
                    currentPublicIp = '';
                    const elWan = document.getElementById('lbl-wan-ip');
                    if (elWan) elWan.innerText = 'LAN Only';
                    const elCard = document.getElementById('wan-ip-card');
                    if (elCard) elCard.innerText = 'Public: Not set (LAN Only)';
                }
                
                const onlineMembers = data.members.filter(m => m.authorized && m.online);
                const totalMembers = data.members.filter(m => m.authorized);
                document.getElementById('member-count').innerHTML = `
                    <span style="color: #10b981;">${onlineMembers.length}</span>
                    <span style="font-size: 13px; color: #64748b; font-weight: normal;">Online</span>
                    <span style="font-size: 11px; color: #475569;">(${totalMembers.length} Saved)</span>
                `;
                
                const tbody = document.getElementById('members-body');
                tbody.innerHTML = '';
                
                if (data.members.length === 0) {
                    tbody.innerHTML = '<tr><td colspan="5" style="text-align: center; color: #64748b;">No registered members yet.</td></tr>';
                    return;
                }
                
                data.members.forEach(m => {
                    const tr = document.createElement('tr');
                    const ip = m.ips.length ? m.ips.join(', ') : (m.authorized ? 'Assigning...' : 'Blocked');
                    const isServer = m.isServer;
                    const label = isServer ? ' <span style="color: #a78bfa; font-size: 11px;">(Server Core)</span>' : '';
                    
                    let statusBadge = '';
                    if (m.banned) {
                        statusBadge = '<span class="badge-banned">⛔ BANNED</span>';
                    } else if (m.kicked) {
                        statusBadge = '<span class="badge-kicked">⚠️ Kicked (Disconnected)</span>';
                    } else if (m.online) {
                        statusBadge = '<span class="badge-online">● Online (Active)</span>';
                    } else {
                        const ago = (m.lastSeenSec > 60) ? Math.floor(m.lastSeenSec / 60) + 'm ago' : (m.lastSeenSec < 9000 ? m.lastSeenSec + 's ago' : 'offline');
                        statusBadge = `<span class="badge-offline">○ Offline (${ago})</span>`;
                    }
                        
                    let actionsHtml = '';
                    if (isServer) {
                        actionsHtml = '<span style="color: #64748b; font-size: 12px;">Core Node</span>';
                    } else if (m.banned) {
                        actionsHtml = `
                            <button class="btn btn-success" onclick="unbanMember('${m.id}')">✅ Unban</button>
                            <button class="btn btn-secondary" style="margin-left: 4px;" onclick="deleteMember('${m.id}')" title="Completely remove player record">🗑️ Remove</button>
                        `;
                    } else if (m.online) {
                        actionsHtml = `
                            <button class="btn btn-warning" onclick="kickMember('${m.id}')" title="Disconnect from current game (Can reconnect)">⚠️ Kick</button>
                            <button class="btn btn-danger" style="margin-left: 4px;" onclick="banMember('${m.id}')" title="Permanently ban player">⛔ Ban</button>
                        `;
                    } else {
                        actionsHtml = `
                            <button class="btn btn-warning" onclick="kickMember('${m.id}')" title="Force disconnect (Can reconnect)">⚠️ Kick</button>
                            <button class="btn btn-danger" style="margin-left: 4px;" onclick="banMember('${m.id}')" title="Permanently ban player">⛔ Ban</button>
                            <button class="btn btn-secondary" style="margin-left: 4px;" onclick="deleteMember('${m.id}')" title="Completely remove player record">🗑️ Remove</button>
                        `;
                    }
                    
                    tr.innerHTML = `
                        <td><strong>${m.id}</strong>${label}</td>
                        <td><span class="ip-pill" style="${m.banned ? 'color: #ef4444; border-color: #ef4444;' : (!m.online ? 'color: #94a3b8;' : '')}">${ip}</span></td>
                        <td><span style="font-family: monospace; color: #94a3b8;">${m.physicalAddress || (m.online ? 'Active P2P' : 'Disconnected')}</span></td>
                        <td>${statusBadge}</td>
                        <td>${actionsHtml}</td>
                    `;
                    tbody.appendChild(tr);
                });
            } catch (e) {
                console.error(e);
            }
        }

        async function kickMember(id) {
            if (!confirm(`Are you sure you want to KICK player ${id}?\\n\\nThey will be disconnected from the current session right now, but they will NOT be banned and can reconnect.`)) return;
            await fetch(`/api/member/kick?id=${id}`, { method: 'POST' });
            fetchData();
        }

        async function banMember(id) {
            if (!confirm(`Are you sure you want to BAN player ${id}?\\n\\nTheir access will be permanently blocked until you click Unban!`)) return;
            await fetch(`/api/member/ban?id=${id}`, { method: 'POST' });
            fetchData();
        }

        async function unbanMember(id) {
            if (!confirm(`Unban player ${id} and allow them back into the game?`)) return;
            await fetch(`/api/member/unban?id=${id}`, { method: 'POST' });
            fetchData();
        }

        async function deleteMember(id) {
            if (!confirm(`Completely remove member record ${id} from database?`)) return;
            await fetch(`/api/member/delete?id=${id}`, { method: 'POST' });
            fetchData();
        }

        async function triggerMoonUpdate() {
            const btn = event.target;
            btn.innerText = '⏳ Updating Moon...';
            btn.disabled = true;
            try {
                const res = await fetch('/api/update_moon', { method: 'POST' });
                const json = await res.json();
                alert(json.message + ' (IPs: ' + json.ip + ')');
            } catch (e) {
                alert('Error updating moon: ' + e);
            } finally {
                btn.innerText = '⚡ Re-Sync Moon to Current IP(s)';
                btn.disabled = false;
                fetchData();
            }
        }

        async function openSettingsModal() {
            document.getElementById('settingsModal').style.display = 'flex';
            document.getElementById('settingWebPort').value = currentWebPort;
            document.getElementById('settingZtPort').value = currentZtPort;
            document.getElementById('settingPublicIp').value = currentPublicIp;
            const elLan = document.getElementById('lblModalLanIp');
            if (elLan) elLan.innerText = currentLanIp || '192.168.x.x';
            document.querySelectorAll('.lblFwdZt').forEach(el => el.innerText = currentZtPort);
            document.querySelectorAll('.lblFwdWeb').forEach(el => el.innerText = currentWebPort);
            document.getElementById('settingsFeedback').style.display = 'none';
        }

        function closeSettingsModal() {
            document.getElementById('settingsModal').style.display = 'none';
        }

        async function saveSettings() {
            const webPort = parseInt(document.getElementById('settingWebPort').value);
            const ztPort = parseInt(document.getElementById('settingZtPort').value);
            const publicIp = document.getElementById('settingPublicIp').value.trim();
            const feedback = document.getElementById('settingsFeedback');
            const btn = document.getElementById('btnSaveSettings');

            if (!webPort || webPort < 1 || webPort > 65535 || !ztPort || ztPort < 1 || ztPort > 65535) {
                feedback.style.display = 'block';
                feedback.style.background = 'rgba(239, 68, 68, 0.2)';
                feedback.style.color = '#ef4444';
                feedback.innerText = 'Ports must be between 1 and 65535.';
                return;
            }

            btn.disabled = true;
            btn.innerText = '⏳ Applying...';
            feedback.style.display = 'block';
            feedback.style.background = 'rgba(56, 189, 248, 0.15)';
            feedback.style.color = '#38bdf8';
            feedback.innerText = 'Saving configuration & updating services...';

            try {
                const res = await fetch('/api/settings', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ web_port: webPort, zt_port: ztPort, public_ip: publicIp })
                });
                const data = await res.json();
                if (data.success) {
                    if (data.web_port_changed) {
                        feedback.style.background = 'rgba(16, 185, 129, 0.2)';
                        feedback.style.color = '#10b981';
                        feedback.innerHTML = `Dashboard port changed to <b>${data.web_port}</b>.<br>Restarting service... Redirecting in 3 seconds...`;
                        setTimeout(() => {
                            window.location.href = data.redirect_url;
                        }, 3000);
                    } else {
                        feedback.style.background = 'rgba(16, 185, 129, 0.2)';
                        feedback.style.color = '#10b981';
                        feedback.innerText = 'Settings applied successfully! Moon file regenerated with updated endpoints.';
                        setTimeout(() => {
                            closeSettingsModal();
                            btn.disabled = false;
                            btn.innerText = '💾 Save & Apply';
                            fetchData();
                        }, 1500);
                    }
                } else {
                    feedback.style.background = 'rgba(239, 68, 68, 0.2)';
                    feedback.style.color = '#ef4444';
                    feedback.innerText = data.error || 'Failed to apply settings.';
                    btn.disabled = false;
                    btn.innerText = '💾 Save & Apply';
                }
            } catch (e) {
                feedback.style.background = 'rgba(239, 68, 68, 0.2)';
                feedback.style.color = '#ef4444';
                feedback.innerText = 'Error: ' + e;
                btn.disabled = false;
                btn.innerText = '💾 Save & Apply';
            }
        }

        fetchData();
        setInterval(fetchData, 3000);
    </script>
</body>
</html>
"""

LOGIN_PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>OpenMesh Gaming - Server Login</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
            background-color: #0b0d14;
            color: #f8fafc;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
            background-image: radial-gradient(circle at 50% 15%, rgba(0, 229, 255, 0.12), transparent 55%),
                              radial-gradient(circle at 80% 80%, rgba(16, 185, 129, 0.08), transparent 50%);
        }
        .login-card {
            background: #121622;
            border: 1px solid #1e293b;
            border-radius: 16px;
            width: 100%;
            max-width: 420px;
            overflow: hidden;
            box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.8), 0 0 30px rgba(0, 229, 255, 0.08);
            position: relative;
        }
        .accent-bar {
            height: 4px;
            background: linear-gradient(90deg, #00e5ff, #38bdf8, #10b981);
        }
        .card-body {
            padding: 36px 32px;
        }
        .header {
            text-align: center;
            margin-bottom: 28px;
        }
        .logo {
            font-size: 24px;
            font-weight: 800;
            color: #00e5ff;
            letter-spacing: -0.5px;
            margin-bottom: 6px;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
        }
        .subtitle {
            font-size: 13px;
            color: #64748b;
            margin-bottom: 12px;
        }
        .badge {
            display: inline-block;
            background: rgba(16, 185, 129, 0.15);
            color: #10b981;
            border: 1px solid rgba(16, 185, 129, 0.35);
            padding: 4px 12px;
            border-radius: 20px;
            font-size: 11px;
            font-weight: 700;
            letter-spacing: 0.5px;
        }
        .form-group {
            margin-bottom: 20px;
        }
        .form-group label {
            display: flex;
            justify-content: space-between;
            font-size: 12px;
            font-weight: 600;
            color: #94a3b8;
            margin-bottom: 8px;
        }
        .form-group label span.fa-hint {
            color: #64748b;
            font-size: 11px;
        }
        .form-input {
            width: 100%;
            background: #080a10;
            border: 1px solid #1e293b;
            border-radius: 8px;
            padding: 12px 14px;
            font-size: 14px;
            color: #f8fafc;
            outline: none;
            transition: all 0.2s ease;
        }
        .form-input:focus {
            border-color: #00e5ff;
            box-shadow: 0 0 0 3px rgba(0, 229, 255, 0.15);
        }
        .alert-error {
            background: rgba(239, 68, 68, 0.15);
            border: 1px solid rgba(239, 68, 68, 0.4);
            color: #f87171;
            padding: 10px 14px;
            border-radius: 8px;
            font-size: 12px;
            margin-bottom: 20px;
            line-height: 1.5;
            display: none;
        }
        .btn-submit {
            width: 100%;
            background: linear-gradient(135deg, #00e5ff, #0284c7);
            color: #0b0d14;
            border: none;
            border-radius: 8px;
            padding: 13px;
            font-size: 14px;
            font-weight: 700;
            cursor: pointer;
            transition: all 0.2s ease;
            box-shadow: 0 4px 14px rgba(0, 229, 255, 0.3);
            margin-top: 6px;
        }
        .btn-submit:hover {
            background: linear-gradient(135deg, #38bdf8, #0369a1);
            transform: translateY(-1px);
            box-shadow: 0 6px 20px rgba(0, 229, 255, 0.45);
        }
        .btn-submit:active {
            transform: translateY(0);
        }
        .btn-submit:disabled {
            opacity: 0.6;
            cursor: not-allowed;
            transform: none;
        }
        .footer-note {
            text-align: center;
            font-size: 11px;
            color: #475569;
            margin-top: 24px;
            padding-top: 18px;
            border-top: 1px solid #1a2233;
        }
    </style>
</head>
<body>
    <div class="login-card">
        <div class="accent-bar"></div>
        <div class="card-body">
            <div class="header">
                <div class="logo">⚡ OPENMESH GAMING</div>
                <div class="subtitle">Server & Mesh Controller Dashboard</div>
                <span class="badge">SECURE ACCESS CONTROL</span>
            </div>

            <div id="error-box" class="alert-error"></div>
            <!--ERROR-->

            <form id="login-form" method="POST" action="/login">
                <div class="form-group">
                    <label for="username">
                        <span>Username</span>
                        <span class="fa-hint">نام کاربری</span>
                    </label>
                    <input type="text" id="username" name="username" class="form-input" placeholder="admin" required autofocus autocomplete="username">
                </div>

                <div class="form-group">
                    <label for="password">
                        <span>Password</span>
                        <span class="fa-hint">رمز عبور</span>
                    </label>
                    <input type="password" id="password" name="password" class="form-input" placeholder="••••••••••••" required autocomplete="current-password">
                </div>

                <button type="submit" id="btn-submit" class="btn-submit">🚀 Sign In to Dashboard / ورود</button>
            </form>

            <div class="footer-note">
                Autonomous Offline Gaming Mesh • ZeroTier Moon Core
            </div>
        </div>
    </div>

    <script>
        const form = document.getElementById('login-form');
        const btn = document.getElementById('btn-submit');
        const errBox = document.getElementById('error-box');

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            errBox.style.display = 'none';
            btn.disabled = true;
            btn.innerText = 'Signing in / در حال ورود...';

            const username = document.getElementById('username').value.trim();
            const password = document.getElementById('password').value;

            try {
                const res = await fetch('/api/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ username, password })
                });
                const data = await res.json();
                if (res.ok && data.success) {
                    btn.innerText = '✅ Access Granted! Redirecting...';
                    location.href = data.redirect || '/';
                } else {
                    errBox.innerText = '❌ نام کاربری یا رمز عبور نامعتبر است / ' + (data.error || 'Invalid credentials');
                    errBox.style.display = 'block';
                    btn.disabled = false;
                    btn.innerText = '🚀 Sign In to Dashboard / ورود';
                }
            } catch (err) {
                errBox.innerText = '❌ ارتباط با سرور برقرار نشد: ' + err.message;
                errBox.style.display = 'block';
                btn.disabled = false;
                btn.innerText = '🚀 Sign In to Dashboard / ورود';
            }
        });
    </script>
</body>
</html>
"""

class DashboardHandler(http.server.BaseHTTPRequestHandler):
    # A client that announces a body and then stops sending must not pin a
    # worker thread forever.
    timeout = 30
    def get_session_token(self):
        cookie_header = self.headers.get('Cookie')
        if not cookie_header:
            return None
        match = re.search(r'(?:^|;\s*)' + re.escape(SESSION_COOKIE_NAME) + r'=([0-9a-fA-F]{64})', cookie_header)
        if match:
            return match.group(1)
        return None

    def check_auth(self):
        # 1. Cookie-based web session
        token = self.get_session_token()
        if token and validate_session(token):
            return True

        # 2. HTTP Basic Auth header (fallback for scripts / curl)
        cfg = load_config()
        admin_user = cfg.get("admin_user", DEFAULT_ADMIN_USER)
        admin_pass = cfg.get("admin_pass", DEFAULT_ADMIN_PASS)
        expected = "Basic " + base64.b64encode(f"{admin_user}:{admin_pass}".encode()).decode('ascii')
        
        auth_header = self.headers.get('Authorization')
        if auth_header and hmac.compare_digest(auth_header, expected):
            return True

        return False

    def send_unauthorized(self):
        self.send_response(401)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        # NO WWW-Authenticate header so browser does not display modal dialog!
        self.end_headers()
        self.wfile.write(b'{"error": "Unauthorized", "login_required": true}')

    def handle_login(self):
        try:
            content_length = int(self.headers.get('Content-Length', 0) or 0)
        except (TypeError, ValueError):
            content_length = 0
        if content_length > 64 * 1024:
            self.send_response(400)
            self.end_headers()
            return

        body = self.rfile.read(content_length).decode('utf-8', errors='replace')
        is_json = 'application/json' in self.headers.get('Content-Type', '')

        user = ""
        password = ""
        if is_json:
            try:
                data = json.loads(body)
                user = str(data.get('username', '')).strip()
                password = str(data.get('password', ''))
            except Exception:
                pass
        else:
            params = urllib.parse.parse_qs(body)
            user = params.get('username', [''])[0].strip()
            password = params.get('password', [''])[0]

        cfg = load_config()
        expected_user = cfg.get("admin_user", DEFAULT_ADMIN_USER)
        expected_pass = cfg.get("admin_pass", DEFAULT_ADMIN_PASS)

        if hmac.compare_digest(user, expected_user) and hmac.compare_digest(password, expected_pass):
            token = create_session(user)
            cookie = f"{SESSION_COOKIE_NAME}={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={SESSION_DURATION}"
            if is_json:
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Set-Cookie', cookie)
                self.end_headers()
                self.wfile.write(b'{"success": true, "redirect": "/"}')
            else:
                self.send_response(302)
                self.send_header('Location', '/')
                self.send_header('Set-Cookie', cookie)
                self.end_headers()
        else:
            if is_json:
                self.send_response(401)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(b'{"error": "Invalid username or password"}')
            else:
                self.send_response(200)
                self.send_header('Content-Type', 'text/html; charset=utf-8')
                self.end_headers()
                err_html = '<div class="alert-error" style="display:block;">❌ نام کاربری یا رمز عبور اشتباه است / Invalid username or password</div>'
                self.wfile.write(LOGIN_PAGE.replace('<!--ERROR-->', err_html).encode('utf-8'))

    def handle_logout(self):
        token = self.get_session_token()
        destroy_session(token)
        cookie = f"{SESSION_COOKIE_NAME}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0"
        if 'application/json' in self.headers.get('Accept', '') or self.path.startswith('/api/'):
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Set-Cookie', cookie)
            self.end_headers()
            self.wfile.write(b'{"success": true, "redirect": "/login"}')
        else:
            self.send_response(302)
            self.send_header('Location', '/login')
            self.send_header('Set-Cookie', cookie)
            self.end_headers()

    def send_bad_request(self, message="Invalid member id"):
        self.send_response(400)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps({"error": message}).encode('utf-8'))

    def has_bootstrap_token(self):
        """True when the request carries the shared bootstrap token.

        The bootstrap token lets a client that has not joined the mesh yet
        reach the three endpoints it needs before joining. It grants nothing
        beyond those endpoints and is separate from the admin credential.
        """
        cfg = load_config()
        token = (cfg.get("bootstrap_token", "") or "").strip()
        if not token:
            return False
        supplied = self.headers.get('X-OpenMesh-Bootstrap')
        if not supplied:
            return False
        return hmac.compare_digest(supplied.strip(), token)

    def client_allowed(self):
        """Authorisation gate for the client-facing bootstrap endpoints."""
        return self.is_mesh_request() or self.has_bootstrap_token() or self.check_auth()

    def is_mesh_request(self):
        """True when the request arrived over the ZeroTier mesh interface.

        The mesh subnet is private and only reachable by nodes that have been
        authorized by the controller, so client bootstrap endpoints can be
        served to them without an admin credential. Everything else stays
        behind Basic Auth.
        """
        try:
            peer = self.client_address[0]
        except Exception:
            return False
        cfg = load_config()
        net = ipaddress.ip_network(cfg.get("mesh_subnet", DEFAULT_MESH_SUBNET), strict=False)
        try:
            return ipaddress.ip_address(peer) in net
        except ValueError:
            return False

    def do_HEAD(self):
        return self.do_GET()

    def do_GET(self):
        cfg = load_config()
        net_id = get_server_network_id()
        all_ips = get_all_lan_ips()
        primary_lan = all_ips[0] if all_ips else get_lan_ip()

        # Login / Logout routes
        if self.path in ('/login', '/login.html'):
            if self.check_auth():
                self.send_response(302)
                self.send_header('Location', '/')
                self.end_headers()
                return
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.end_headers()
            self.wfile.write(LOGIN_PAGE.encode('utf-8'))
            return

        if self.path in ('/logout', '/api/logout'):
            self.handle_logout()
            return

        # Moon file download endpoint
        if self.path == '/download/moon':
            # The moon root defines the mesh root of trust. Only mesh peers,
            # holders of the bootstrap token, or an authenticated admin may
            # fetch it.
            if not self.client_allowed():
                self.send_unauthorized()
                return
            moon_file = None
            for d in [ZT_DIR, APP_DIR]:
                for f in os.listdir(d):
                    if f.startswith('000000') and f.endswith('.moon'):
                        moon_file = os.path.join(d, f)
                        break
                if moon_file:
                    break
            
            if moon_file and os.path.exists(moon_file):
                self.send_response(200)
                self.send_header('Content-Type', 'application/octet-stream')
                self.send_header('Content-Disposition', f'attachment; filename="{os.path.basename(moon_file)}"')
                self.send_header('Access-Control-Allow-Origin', '*')
                self.end_headers()
                with open(moon_file, 'rb') as f:
                    self.wfile.write(f.read())
                return
            else:
                self.send_error(404, "Moon file not found on server")
                return

        # Client Health / Kick & Ban Status endpoint
        if self.path.startswith('/api/client_check'):
            # Bootstrapped by the client before it holds any credential.
            if not self.client_allowed():
                self.send_unauthorized()
                return
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            client_id = params.get('id', [None])[0]
            
            banned_members = load_banned_members()
            is_banned = False
            is_kicked = False
            is_auth = False
            kick_ts = 0
            
            if client_id:
                cid = normalize_member_id(client_id)
                if not cid:
                    cid = client_id.strip().lower()
                is_banned = cid in banned_members
                with state_lock:
                    prune_recent_kicks()
                    if cid in recent_kicks:
                        kick_ts = recent_kicks[cid]
                        is_kicked = (time.time() - kick_ts < KICK_TTL_SECONDS)
                
                if net_id:
                    minfo = zt_api_request(f"/controller/network/{net_id}/member/{cid}")
                    if isinstance(minfo, dict) and "error" not in minfo:
                        is_auth = minfo.get("authorized", False)
                        
            resp = {
                "networkId": net_id,
                "serverVirtualIp": get_server_virtual_ip(),
                "isBanned": is_banned,
                "isKicked": is_kicked,
                "kickTimestamp": int(kick_ts * 1000),
                "authorized": is_auth,
                "bannedMembers": list(banned_members)
            }
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(json.dumps(resp).encode('utf-8'))
            return

        # Client Disconnect endpoint
        if self.path.startswith('/api/client_disconnect'):
            if not self.client_allowed():
                self.send_unauthorized()
                return
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            client_id = params.get('id', [None])[0]
            if client_id:
                cid = normalize_member_id(client_id) or client_id.strip().lower()
                with state_lock:
                    manual_disconnects.add(cid)
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"disconnected"}')
            return

        # Client Connect endpoint
        if self.path.startswith('/api/client_connect'):
            # This endpoint authorizes a node on the controller, so it must be
            # restricted to mesh peers, bootstrap token holders, or an admin.
            if not self.client_allowed():
                self.send_unauthorized()
                return
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            client_id = params.get('id', [None])[0]
            if client_id and net_id:
                cid = normalize_member_id(client_id)
                if not cid:
                    cid = client_id.strip().lower()
                with state_lock:
                    manual_disconnects.discard(cid)
                    recent_kicks.pop(cid, None)
                banned = load_banned_members()
                if cid not in banned:
                    zt_api_request(f"/controller/network/{net_id}/member/{cid}", method='POST', data={"authorized": True})
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(b'{"status":"connected"}')
            return

        # Dashboard Web UI
        if self.path in ('/', '/index.html'):
            if not self.check_auth():
                self.send_response(200)
                self.send_header('Content-Type', 'text/html; charset=utf-8')
                self.end_headers()
                self.wfile.write(LOGIN_PAGE.encode('utf-8'))
                return
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.end_headers()
            self.wfile.write(HTML_PAGE.encode('utf-8'))
            return

        # Protected API endpoints
        if not self.check_auth():
            self.send_unauthorized()
            return
            
        elif self.path == '/api/status':
            banned = load_banned_members()
            peers_map = get_peers_map()
            members_raw = zt_api_request(f"/controller/network/{net_id}/member") if net_id else {}
            members_list = []
            
            # Find local server node ID
            status_obj = zt_api_request("/status")
            server_node_id = status_obj.get("address", "").lower() if isinstance(status_obj, dict) else ""

            if isinstance(members_raw, dict) and "error" not in members_raw:
                for member_id in members_raw.keys():
                    m_lower = member_id.lower()
                    m_info = zt_api_request(f"/controller/network/{net_id}/member/{member_id}")
                    is_banned = (m_lower in banned)
                    with state_lock:
                        _kick_ts = recent_kicks.get(m_lower)
                    is_kicked = (_kick_ts is not None) and (time.time() - _kick_ts < KICK_TTL_SECONDS)
                    is_auth = m_info.get("authorized", False) and (not is_banned)
                    
                    is_srv = (m_lower == server_node_id)
                    p_info = peers_map.get(m_lower, {})
                    with state_lock:
                        _was_disconnected = m_lower in manual_disconnects
                    is_online = is_srv or (p_info.get("online", False) and not _was_disconnected and not is_kicked)
                    last_seen = 0 if is_srv else p_info.get("lastSeenSec", 9999)

                    members_list.append({
                        "id": member_id,
                        "isServer": is_srv,
                        "authorized": is_auth,
                        "banned": is_banned,
                        "kicked": is_kicked,
                        "online": is_online,
                        "lastSeenSec": last_seen,
                        "ips": m_info.get("ipAssignments", []),
                        "physicalAddress": m_info.get("physicalAddress", "")
                    })
            
            status_data = {
                "networkId": net_id,
                "serverVirtualIp": get_server_virtual_ip(),
                "lanIp": ", ".join(all_ips) if all_ips else primary_lan,
                "lanIps": all_ips,
                "webPort": cfg.get("web_port", 8080),
                "ztPort": cfg.get("zt_port", 9993),
                "publicIp": cfg.get("public_ip", ""),
                "adminUser": cfg.get("admin_user", DEFAULT_ADMIN_USER),
                "adminPassSet": bool(cfg.get("admin_pass") and
                                    cfg.get("admin_pass") != DEFAULT_ADMIN_PASS),
                "hostname": socket.gethostname(),
                "members": members_list
            }
            
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(json.dumps(status_data).encode('utf-8'))
            return

        elif self.path == '/api/settings':
            # Never return the admin credential to the browser: the dashboard
            # JS does not need it, and it would land in devtools / any proxy
            # or log that captures the response.
            safe_cfg = {k: v for k, v in cfg.items()
                        if k not in ("admin_pass", "bootstrap_token")}
            safe_cfg["lanIp"] = ", ".join(all_ips) if all_ips else primary_lan
            safe_cfg["lanIps"] = all_ips
            safe_cfg["adminPassSet"] = bool(cfg.get("admin_pass") and
                                            cfg.get("admin_pass") != DEFAULT_ADMIN_PASS)
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(json.dumps(safe_cfg).encode('utf-8'))
            return
                
        self.send_error(404, "Not Found")

    def do_POST(self):
        net_id = get_server_network_id()

        # Login / Logout handlers
        if self.path in ('/login', '/api/login'):
            self.handle_login()
            return

        if self.path in ('/logout', '/api/logout'):
            self.handle_logout()
            return

        if self.path.startswith('/api/client_disconnect'):
            if not self.client_allowed():
                self.send_unauthorized()
                return
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            client_id = params.get('id', [None])[0]
            if client_id:
                cid = normalize_member_id(client_id) or client_id.strip().lower()
                with state_lock:
                    manual_disconnects.add(cid)
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(b'{"status":"disconnected"}')
            return

        if self.path.startswith('/api/client_connect'):
            if not self.client_allowed():
                self.send_unauthorized()
                return
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            client_id = params.get('id', [None])[0]
            if client_id and net_id:
                cid = normalize_member_id(client_id)
                if not cid:
                    cid = client_id.strip().lower()
                with state_lock:
                    manual_disconnects.discard(cid)
                    recent_kicks.pop(cid, None)
                banned = load_banned_members()
                if cid not in banned:
                    zt_api_request(f"/controller/network/{net_id}/member/{cid}", method='POST', data={"authorized": True})
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(b'{"status":"connected"}')
            return

        if not self.check_auth():
            self.send_unauthorized()
            return

        if self.path == '/api/settings':
            try:
                content_length = int(self.headers.get('Content-Length', 0) or 0)
            except (TypeError, ValueError):
                content_length = 0
            # Cap the body: a bogus Content-Length would otherwise block this
            # worker thread forever.
            if content_length < 0 or content_length > 1024 * 1024:
                self.send_response(400)
                self.end_headers()
                self.wfile.write(b'{"error": "Invalid Content-Length."}')
                return
            post_body = self.rfile.read(content_length)
            try:
                data = json.loads(post_body.decode('utf-8'))
                new_web_port = int(data.get('web_port', 8080))
                new_zt_port = int(data.get('zt_port', 9993))
                new_public_ip = str(data.get('public_ip', '')).strip()
                
                if not (1 <= new_web_port <= 65535 and 1 <= new_zt_port <= 65535):
                    self.send_response(400)
                    self.end_headers()
                    self.wfile.write(b'{"error": "Invalid port number. Must be between 1 and 65535."}')
                    return

                cfg = load_config()
                old_web_port = cfg.get('web_port', 8080)
                old_zt_port = cfg.get('zt_port', 9993)
                old_public_ip = cfg.get('public_ip', '').strip()

                cfg['web_port'] = new_web_port
                cfg['zt_port'] = new_zt_port
                cfg['public_ip'] = new_public_ip
                save_config(cfg)

                zt_updated = False
                if new_zt_port != old_zt_port or new_public_ip != old_public_ip:
                    update_moon_for_ip(zt_port=new_zt_port, public_ip=new_public_ip)
                    zt_updated = True

                primary_ip = get_lan_ip()
                web_changed = (new_web_port != old_web_port)
                resp = {
                    "success": True,
                    "web_port": new_web_port,
                    "zt_port": new_zt_port,
                    "public_ip": new_public_ip,
                    "web_port_changed": web_changed,
                    "zt_updated": zt_updated,
                    "redirect_url": f"http://{primary_ip}:{new_web_port}"
                }

                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps(resp).encode('utf-8'))

                if web_changed:
                    def restart_dashboard():
                        time.sleep(1)
                        subprocess.Popen(['systemctl', 'restart', 'openmesh-server.service'])
                    threading.Thread(target=restart_dashboard, daemon=True).start()
                return
            except Exception as e:
                self.send_response(500)
                self.end_headers()
                self.wfile.write(json.dumps({"error": str(e)}).encode('utf-8'))
                return

        elif self.path == '/api/update_moon':
            ok, msg = update_moon_for_ip()
            all_ips = get_all_lan_ips()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({"success": ok, "message": msg, "ip": ", ".join(all_ips)}).encode('utf-8'))
            return

        elif self.path.startswith('/api/member/kick'):
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            member_id = params.get('id', [None])[0]
            m_lower = normalize_member_id(member_id)
            if not m_lower:
                self.send_bad_request("Member id must be a 10-character hex ZeroTier address")
                return
            if m_lower and net_id:
                with state_lock:
                    recent_kicks[m_lower] = time.time()
                    manual_disconnects.add(m_lower)
                zt_api_request(f"/controller/network/{net_id}/member/{m_lower}", method='POST', data={"authorized": False})

                def reauth():
                    time.sleep(2)
                    banned = load_banned_members()
                    if m_lower not in banned:
                        zt_api_request(f"/controller/network/{net_id}/member/{m_lower}", method='POST', data={"authorized": True})
                threading.Thread(target=reauth, daemon=True).start()
                
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"kicked"}')
            return

        elif self.path.startswith('/api/member/ban'):
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            member_id = params.get('id', [None])[0]
            m_lower = normalize_member_id(member_id)
            if not m_lower:
                self.send_bad_request("Member id must be a 10-character hex ZeroTier address")
                return
            if m_lower and net_id:
                banned = load_banned_members()
                banned.add(m_lower)
                save_banned_members(banned)
                with state_lock:
                    recent_kicks.pop(m_lower, None)
                    manual_disconnects.add(m_lower)
                zt_api_request(f"/controller/network/{net_id}/member/{m_lower}", method='POST', data={"authorized": False})
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"banned"}')
            return

        elif self.path.startswith('/api/member/unban'):
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            member_id = params.get('id', [None])[0]
            m_lower = normalize_member_id(member_id)
            if not m_lower:
                self.send_bad_request("Member id must be a 10-character hex ZeroTier address")
                return
            if m_lower and net_id:
                banned = load_banned_members()
                banned.discard(m_lower)
                save_banned_members(banned)
                with state_lock:
                    manual_disconnects.discard(m_lower)
                    recent_kicks.pop(m_lower, None)
                zt_api_request(f"/controller/network/{net_id}/member/{m_lower}", method='POST', data={"authorized": True})
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"unbanned"}')
            return

        elif self.path.startswith('/api/member/delete'):
            query = urllib.parse.urlparse(self.path).query
            params = urllib.parse.parse_qs(query)
            member_id = params.get('id', [None])[0]
            m_lower = normalize_member_id(member_id)
            if not m_lower:
                self.send_bad_request("Member id must be a 10-character hex ZeroTier address")
                return
            if m_lower and net_id:
                banned = load_banned_members()
                banned.discard(m_lower)
                save_banned_members(banned)
                with state_lock:
                    manual_disconnects.discard(m_lower)
                    recent_kicks.pop(m_lower, None)
                zt_api_request(f"/controller/network/{net_id}/member/{m_lower}", method='DELETE')
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"deleted"}')
            return

        self.send_error(404, "Not Found")

class ThreadedHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True

if __name__ == '__main__':
    cfg = load_config()
    port = cfg.get('web_port', 8080)
    server = ThreadedHTTPServer(('0.0.0.0', port), DashboardHandler)
    all_lan = get_all_lan_ips()
    print(f"==================================================")
    print(f"⚡ OpenMesh Gaming Dashboard Started")
    print(f"Local LAN Dashboard: http://{all_lan[0] if all_lan else '127.0.0.1'}:{port}")
    if cfg.get('public_ip'):
        print(f"Public WAN Dashboard: http://{cfg.get('public_ip')}:{port}")
    print(f"Admin User: {cfg.get('admin_user', DEFAULT_ADMIN_USER)}")
    virt_ip = get_server_virtual_ip()
    print(f"Server Virtual IP: {virt_ip if virt_ip else '(not joined yet)'}")
    if cfg.get('admin_pass') == DEFAULT_ADMIN_PASS:
        print("-" * 50)
        print(f"⚠️  WARNING: using the default admin password!")
        print(f"    Change it before exposing this dashboard to the Internet.")
        print(f"    Edit: {CONFIG_FILE}")
        print("-" * 50)
    print(f"==================================================")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
