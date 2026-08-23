# Linux release verification matrix

CI proves compilation, dependency-free tests, Xvfb rendering, the self-contained
`linux-x64` publish, and Debian package structure. It does **not** prove real
compositor behavior, portal UX, physical input-device access, or GPU inference.
Every release candidate must therefore complete this checklist on three clean
x86_64 desktop installations plus a physical GPU host.

Publishing is fail-closed: after completing the matrix, upload each redacted
evidence bundle, record its HTTPS URL and SHA-256 in
`release-evidence/VERSION.json`, and have the manifest reviewed against the
exact tested commit. The release commit may add only evidence manifests after
that commit. Tag pushes only build a dry run. The manually approved release
workflow validates this ancestry/diff invariant and includes the manifest in
the published checksums.

Use `PASS`, `FAIL`, `BLOCKED`, or `NOT IMPLEMENTED` for every result. A release
requires `PASS` everywhere marked **gate**; `NOT IMPLEMENTED` is not a pass.

## Run identity and evidence fields

Create one evidence directory per machine. Do not capture API keys, bearer
tokens, dictated personal content, or raw unrelated keystrokes.

```bash
export HW_RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$(hostname)-${XDG_SESSION_TYPE}"
export HW_EVIDENCE_DIR="$PWD/evidence/$HW_RUN_ID"
mkdir -p "$HW_EVIDENCE_DIR"

{
  printf 'run_id=%s\n' "$HW_RUN_ID"
  printf 'tester=%s\n' "<name>"
  printf 'commit=%s\n' "$(git rev-parse HEAD 2>/dev/null || printf '<release-commit>')"
  printf 'date_utc=%s\n' "$(date -u --iso-8601=seconds)"
  printf 'distro=%s\n' "$(. /etc/os-release; printf '%s %s' "$ID" "$VERSION_ID")"
  printf 'kernel=%s\n' "$(uname -srmo)"
  printf 'session_type=%s\n' "${XDG_SESSION_TYPE:-unset}"
  printf 'desktop=%s\n' "${XDG_CURRENT_DESKTOP:-unset}"
  printf 'display=%s\n' "${DISPLAY:-unset}"
  printf 'wayland_display=%s\n' "${WAYLAND_DISPLAY:-unset}"
} | tee "$HW_EVIDENCE_DIR/system.txt"

loginctl show-session "${XDG_SESSION_ID:?XDG_SESSION_ID is required}" \
  -p Type -p Desktop -p Remote -p Active \
  | tee "$HW_EVIDENCE_DIR/session.txt"
```

Record these fields for each checklist row:

| Field | Required evidence |
|---|---|
| Result | `PASS`, `FAIL`, `BLOCKED`, or `NOT IMPLEMENTED` |
| Start/end UTC | ISO-8601 timestamps |
| Exact command/action | Command plus UI action sequence |
| Expected | Observable acceptance condition below |
| Actual | What happened, including fallback behavior |
| Logs | Relevant redacted log excerpt; never raw unrelated keys or tokens |
| Visual evidence | Screenshot/video filename when the check is visual |
| Defect | Issue URL/ID for every failure |

## Required desktop matrix

| Environment | Required configuration | Active-app expectation |
|---|---|---|
| GNOME Wayland | Ubuntu 22.04, Wayland session | Companion extension when installed; explicit default-mode fallback otherwise |
| GNOME Xorg | Ubuntu 22.04, Xorg session | X11 active-window detection |
| KDE Wayland | Debian 12, Plasma Wayland session | KDE D-Bus active-window detection |

Confirm the matrix identity before testing:

```bash
test "$(uname -m)" = x86_64
printf 'type=%s desktop=%s\n' "$XDG_SESSION_TYPE" "$XDG_CURRENT_DESKTOP"
```

## Package installation and permissions — gate on all three desktops

```bash
export HW_DEB="${HW_DEB:?absolute path to hyperwhisper_1.0.0_amd64.deb}"
sha256sum "$HW_DEB" | tee "$HW_EVIDENCE_DIR/package-sha256.txt"
dpkg-deb --info "$HW_DEB" | tee "$HW_EVIDENCE_DIR/package-info.txt"
sudo apt-get install --yes "$HW_DEB"
dpkg-query -W -f='${Package} ${Version} ${Architecture}\n' hyperwhisper \
  | tee "$HW_EVIDENCE_DIR/package-installed.txt"
command -v wl-copy wl-paste wmctrl xclip xprop \
  | tee "$HW_EVIDENCE_DIR/text-injection-tools.txt"
python3 -c "import gi; gi.require_version('Atspi','2.0'); from gi.repository import Atspi" \
  && printf 'AT-SPI Python bindings available\n' \
  | tee "$HW_EVIDENCE_DIR/atspi-bindings.txt"

sudo usermod -aG hyperwhisper-input "$USER"
printf 'Log out and back in now; do not continue in the old session.\n'
```

