# Vendored: sindresorhus/KeyboardShortcuts

This is a vendored copy of
[sindresorhus/KeyboardShortcuts](https://github.com/sindresorhus/KeyboardShortcuts)
at tag `2.4.0`, with three small fixes applied on top. License: MIT (see `license`).

## Why it is vendored

On macOS 26 (Tahoe) and later, the shortcut recorder in every released version of the
package (verified on 0.7.1 through 3.0.1) is unusable:

- Clicking **Record Shortcut** focuses the field, but key presses type characters
  into the search field instead of recording a shortcut.
- The clear (x) button highlights but never fires.
- The user gets no error or message of any kind.

The root causes are three independent defects in the package, diagnosed in
[upstream issue #241](https://github.com/sindresorhus/KeyboardShortcuts/issues/241).
Upstream has not shipped a fix yet. This vendored copy carries the three fixes,
backported from the issue author's fork onto the `2.4.0` code the app already used
(so there is no 2.x → 3.x API jump):

| # | Defect | Fix location |
|---|--------|--------------|
| 1 | `LocalEventMonitor` held its `NSEvent.addLocalMonitorForEvents` token **weakly**, so the monitor deallocated when the autorelease pool drained — before the user could press anything. | `Sources/KeyboardShortcuts/Utilities.swift` |
| 2 | `becomeFirstResponder` mutates `cancelButtonCell` while the field editor is installed, which makes AppKit end/restart editing; the resulting `controlTextDidEndEditing` tore down the key monitor right after it was armed. | `Sources/KeyboardShortcuts/RecorderCocoa.swift` |
| 3 | The recorder's event monitor swallowed `mouseUp` inside the field, so the clear button could be pressed but never fired. | `Sources/KeyboardShortcuts/RecorderCocoa.swift` |

Original fix commits (written against 3.0.1, backported here to 2.4.0):
[c3f86c6](https://github.com/ppardi/KeyboardShortcuts/commit/c3f86c6),
[3f517c0](https://github.com/ppardi/KeyboardShortcuts/commit/3f517c0),
[d4bd30d](https://github.com/ppardi/KeyboardShortcuts/commit/d4bd30d).

One extra hardening change not in the upstream fork: `LocalEventMonitor.stop()` now
nils its token after `removeMonitor`, so the `deinit → stop()` path cannot remove the
same token twice once the reference is strong.

## How to remove this vendored copy

When upstream ships a release that fixes issue #241:

1. Delete `app/macos/Vendor/KeyboardShortcuts/`.
2. In `hyperwhisper.xcodeproj`, replace the local package reference
   `Vendor/KeyboardShortcuts` with the upstream URL
   `https://github.com/sindresorhus/KeyboardShortcuts` at the fixed version.

Every patched line is marked with a comment linking to upstream issue #241, so the
delta against stock `2.4.0` is easy to audit: `git diff` this directory against the
`2.4.0` tag of the upstream repository.
