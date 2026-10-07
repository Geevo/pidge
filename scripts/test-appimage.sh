#!/usr/bin/env bash
# Run in a clean desktop/Xvfb session without system WebKitGTK. A successful
# HTTP request proves the page loaded, its bridge worked and NativeAOT sent it.
set -euo pipefail
image=$(realpath "$1")
work=$(mktemp -d)
app_pid=
server_pid=
cleanup() {
    [ -z "$app_pid" ] || kill "$app_pid" 2>/dev/null || true
    [ -z "$server_pid" ] || kill "$server_pid" 2>/dev/null || true
    cat "$work/app.log" "$work/server.log"
    rm -rf "$work"
}
trap cleanup EXIT
export XDG_DATA_HOME="$work/data" XDG_CACHE_HOME="$work/cache" XDG_CONFIG_HOME="$work/config"
export APPIMAGE_EXTRACT_AND_RUN=1 GDK_BACKEND=x11
unset WEBKIT_DISABLE_DMABUF_RENDERER

python3 -u - "$work" > "$work/server.log" 2>&1 <<'PY' &
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import sys

work = Path(sys.argv[1])
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"appimage smoke test")
        (work / "received").write_text(self.path)

server = HTTPServer(("127.0.0.1", 0), Handler)
(work / "port").write_text(str(server.server_port))
server.serve_forever()
PY
server_pid=$!
"$image" > "$work/app.log" 2>&1 &
app_pid=$!

for ((attempt=0; attempt<30; attempt++)); do
    kill -0 "$app_pid"
    window=$(xdotool search --onlyvisible --name '^pidge$' 2>/dev/null | head -n1 || true)
    if [ -n "$window" ] && [ -f "$work/port" ]; then
        xdotool windowfocus --sync "$window"
        xdotool key --clearmodifiers ctrl+l
        xdotool type --clearmodifiers --delay 5 "http://127.0.0.1:$(cat "$work/port")/appimage-smoke"
        xdotool key --clearmodifiers Return
    fi
    sleep 1
    if [ -f "$work/received" ]; then
        test "$(cat "$work/received")" = /appimage-smoke
        if grep -E 'Failed to dispatch message|not found|cannot open shared object|Error loading the injected bundle|Failed to set window icon|Fontconfig error' "$work/app.log"; then
            exit 1
        fi
        echo "AppImage loaded the UI and sent an HTTP request."
        exit 0
    fi
done
echo "AppImage did not send the smoke-test request." >&2
exit 1
