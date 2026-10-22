//
//  LocalAPITransientMode.swift
//  hyperwhisper
//
//  The per-request `Mode` that `/transcribe` and `/post-process` build when a
//  caller passes overrides (issue #1509).
//

import CoreData
import Foundation

/// The values that mark a per-request Local API `Mode`, and the test that
/// recognises one a pre-#1509 build leaked into the store.
///
/// Not actor-isolated: `PersistenceController` reads it during its own init,
/// which is not on the main actor.
enum LocalAPITransientModeMarker {

    /// The name `/transcribe` gives its per-request Mode.
    static let transcribeName = "__local_api_transient__"

    /// The name `/post-process` gives its per-request Mode.
    static let postProcessName = "__local_api_postproc_transient__"

    /// The `sortOrder` both endpoints give the per-request Mode. A user Mode
    /// gets `maxSortOrder + 1`, so a real Mode reaches this value only after
    /// 32,767 modes or after a leaked row already holds it.
    static let sortOrder = Int16.max

    /// True for a row that is a leaked per-request Mode.
    ///
    /// Every field must match. The name is one of the two endpoint names,
    /// either bare or with the ` 2`, ` 3`, … suffix that
    /// `PersistenceController.repairModeNames()` gives a duplicate at launch
    /// (the issue's 12 copies were `__local_api_transient__` to
    /// `__local_api_transient__ 12`). The sort order is the marker above, and
    /// the row is neither the default Mode nor a seeded one, which is how both
    /// endpoints build it. A Mode a user named this way by hand still keeps an
    /// ordinary sort order, so it does not match.
    static func isLeakedRow(
        name: String?,
        sortOrder: Int16,
        isDefault: Bool,
        isSystemProvided: Bool
    ) -> Bool {
        guard sortOrder == Self.sortOrder, !isDefault, !isSystemProvided else { return false }
        guard let name else { return false }
        for base in [transcribeName, postProcessName] {
            if name == base { return true }
            let prefix = base + " "
            guard name.hasPrefix(prefix) else { continue }
            let suffix = name.dropFirst(prefix.count)
            if !suffix.isEmpty, suffix.allSatisfy({ $0.isASCII && $0.isNumber }) {
                return true
            }
        }
        return false
    }
}

/// A `Mode` that lives for one Local API request and can never reach the
/// persistent store.
///
/// # Why a scratch context (issue #1509)
///
/// Both endpoints used to insert this Mode straight into the shared
/// `viewContext` and rely on never saving it. Any OTHER save of that context
/// while a request ran (a Transcribe File transcript, a History retry, a Modes
/// edit) committed it, and the later `delete` was only a pending change. The
/// row then showed in Select Mode and Transcribe File, survived relaunch, and
/// piled up. While it was unsaved, every `viewContext` fetch (`GET /modes`,
/// a backup export, the default-mode repair, the Model Library delete check)
/// saw it as well.
///
/// The Mode is now inserted into a private main-queue context whose parent is
/// the `viewContext`. Nothing calls `save()` on that context, so the Mode never
/// reaches the parent, let alone the store, whatever else saves meanwhile, and
/// a `viewContext` fetch does not see it. It is main-queue for the same reason
/// the old Mode lived in the `viewContext`: every reader is on the main actor.
/// The Mode only holds attribute values copied off a baseline, never a
/// relationship to an object in another context.
///
/// # The in-flight registry (issue #1446)
///
/// The Model Library refused to delete a model a Local API request was using,
/// only because it found the unsaved Mode in its `viewContext` fetch. Moving
/// the Mode out would silently drop that protection, so each live instance
/// registers here until `end()`, and the delete check reads `inFlightModes`.
///
/// The caller MUST call `end()` when the request finishes, on every path.
/// Both endpoints do it in a `defer`.
@MainActor
final class LocalAPITransientMode {

    /// The per-request Mode. Read it on the main actor only.
    let mode: Mode

    /// Holds the Mode. Kept alive for as long as the handle is, and never saved.
    private let scratchContext: NSManagedObjectContext

    private var hasEnded = false

    /// Live handles, keyed by identity.
    private static var inFlight: [ObjectIdentifier: LocalAPITransientMode] = [:]

    /// The Modes of every Local API request still running.
    static var inFlightModes: [Mode] {
        inFlight.values.map { $0.mode }
    }

    /// True when `mode` belongs to a Local API request still running.
    static func isInFlight(_ mode: Mode) -> Bool {
        inFlight.values.contains { $0.mode === mode }
    }

    /// - Parameters:
    ///   - name: `LocalAPITransientModeMarker.transcribeName` or `.postProcessName`.
    ///   - parent: the context the scratch context hangs off. The endpoints
    ///     pass the shared `viewContext`; tests pass an in-memory one.
    init(name: String, parent: NSManagedObjectContext) {
        let scratch = NSManagedObjectContext(concurrencyType: .mainQueueConcurrencyType)
        scratch.parent = parent
        scratch.name = "HyperWhisper.localAPITransientMode"
        scratch.undoManager = nil
        scratchContext = scratch

        let mode = Mode(context: scratch)
        mode.id = UUID()
        mode.name = name
        mode.isDefault = false
        mode.isSystemProvided = false
        mode.sortOrder = LocalAPITransientModeMarker.sortOrder
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        self.mode = mode

        Self.inFlight[ObjectIdentifier(self)] = self
    }

    /// Ends the request: the Mode leaves the in-flight registry and is dropped
    /// from its scratch context. Safe to call more than once.
    func end() {
        guard !hasEnded else { return }
        hasEnded = true
        Self.inFlight[ObjectIdentifier(self)] = nil
        // An unsaved insert, so this only forgets it. No save, here or
        // anywhere: the scratch context is never saved.
        scratchContext.delete(mode)
    }
}
