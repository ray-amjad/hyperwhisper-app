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
- `CapturedTargetFocusAvailable` is true only in an X11 session with `xprop` and
  `wmctrl`. Before uinput paste, the captured window must still exist and is
  focused twice: before the secure-field query and immediately before paste.
  Native Wayland does not expose a general-purpose protocol for focusing an
  arbitrary previously captured client, so this capability is false there and
  injection safely stops at clipboard copy.
- The `wl-clipboard` and `xclip` adapters capture every advertised MIME payload
  (up to 64 formats and 32 MiB), but their command-line ownership model cannot
  restore multiple MIME types as one clipboard data source. Therefore
  `PreservesAllClipboardFormats` is false and a multi-format restore reports
  `clipboard_restore_partial` after restoring the preferred representation.

Before shipping, manually verify the advertised capability values and behavior
on GNOME Wayland, KDE Plasma Wayland, and an Xorg session. Exact multi-MIME
restore requires a future native Wayland/X11 data-source implementation; the
service seam and deterministic tests already pass the complete binary snapshot
to any backend that provides it.
