# Linux release verification matrix

CI proves compilation, dependency-free tests, Xvfb rendering, the self-contained
`linux-x64` publish, and Debian package structure. It does **not** prove real
compositor behavior, portal UX, physical input-device access, or GPU inference.
Every release candidate must therefore complete this checklist on three clean
x86_64 desktop installations plus a physical GPU host.

This checklist is **recommended, not enforced**. The release workflow no longer
blocks on it. Recording the run is still the only way to prove a release was
tested on real hardware: after completing the matrix, upload each redacted
evidence bundle, record its HTTPS URL and SHA-256 in
`release-evidence/VERSION.json`, and have the manifest reviewed against the
exact tested commit. The release commit may add only evidence manifests after
that commit. A manually approved dry run builds a retrievable package without
publishing; a publishing run validates any manifest it finds against this
ancestry/diff invariant and includes it in the published checksums. A manifest
that fails validation still fails the release.

Use `PASS`, `FAIL`, `BLOCKED`, or `NOT IMPLEMENTED` for every result. Steps
marked **gate** are the ones a manifest must record as `PASS`;
`NOT IMPLEMENTED` is not a pass.

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
| First-run onboarding | Move the existing profile aside, launch once, inspect every capability row, choose a real mode and microphone, complete one test dictation, relaunch; repeat once using Skip setup | Reported audio, clipboard/uinput, shortcut, portal, and local-engine capabilities match the host; selections reach the live app; test dictation uses the normal privacy/storage path; completion and skip each remain dismissed after relaunch |
| Tray | Hide/show the window from the tray; on stock GNOME also repeat with AppIndicator disabled | StatusNotifierItem works where supported; missing GNOME tray never makes the app unreachable |
| Hotkey | Configure `Ctrl+Alt+R`; press it in Terminal, Firefox, and LibreOffice | One press/release action per chord in every app; no repeats or stuck modifiers |
| Privacy invariant | While log capture runs, type `UNRELATED-KEY-PROBE-7391` without the configured chord, stop the app, then run `grep -R 'UNRELATED-KEY-PROBE-7391' "$HW_EVIDENCE_DIR"` | Grep returns no raw-key or phrase record except this checklist/evidence command itself; public app events contain configured action IDs only |
| Text injection | Dictate `known injection sentence` into Terminal, Firefox, LibreOffice, and an Electron editor | Exact text enters the focused normal text field once |
| Password guard | Focus a password field and trigger injection | App refuses best-effort where AT-SPI identifies the field and reports the refusal; record any undetectable field explicitly |
| Clipboard fallback | Temporarily remove membership with `sudo gpasswd -d "$USER" hyperwhisper-input`, re-login, dictate once, then restore membership | Transcript remains on clipboard and a notification explains that uinput is unavailable; no text is lost |
| Clipboard restore | Put text, HTML, and an image on the clipboard in turn; inject text; wait the configured restore delay | Every original clipboard format is restored after the delay |
| Audio | Select the default PulseAudio/pipewire-pulse microphone and record the phrase `deterministic microphone sentence` | Recording starts/stops once and playback/transcript contains the spoken phrase |
| Crash audio salvage | Start a recording, force-kill the app after audio is written, relaunch, then repeat with an empty file, a deliberately truncated WAV data chunk, and a symlink named like an app recording | The valid/incomplete app-owned WAV is header-repaired and imported once as recoverable failed history; empty/unsafe input is quarantined without following links or exposing its path/content; a second relaunch creates no duplicate history row; the current active recording is never claimed |
| Batch VAD corpus | Enable voice-activity trimming and transcribe a >=30 s corpus containing leading/trailing silence, speech-only audio, all-silence audio, and a cancellation during preprocessing; repeat with trimming disabled | Silero processes bounded 512-sample frames, preserves speech and trims only detected silence; no-speech, inference error, and cancellation preserve the original source; a successful trim records its separate private path and retry/file-import behavior matches recording behavior |
| Word timestamps | Enable timestamp storage and transcribe a known local-Whisper fixture with spoken word boundaries, inspect the history database, relaunch, then retry; repeat after disabling the setting | Enabled local Whisper persists finite monotonic segment/word times within the audio duration in a `basis=raw_text` payload aligned to the raw transcript, and the payload survives relaunch/retry; disabled runs persist no timestamp payload; post-processing never changes the timestamp alignment basis |
| Sound-effect gain | With recording cues enabled, repeat start/stop at volume 0, 0.5, and 1 using the same output device | Zero is silent, half is audibly reduced, full is not clipped, and changing gain does not alter microphone capture or other application audio |
| File transcription | Select a known WAV and transcribe locally | Progress reaches completion; history and output are persisted |
| Local Parakeet | Download Parakeet v2, select it, and transcribe a known 16 kHz WAV | Packaged sherpa-onnx daemon reports `provider=cpu`, returns a non-empty accurate transcript, and exits cleanly after model unload |
| Local Parakeet live | In Model Library select the installed Parakeet v2 streaming row and press **Use for live transcription**; dictate `parakeet local live sentence`, pause, continue, then stop; repeat once and cancel mid-sentence | The credential-free local daemon stays alive for the session; bounded rolling-window partial and committed text appears without duplicate words; stop injects and persists the final transcript exactly once; cancel injects/persists nothing and leaves no daemon or stuck session |
| Local Nemotron live | Download Nemotron 3.5 Streaming, select its streaming row, press **Use for live transcription**, choose `auto` and dictate short phrases in two advertised production locales; stop each session; repeat and cancel once | The packaged sherpa-onnx online stream uses the installed 560 ms multilingual artifact without credentials; each language produces non-empty incremental/final text; stop injects and persists exactly once; cancellation leaves no text, process, or stuck session |
| Interim transcript preview privacy | During each local-live check, keep the console capture running and record the preview on video; while a partial phrase is visible, switch focus and attempt to click through its full bounds. In a separate session dictate a unique token, cancel before finalization, then run `grep -R '<unique-partial-only-token>' "$HW_EVIDENCE_DIR" "${XDG_DATA_HOME:-$HOME/.local/share}/hyperwhisper"` | Preview text is bounded, updates without taking focus, and is click-through on the tested X11/XWayland desktop; committed text replaces overlapping partials without duplication; preview clears on final and cancel; the cancelled partial-only token is absent from logs, settings, database/history, and other persisted app data |
| OCR portal | Enable screen OCR and trigger it once, then repeat and deny the portal dialog | Capture occurs only after the user action/consent; denial is handled without stale OCR text |
| Local API | Enable the API, then inspect the discovery file using the commands below | Listener is IPv4 loopback-only, unauthenticated call is `401`, discovery file is `0600`, and no recording-delete route exists |
| Backup | Export, import into a clean profile, then re-export | Linux settings/modes/vocabulary survive and foreign `macos`/`windows` extension slices remain byte-semantically equal |
| Autostart | Enable autostart, reboot, and sign in | One app instance starts; disabling autostart prevents the next-login launch |
| Package update awareness | Open About, record `apt-cache policy hyperwhisper`, press Refresh package status, then repeat with `apt-cache` hidden from `PATH` on a PackageKit host | UI agrees with existing package metadata and gives distribution-updater instructions; it never runs cache refresh, install, upgrade, elevation, or self-update |
| Locale and RTL | Switch to German, Arabic, and Simplified Chinese; visit onboarding, Settings, About, dialogs, and tray; capture screenshots with non-sensitive sample data | Exact shared translations resolve from reviewed satellites, deliberate Linux-only fallback is visible where review is pending, Arabic mirrors layout, and IDs/paths/models/protocol values remain byte-identical |

