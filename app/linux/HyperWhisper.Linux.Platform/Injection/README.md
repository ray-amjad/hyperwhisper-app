# Linux text-injection desktop gates

The adapter always writes the transcript to the clipboard before attempting the
synthetic `Ctrl+V`. It never records or logs transcript text, keys, clipboard
payloads, or window identifiers.

## Capabilities and required desktop checks

- `SecureFieldGuardAvailable` requires `python3`, PyGObject, and the AT-SPI 2
  introspection package (`python3-gi` and `gir1.2-atspi-2.0` on Debian/Ubuntu).
  The guard refuses a focused AT-SPI `PASSWORD_TEXT` role. An unavailable or
  inconclusive accessibility query is explicitly best-effort and does not claim
  protection.
- On X11, `CapturedTargetFocusAvailable` requires `xprop` and `wmctrl`; the
  captured window is refocused and validated twice. Wayland prohibits clients
  from arbitrarily focusing another client, so the AT-SPI adapter instead
  captures a content-free identity (process id plus accessibility-tree path).
  Paste is allowed only when that exact accessible remains focused at both
  checks. A changed, missing, or unavailable accessible safely stops at copy.
- Both adapters capture every advertised MIME payload (up to 64 formats and 32
  MiB). Restoration uses one in-process `libX11` selection owner, replacing its
  prior in-memory snapshot atomically and serving every captured target exactly
  while the service lives. Stable Avalonia is hosted by X11/XWayland, so on
  GNOME and KDE Wayland the compositor's XWayland clipboard bridge exposes that
  multi-target selection to native Wayland clients. No snapshot bytes are
  written to temporary files. `PreservesAllClipboardFormats` is true only when
  that owner successfully connects to `DISPLAY`; a pure Wayland session without
  XWayland reports false. It can restore a genuinely single-format snapshot
  through `wl-copy`, but rejects a multi-format snapshot with
  `clipboard_restore_partial` before changing the clipboard.
- Every clipboard, X11 focus, and Python AT-SPI helper is bounded by a five-second
  deadline. Timeout terminates the helper process tree and degrades to the safe
  capability/fallback result.

Before shipping, manually verify text, HTML, and PNG round trips across the
XWayland bridge and the advertised capability values on GNOME Wayland, KDE
Plasma Wayland, and an Xorg session. `wl-paste` capture consists of one bounded
request per advertised MIME type, so a source application replacing its
clipboard midway causes capture to fail or produce only the still-readable
targets; the Wayland protocol does not offer third-party clients a transaction
that freezes another owner's payloads.