After re-login:

```bash
id -nG | tee "$HW_EVIDENCE_DIR/groups.txt"
test "$(id -nG | tr ' ' '\n' | grep -cx hyperwhisper-input)" = 1
keyboard_device="$(readlink -f /dev/input/by-id/*-event-kbd | head -n1)"
stat -c '%n mode=%a group=%G' "$keyboard_device" /dev/uinput \
  | tee "$HW_EVIDENCE_DIR/device-permissions.txt"
test "$(stat -c '%G:%a' "$keyboard_device")" = 'hyperwhisper-input:640'
test "$(stat -c '%G:%a' /dev/uinput)" = 'hyperwhisper-input:660'
```

Expected: package is `1.0.0 amd64`; only an explicitly enrolled user gains
keyboard read and uinput write access; neither device is world-readable or
world-writable. Save `journalctl -b -u systemd-udevd --no-pager` if modes differ.

## Application checklist — gate on all three desktops

Start a redacted application log capture:

```bash
hyperwhisper 2>&1 | tee "$HW_EVIDENCE_DIR/hyperwhisper-console.txt"
```

Complete each row and record the evidence fields above.

| Check | Exact action | Acceptance condition |
|---|---|---|
| Window and navigation | Launch from the desktop menu, then visit every top-level page | Window renders without corruption; every page opens; app remains usable without a tray |
| Tray | Hide/show the window from the tray; on stock GNOME also repeat with AppIndicator disabled | StatusNotifierItem works where supported; missing GNOME tray never makes the app unreachable |
| Hotkey | Configure `Ctrl+Alt+R`; press it in Terminal, Firefox, and LibreOffice | One press/release action per chord in every app; no repeats or stuck modifiers |
| Privacy invariant | While log capture runs, type `UNRELATED-KEY-PROBE-7391` without the configured chord, stop the app, then run `grep -R 'UNRELATED-KEY-PROBE-7391' "$HW_EVIDENCE_DIR"` | Grep returns no raw-key or phrase record except this checklist/evidence command itself; public app events contain configured action IDs only |
| Text injection | Dictate `known injection sentence` into Terminal, Firefox, LibreOffice, and an Electron editor | Exact text enters the focused normal text field once |
| Password guard | Focus a password field and trigger injection | App refuses best-effort where AT-SPI identifies the field and reports the refusal; record any undetectable field explicitly |
| Clipboard fallback | Temporarily remove membership with `sudo gpasswd -d "$USER" hyperwhisper-input`, re-login, dictate once, then restore membership | Transcript remains on clipboard and a notification explains that uinput is unavailable; no text is lost |
| Clipboard restore | Put text, HTML, and an image on the clipboard in turn; inject text; wait the configured restore delay | Every original clipboard format is restored after the delay |
| Audio | Select the default PulseAudio/pipewire-pulse microphone and record the phrase `deterministic microphone sentence` | Recording starts/stops once and playback/transcript contains the spoken phrase |
| File transcription | Select a known WAV and transcribe locally | Progress reaches completion; history and output are persisted |
| Local Parakeet | Download Parakeet v2, select it, and transcribe a known 16 kHz WAV | Packaged sherpa-onnx daemon reports `provider=cpu`, returns a non-empty accurate transcript, and exits cleanly after model unload |
| OCR portal | Enable screen OCR and trigger it once, then repeat and deny the portal dialog | Capture occurs only after the user action/consent; denial is handled without stale OCR text |
| Local API | Enable the API, then inspect the discovery file using the commands below | Listener is IPv4 loopback-only, unauthenticated call is `401`, discovery file is `0600`, and no recording-delete route exists |
| Backup | Export, import into a clean profile, then re-export | Linux settings/modes/vocabulary survive and foreign `macos`/`windows` extension slices remain byte-semantically equal |
| Autostart | Enable autostart, reboot, and sign in | One app instance starts; disabling autostart prevents the next-login launch |

Local API evidence commands (redact the token value from saved output):

