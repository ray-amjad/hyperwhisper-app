//
//  PersistenceController+Writer.swift
//  hyperwhisper
//
//  Main-context saves.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    // MARK: - Save Operations
    
    /// Saves the view context if there are changes
    /// Core Data automatically posts NSManagedObjectContextDidSave notification when saving
    func save() {
        let context = container.viewContext
        
        // Early return if no changes to save (performance optimization)
        guard context.hasChanges else {
            // Nothing pending means somebody else drained this context, so it
            // still accepts writes. Without this the streak below could only
            // grow: `hyperwhisperApp` and the Local API's modes endpoint both
            // save the shared `viewContext` directly, and a stale streak would
            // report a healthy context as unwritable.
            CoreDataSaveDiagnostics.recordSuccess(contextKey: CoreDataSaveDiagnostics.viewContextKey)
            return
        }

        do {
            // Persist changes to the Core Data store
            // Core Data automatically posts notification
            try context.save()
            CoreDataSaveDiagnostics.recordSuccess(contextKey: CoreDataSaveDiagnostics.viewContextKey)
        } catch {
            // In production, handle this error appropriately
            let nsError = error as NSError
            // WHY THE SHAPE MATTERS HERE (Sentry HYPERWHISPER-VC): this path has
            // no `rollback()`, unlike the background writer below. One rejected
            // row therefore stays pending on the view context and every later
            // save fails on it — which is what 19 events, 4 hours apart, from a
            // single process actually were. `pending_*` growing across a streak
            // says that; a stack trace cannot.
            //
            // Read here rather than before the `do`: a save that throws keeps its
            // pending changes (that is the poisoning), so the sets are intact,
            // and a save that works pays nothing for this.
            let shape = CoreDataSaveDiagnostics.contextShape(context)
            // One event, not two. The duplicate `SentryService.capture` that
            // used to follow this line carried the same message with a
            // stack-trace fingerprint, which is how one fault became both
            // HYPERWHISPER-VB and HYPERWHISPER-VC.
            AppLogger.logCoreData(
                .save(site: PersistenceController.viewContextSaveSite, contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: shape
            )
        }
    }

    /// Save-site slug for the `viewContext` save. A constant so the log line, the
    /// Sentry tag and the failure streak cannot drift apart.
    private static let viewContextSaveSite = "view_context_save"
}
