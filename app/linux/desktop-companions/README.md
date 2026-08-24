# Linux desktop companions

Wayland has no standard active-window portal. HyperWhisper therefore ships
optional companions returning only
`CONTEXT|pid|base64(application)|base64(title)` from `GetActiveWindow`.
Document text and key events are never accepted.

`gnome/42` supports Ubuntu 22.04 GNOME Shell 42 and the legacy API through 44.
`gnome/45` is the maintained ESM path for GNOME 45+ and this release declares
versions 45 through 50. Each new GNOME major must be added to `metadata.json`
after a Shell-session smoke test. Run
`hyperwhisper-companionctl install-gnome` as the signed-in desktop user; it
selects the matching variant.

For Plasma 5 or 6, `install-kde` installs and enables the KWin script and a user
autostart bridge. The bridge owns `org.kde.KWin.HyperWhisper`, object
`/HyperWhisper`, with method `org.kde.KWin.HyperWhisper.GetActiveWindow`.

Debian maintainer scripts only install immutable `/usr/share` assets and never
inspect or mutate a home directory. Enablement/removal are explicit user actions.