## Credentialed service gates — gate on one clean desktop

These checks require release-test accounts or API keys and cannot be replaced
by fake HTTP transports in CI. Use a short non-sensitive audio fixture, redact
request identifiers and account data, and never write credentials to evidence.

| Check | Exact action | Acceptance condition |
|---|---|---|
| Cloud batch STT matrix | With production credentials, transcribe the fixture once through OpenAI, Groq, ElevenLabs, Mistral, Grok, Deepgram, AssemblyAI, Soniox, Gemini, Azure MAI, Google Chirp, and HyperWhisper Cloud | Every enabled provider returns a non-empty accurate transcript through its advertised model; history records the selected provider/model and no credential appears in logs |
| Cloud file uploads | Import one supported non-WAV fixture through every enabled cloud provider, including a provider-native container | The original is never modified; upload limits are enforced before transmission; the provider receives the supported container and the app-owned copy is cleaned according to storage policy |
| Live streaming matrix | Stream the microphone through Deepgram, ElevenLabs, OpenAI, Grok, and HyperWhisper Cloud | Partial/final text arrives, stop completes once, the final transcript is persisted, and cancellation/timeout leaves no stuck session |
| Cloud post-processing matrix | Enhance a fixed transcript through OpenAI, Anthropic, Groq, Grok, Gemini, Cerebras, Mistral, HyperWhisper Cloud, and a loopback custom endpoint | Each enabled route uses the selected model and returns the expected transformed text; malformed/rejected output retains the original transcript |
| HyperWhisper account | Activate a release-test account, refresh details and credits, open purchase/manage links, then deactivate locally | Status/details/credits refresh without exposing the key; links contain no account identifier; deactivation behavior matches the server capability reported by the app |
| Local LLM CPU | Run local post-processing with CUDA disabled | A supported model produces the expected transformed text on CPU and reports the CPU backend |