```bash
discovery="${XDG_DATA_HOME:-$HOME/.local/share}/hyperwhisper/local-api.json"
stat -c '%n mode=%a owner=%U' "$discovery" \
  | tee "$HW_EVIDENCE_DIR/local-api-permissions.txt"
test "$(stat -c '%a' "$discovery")" = 600
port="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["port"])' "$discovery")"
token="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["token"])' "$discovery")"
ss -ltnp | grep "127.0.0.1:${port}" | tee "$HW_EVIDENCE_DIR/local-api-listener.txt"
test "$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}/models")" = 401
curl -fsS -H "Authorization: Bearer ${token}" "http://127.0.0.1:${port}/models" \
  | python3 -m json.tool > "$HW_EVIDENCE_DIR/local-api-models.json"
unset token
```

## Environment-specific gates

### GNOME Wayland

```bash
test "$XDG_SESSION_TYPE" = wayland
printf '%s\n' "$XDG_CURRENT_DESKTOP" | grep -qi GNOME
gnome-extensions list --enabled | tee "$HW_EVIDENCE_DIR/gnome-extensions.txt"
busctl --user list | grep -i hyperwhisper \
  | tee "$HW_EVIDENCE_DIR/gnome-extension-dbus.txt"
```

With the companion extension enabled, switch focus among Terminal, Firefox,
and LibreOffice and verify the matching mode activates. Disable the extension
and repeat: the app must remain functional in default mode and show the Settings
note explaining reduced active-app detection. Verify portal consent and denial.

### GNOME Xorg

```bash
test "$XDG_SESSION_TYPE" = x11
printf '%s\n' "$XDG_CURRENT_DESKTOP" | grep -qi GNOME
xprop -root _NET_ACTIVE_WINDOW | tee "$HW_EVIDENCE_DIR/x11-active-window.txt"
```

Switch among Terminal, Firefox, and LibreOffice. The active mode must follow the
X11 active window without the GNOME companion extension. Run global hotkey and
injection checks on both a native X11 app and an Electron app.

### KDE Wayland

```bash
test "$XDG_SESSION_TYPE" = wayland
printf '%s\n' "$XDG_CURRENT_DESKTOP" | grep -qi KDE
busctl --user list | grep -Ei 'kwin|kde' | tee "$HW_EVIDENCE_DIR/kde-dbus.txt"
```

Switch among Konsole, Firefox, and LibreOffice. The active mode must follow the
KDE D-Bus signal. Verify the portal consent/denial path and the StatusNotifierItem
tray on Plasma.

## Physical GPU release gate — never satisfied by CI

This gate requires a real x86_64 workstation. A VM, Virtio GPU, llvmpipe,
lavapipe, or successful native-library load does not count as GPU inference.

Capture hardware and driver evidence:

```bash
lspci -nnk | grep -A4 -Ei 'VGA|3D|Display' \
  | tee "$HW_EVIDENCE_DIR/gpu-pci.txt"
vulkaninfo --summary | tee "$HW_EVIDENCE_DIR/vulkan-summary.txt"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi | tee "$HW_EVIDENCE_DIR/nvidia-smi.txt"
fi
```

Reject software rendering explicitly:

```bash
if grep -Eqi 'llvmpipe|lavapipe|software rasterizer' "$HW_EVIDENCE_DIR/vulkan-summary.txt"; then
  echo 'FAIL: software Vulkan is not a physical-GPU gate' >&2
  exit 1
fi
```

Then perform and record all of the following:

1. Download a supported Whisper model through the app and transcribe a known WAV
   with Vulkan selected. Evidence must include the redacted runtime line naming
   the Vulkan backend, transcript, wall time, model ID, and peak memory.
2. On an NVIDIA CUDA 12 host, run local LLM post-processing once with CUDA and
   record the backend/model/runtime line. Repeat with CUDA disabled and prove the
   CPU fallback succeeds.
3. Restart the app and repeat one inference to catch native runtime/load-order
   failures.
4. Record `PASS` only when actual inference completed on the named physical GPU.

CI may prove that Vulkan/CUDA assets publish and runtime probing does not crash;
it must never label those checks as GPU validation.

## Upgrade and removal — gate once per distro

```bash
sudo apt-get install --yes "$HW_DEB"
sudo apt-get install --yes --reinstall "$HW_DEB"
test "$(grep -c 'hyperwhisper-input' /etc/group)" = 1
test -f /etc/udev/rules.d/70-hyperwhisper-input.rules
sudo apt-get remove --yes hyperwhisper
test ! -e /etc/udev/rules.d/70-hyperwhisper-input.rules
sudo apt-get purge --yes hyperwhisper
```

Expected: configure/reinstall is idempotent, removal deletes the generated udev
rule, purge succeeds repeatedly, no user-home files are created or removed by
maintainer scripts, and the dedicated system group is deliberately retained to
avoid GID reuse or silently mutating administrator-managed memberships.
