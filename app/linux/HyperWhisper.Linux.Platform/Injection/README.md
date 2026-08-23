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
  MiB). X11 restoration uses an in-process `libX11` selection owner and serves
  every captured target exactly while the service lives, so
  `PreservesAllClipboardFormats` is true. Core Wayland requires a compositor
  input serial to set `wl_data_device` selection plus a live protocol event loop;
  this non-window service owns neither. `wl-clipboard` can own only one requested
  MIME type, so Wayland reports `PreservesAllClipboardFormats=false` and
  `clipboard_restore_partial` for multi-format snapshots.
- Every clipboard, X11 focus, and Python AT-SPI helper is bounded by a five-second
  deadline. Timeout terminates the helper process tree and degrades to the safe
  capability/fallback result.

Before shipping, manually verify the advertised capability values and behavior
on GNOME Wayland, KDE Plasma Wayland, and an Xorg session. Exact Wayland
multi-MIME restore requires the future Avalonia window integration to supply a
valid seat serial and host a `wl_data_source` event loop for the selection
lifetime. X11 exact restoration is implemented by the current native owner.
