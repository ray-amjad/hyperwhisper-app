//
//  DefaultModePolicy.swift
//  hyperwhisper
//
//  The default-mode invariant, over Core Data `Mode` objects (issue #536).
//

import CoreData
import Foundation

/// > Exactly one mode carries `isDefault`, and that mode's name is fixed.
///
/// Both halves used to be enforced only by the mode editors — each head
/// disabled the Name field for the mode it happened to be showing, and nothing
/// below the UI checked either one. `PATCH /modes` could therefore rename the
/// default, or set the flag on a second mode, and the editor would then show a
/// disabled field reading the new name directly above the caption PR #535 added
/// saying the name cannot be changed.
///
/// The DECISION is in the shared Rust core (`hw-modes`), not here. Applying it
/// is per-head — this one mutates a Core Data context — but the choice of WHICH
/// mode is promoted when a restored backup left none flagged, or two, is a
/// cross-platform contract: three heads that chose differently would restore the
/// same backup to a different default mode each. `DefaultModePolicy` on the .NET
/// side calls the same core functions.
enum DefaultModePolicy {

    /// Make exactly one of `modes` the default, in place.
    ///
    /// - Parameters:
    ///   - modes: EVERY mode that will exist after the write, in display order.
    ///     "Exactly one" cannot be decided from a subset, and the caller's order
    ///     is the tie-break between equal sort orders.
    ///   - preferred: the mode the caller is trying to make the default, or
    ///     `nil` when it is only repairing a set that arrived broken.
    /// - Returns: whether anything changed. `false` means the set already
    ///   satisfied the invariant, so the caller can skip its save.
    @discardableResult
    static func apply(to modes: [Mode], preferred: UUID? = nil) -> Bool {
        let plan = modePlanDefault(rows: flags(modes), preferred: preferred?.uuidString)
        guard plan.changed else { return false }

        let cleared = Set(plan.clearIds.map { $0.lowercased() })
        for mode in modes where !mode.isDeleted {
            guard let key = mode.id?.uuidString.lowercased() else { continue }
            if cleared.contains(key) {
                mode.isDefault = false
            }
            if let winner = plan.defaultId?.lowercased(), key == winner {
                mode.isDefault = true
            }
        }
        return true
    }

    /// Whether `mode` may be renamed to `newName`. The default mode's name is
    /// fixed; every other mode renames freely.
    static func canRename(_ mode: Mode, to newName: String) -> Bool {
        modeCheckNameChange(
            isDefault: mode.isDefault,
            storedName: mode.name ?? "",
            newName: newName
        ) == .allowed
    }

    /// Whether `id` may have its default flag written to `requested`. `modes` is
    /// the set as it stands BEFORE the write. Only one combination is refused:
    /// clearing the flag on the one mode that carries it.
    static func canWriteDefaultFlag(_ modes: [Mode], id: UUID, requested: Bool) -> Bool {
        modeCheckDefaultFlag(
            rows: flags(modes),
            id: id.uuidString,
            requestedIsDefault: requested
        ) == .allowed
    }

    /// A deleted-but-unsaved object is not part of the set any more, and reading
    /// its properties is a Core Data use-after-delete, so it never reaches the
    /// core.
    private static func flags(_ modes: [Mode]) -> [HwModeFlags] {
        modes.compactMap { mode in
            guard !mode.isDeleted, let id = mode.id else { return nil }
            return HwModeFlags(
                id: id.uuidString,
                isDefault: mode.isDefault,
                sortOrder: Int32(mode.sortOrder)
            )
        }
    }
}
