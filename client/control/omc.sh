#!/usr/bin/env bash
# omc - OpenMesh Gaming client control helper
#
# Drives the Windows client over its loopback control channel, so the
# client can be scripted instead of clicked.
#
#   ./omc.sh status              show the client state
#   ./omc.sh log                 show the recent client log
#   ./omc.sh connect             connect to the gaming LAN
#   ./omc.sh disconnect          leave the gaming LAN
#   ./omc.sh cancel              cancel a connection attempt in flight
#   ./omc.sh ping                run a manual ping
#   ./omc.sh service             toggle the ZeroTier service
#   ./omc.sh settings server=192.168.31.96 port=8080 token=XXXX
#   ./omc.sh quit                close the client
#
# The control channel listens on 127.0.0.1 only, and every call needs the
# token from %AppData%\OpenMeshGaming\control_token.txt
set -uo pipefail

PORT="${OPENMESH_CONTROL_PORT:-17871}"
APPDATA_WIN="$(cmd //c "echo %APPDATA%" 2>/dev/null | tr -d '\r')"
TOKEN_FILE="$(cygpath -u "$APPDATA_WIN" 2>/dev/null)/OpenMeshGaming/control_token.txt"
[ -f "$TOKEN_FILE" ] || TOKEN_FILE="$HOME/AppData/Roaming/OpenMeshGaming/control_token.txt"

if [ ! -f "$TOKEN_FILE" ]; then
    echo "control token not found at $TOKEN_FILE" >&2
    echo "start the client first - it creates the token on launch" >&2
    exit 1
fi
TOKEN="$(cat "$TOKEN_FILE")"

cmd="${1:-status}"
shift || true

case "$cmd" in
    status)     method=GET;  path=/status;   body="" ;;
    log)        method=GET;  path=/log;      body="" ;;
    token)      method=GET;  path=/token;    body="" ;;
    connect)    method=POST; path=/connect;  body="" ;;
    disconnect) method=POST; path=/disconnect; body="" ;;
    cancel)     method=POST; path=/cancel;   body="" ;;
    ping)       method=POST; path=/ping;     body="" ;;
    service)    method=POST; path=/service;  body="action=${1:-toggle}" ;;
    settings)   method=POST; path=/settings; body="$*" ;;
    quit)       method=POST; path=/quit;     body="" ;;
    *)
        echo "unknown command: $cmd" >&2
        sed -n '2,20p' "$0" >&2
        exit 2
        ;;
esac

if [ "$method" = "POST" ]; then
    curl -s -X POST -H "X-OpenMesh-Control: $TOKEN" \
         ${body:+--data "$body"} \
         "http://127.0.0.1:$PORT$path"
    echo
else
    curl -s -H "X-OpenMesh-Control: $TOKEN" "http://127.0.0.1:$PORT$path"
    echo
fi