If a provider is intentionally unavailable for the release, record `BLOCKED` or
`NOT IMPLEMENTED`; removing it from the UI/catalog is a product-scope decision,
not a verification pass.

## Linux-specific localization content gate — human review required

Automated checks prove that every supported culture has a loadable satellite,
exact semantics-identical macOS translations are reused, fallback is deterministic,
format placeholders remain valid, and RTL metadata is applied. They do not prove
that Linux-specific English fallback copy has been professionally translated.

For any release claiming complete Linux UI-language parity, a native speaker for
each supported culture must review every key absent from
`AvaloniaLocalizationBridge.LinuxTranslatedKeys(culture)`, supply the translation,
and record reviewer, culture, commit, screenshots, placeholder audit, and result in
the evidence bundle. Machine translation alone is not acceptance evidence. Do not
record this gate as `PASS` while any Linux-specific key still resolves to invariant
English, unless the reviewer explicitly confirms that English is the correct locale
value. Provider/model identifiers, paths, shortcut tokens, protocol values, command
examples, and redacted opaque IDs must remain untranslated.

Run the infrastructure checks before physical review:

```bash
dotnet run --project \
  app/linux/HyperWhisper.Linux.Localization.Tests/HyperWhisper.Linux.Localization.Tests.csproj \
  -c Release
```

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

Run the required all-MIME clipboard bridge gate from the graphical user session
(not SSH or a headless compositor):

```bash
HW_REQUIRE_XWAYLAND_CLIPBOARD_BRIDGE=1 dotnet run \
  --project app/linux/HyperWhisper.Linux.Platform.Tests/HyperWhisper.Linux.Platform.Tests.csproj \
  -c Release
```

The `XWayland owner bridges text HTML and PNG` test must pass. It captures the
three formats, replaces the clipboard with a transcript, restores through one
native owner, and reads each payload back through the independent `wl-paste`
Wayland client, including NUL and non-UTF-8 PNG bytes.

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

Run the same `HW_REQUIRE_XWAYLAND_CLIPBOARD_BRIDGE=1` command above from the
physical Plasma Wayland session and require the bridge test to pass. A missing
`DISPLAY` or a compositor that does not expose the XWayland selection to
`wl-paste` is a release blocker, not a skipped test.

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
