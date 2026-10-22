//
//  BackupKeyboardShortcuts.swift
//  hyperwhisper
//
//  The KeyboardShortcuts key combos in a settings backup (#1481).
//
//  Before this, `BackupShortcutSettings` carried the push-to-talk and Quick
//  Capture preferences but no key combo, so a restore silently lost every
//  custom shortcut. The combos now ride in `shortcuts.keyboardShortcuts`
//  (v1) and `platformExtensions.macos.settings.shortcuts.keyboardShortcuts`
//  (v2 — the shared core carries the macOS `shortcuts` category whole).
//
//  Restore rules:
//  - no `keyboardShortcuts` map (an older backup): every shortcut is left alone;
//  - a name absent from the map: that shortcut is left alone;
//  - `null`: the action has no shortcut, exactly as when the user clears it;
//  - a name this build does not register (a newer build's action): ignored.
//
//  The two pure functions take raw-value strings, so the tests reach them
//  without the live hotkeys and without importing KeyboardShortcuts.
//

import Foundation
import KeyboardShortcuts

enum BackupKeyboardShortcuts {

    /// The combo of every name in `names`, for export. `read` returns
    /// `.unassigned` when the action has no shortcut; that is written as
    /// `null`, so the restore clears it rather than leave a stale local combo.
    static func snapshot(
        names: [String],
        read: (String) -> BackupShortcutBinding
    ) -> [String: BackupShortcutBinding] {
        var map: [String: BackupShortcutBinding] = [:]
        for name in names {
            map[name] = read(name)
        }
        return map
    }

    /// The writes an imported map implies, in `knownNames` order. Absent
    /// entries and unknown names produce no write.
    static func assignments(
        from imported: [String: BackupShortcutBinding]?,
        knownNames: [String]
    ) -> [(name: String, binding: BackupShortcutBinding)] {
        guard let imported else { return [] }
        return knownNames.compactMap { name in
            imported[name].map { (name: name, binding: $0) }
        }
    }

    // MARK: - Live store

    /// Every shortcut name the app registers.
    static var registeredNames: [String] {
        KeyboardShortcuts.Name.allCases.map(\.rawValue)
    }

    /// The live combos, for an export.
    @MainActor
    static func liveSnapshot() -> [String: BackupShortcutBinding] {
        snapshot(names: registeredNames) { rawValue in
            guard let name = KeyboardShortcuts.Name.allCases.first(where: { $0.rawValue == rawValue }),
                  let shortcut = KeyboardShortcuts.getShortcut(for: name) else {
                return .unassigned
            }
            return .combo(carbonKeyCode: shortcut.carbonKeyCode, carbonModifiers: shortcut.carbonModifiers)
        }
    }

    /// Writes an imported map onto the live shortcuts. The caller posts
    /// `.shortcutDidChange` afterwards, as it does for the other shortcut
    /// settings.
    @MainActor
    static func applyLive(_ imported: [String: BackupShortcutBinding]?) {
        for assignment in assignments(from: imported, knownNames: registeredNames) {
            guard let name = KeyboardShortcuts.Name.allCases.first(where: { $0.rawValue == assignment.name }) else {
                continue
            }
            switch assignment.binding {
            case .unassigned:
                KeyboardShortcuts.setShortcut(nil, for: name)
            case .combo(let carbonKeyCode, let carbonModifiers):
                KeyboardShortcuts.setShortcut(
                    KeyboardShortcuts.Shortcut(carbonKeyCode: carbonKeyCode, carbonModifiers: carbonModifiers),
                    for: name
                )
            }
        }
    }
}
