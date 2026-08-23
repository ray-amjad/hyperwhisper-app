#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
RUNTIME_DIR="$(mktemp -d -t hyperwhisper-weston.XXXXXXXX)"
SOCKET_NAME="wayland-hyperwhisper-smoke"
WESTON_LOG="$RUNTIME_DIR/weston.log"
WESTON_PID=""

cleanup() {
    if [[ -n "$WESTON_PID" ]] && kill -0 "$WESTON_PID" 2>/dev/null; then
        kill "$WESTON_PID" 2>/dev/null || true
        wait "$WESTON_PID" 2>/dev/null || true
    fi
    rm -rf -- "$RUNTIME_DIR"
}
trap cleanup EXIT

chmod 0700 "$RUNTIME_DIR"
XDG_RUNTIME_DIR="$RUNTIME_DIR" weston \
    --backend=headless-backend.so \
    --socket="$SOCKET_NAME" \
    --xwayland \
    --idle-time=0 \
    --log="$WESTON_LOG" &
WESTON_PID=$!

for _ in $(seq 1 100); do
    if [[ -S "$RUNTIME_DIR/$SOCKET_NAME" ]] \
        && grep -q 'xserver listening on display :' "$WESTON_LOG"; then
        break
    fi
    if ! kill -0 "$WESTON_PID" 2>/dev/null; then
        cat "$WESTON_LOG" >&2
        echo "Weston exited before Xwayland became ready." >&2
        exit 1
    fi
    sleep 0.05
done

DISPLAY_NUMBER="$(sed -n 's/.*xserver listening on display \(:[0-9][0-9]*\).*/\1/p' "$WESTON_LOG" | tail -n1)"
if [[ -z "$DISPLAY_NUMBER" ]]; then
    cat "$WESTON_LOG" >&2
    echo "Xwayland display was not reported by Weston." >&2
    exit 1
fi

cd "$REPO_ROOT"
XDG_RUNTIME_DIR="$RUNTIME_DIR" \
WAYLAND_DISPLAY="$SOCKET_NAME" \
DISPLAY="$DISPLAY_NUMBER" \
dbus-run-session -- dotnet run \
    --project app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj \
    --configuration Release \
    --no-build \
    -- \
    --smoke-test
