//
//  CoreDataSaveDiagnostics.swift
//  hyperwhisper
//
//  Turns a Core Data failure into privacy-safe, queryable metadata.
//
//  WHY THIS EXISTS (Sentry HYPERWHISPER-VC / HYPERWHISPER-VB — "Core Data save
//  failed"): the report carries `category=coredata` and `operation=save` and
//  nothing else. It cannot say which entity failed, which attribute failed,
//  which Cocoa error code was raised, which of the eight save sites raised it,
//  or whether the same save has been failing for days. The only place any of
//  that appeared was the `recent_logs` blob, and that blob is the wrong answer
//  twice over: it is free text, so it cannot be searched or grouped, and it is
//  a dump of `NSError.description`, which inlines `NSValidationErrorObject` —
//  the failing row's own attribute values. For a `Transcript` row those values
//  are the transcript.
//
//  So this type reads the parts of the error that describe the SCHEMA and the
//  FAULT, and never the parts that describe the DATA.
//
//  PRIVACY: every value produced here is one of three things — an entity name,
//  an attribute name (both are identifiers written in the Core Data model, not
//  user input), a numeric code, or a count. `NSValidationErrorValue` holds the
//  offending value itself and is NEVER read. `localizedDescription` is never
//  read either: Cocoa interpolates values into it.
//

import CoreData
import Dispatch
import Foundation

enum CoreDataSaveDiagnostics {

    // MARK: - Error codes

    /// Stable slug for the `NSCocoaErrorDomain` codes a Core Data save raises.
    ///
    /// Deliberately written as numeric literals with the constant named beside
    /// them. The slug is a convenience for reading and grouping; `coredata_code`
    /// is emitted alongside it on every event, so an unmapped or mis-mapped code
    /// still reports the fault exactly — it just reports it as a number.
    static func codeName(_ code: Int) -> String {
        switch code {
        case 1550: return "managed_object_validation"
        case 1551: return "constraint_validation"
        case 1560: return "multiple_validation_errors"
        case 1570: return "missing_mandatory_property"
        case 1580: return "relationship_below_minimum_count"
        case 1590: return "relationship_above_maximum_count"
        case 1600: return "relationship_denied_delete"
        case 1610: return "number_too_large"
        case 1620: return "number_too_small"
        case 1630: return "date_too_late"
        case 1640: return "date_too_soon"
        case 1650: return "invalid_date"
        case 1660: return "string_too_long"
        case 1670: return "string_too_short"
        case 1680: return "string_pattern_mismatch"
        case 1690: return "invalid_uri"
        case 132000: return "context_locking"
        case 132010: return "coordinator_locking"
        case 133000: return "referential_integrity"
        case 133010: return "external_relationship"
        case 133020: return "merge"
        case 133021: return "constraint_merge"
        case 134000: return "store_invalid_type"
        case 134010: return "store_type_mismatch"
        case 134020: return "store_incompatible_schema"
        case 134030: return "store_save"
        case 134040: return "store_incomplete_save"
        case 134050: return "store_save_conflicts"
        case 134060: return "core_data"
        case 134070: return "store_operation"
        case 134080: return "store_open"
        case 134090: return "store_timeout"
        case 134100: return "store_incompatible_version_hash"
        case 134110: return "migration"
        case 134130: return "migration_missing_source_model"
        case 134140: return "migration_missing_mapping_model"
        case 134180: return "sqlite"
        default: return "cocoa_\(code)"
        }
    }

    // MARK: - Error shape

    /// Privacy-safe metadata for one Core Data error.
    ///
    /// - `coredata_domain` / `coredata_code` / `coredata_error` — the fault.
    /// - `coredata_entity` / `coredata_attribute` — WHICH row and WHICH column
    ///   the model rejected. Both are names from the `.xcdatamodeld`.
    /// - `coredata_detailed_count` / `coredata_detailed` — a save that breaks
    ///   more than one rule reports code 1560 and hides the real failures in
    ///   `NSDetailedErrorsKey`, where nothing ever looked.
    static func metadata(for error: NSError) -> [String: Any] {
        var metadata: [String: Any] = [
            "coredata_domain": error.domain,
            "coredata_code": error.code,
            "coredata_error": codeName(error.code)
        ]

        if let attribute = validationAttribute(of: error) {
            metadata["coredata_attribute"] = attribute
        }
        if let entity = validationEntity(of: error) {
            metadata["coredata_entity"] = entity
        }

        let detailed = detailedFaults(of: error)
        if !detailed.isEmpty {
            metadata["coredata_detailed_count"] = detailed.count
            // Capped: a bulk save can fail every row, and a 400-entry list is
            // noise. The count above is the honest total.
            metadata["coredata_detailed"] = detailed.prefix(10).joined(separator: ",")
        }

        return metadata
    }

    /// One line for the unified log, in place of `\(error)`.
    ///
    /// `NSError.description` was being interpolated straight into an os.log
    /// line. That description contains `NSValidationErrorObject`, i.e. the whole
    /// failing row. This replaces it with the same three facts and no row.
    static func summary(for error: NSError) -> String {
        var parts = ["\(error.domain)/\(error.code)", codeName(error.code)]
        if let entity = validationEntity(of: error), let attribute = validationAttribute(of: error) {
            parts.append("\(entity).\(attribute)")
        } else if let attribute = validationAttribute(of: error) {
            parts.append(attribute)
        } else if let entity = validationEntity(of: error) {
            parts.append(entity)
        }
        let detailed = detailedFaults(of: error)
        if !detailed.isEmpty {
            parts.append("detailed=\(detailed.count)[\(detailed.prefix(5).joined(separator: ","))]")
        }
        return parts.joined(separator: " · ")
    }

    /// Attribute or relationship name the model rejected.
    private static func validationAttribute(of error: NSError) -> String? {
        error.userInfo[NSValidationKeyErrorKey] as? String
    }

    /// Entity name of the rejected row.
    ///
    /// Reads ONLY `entity.name` off the managed object. The object itself is
    /// never described, printed or interpolated.
    ///
    /// Falls back to the first nested error: `NSValidationMultipleErrorsError`
    /// (1560) puts nothing but `NSDetailedErrorsKey` at the top level, so
    /// without the fallback every multi-rule failure reports its entity as
    /// "unknown" and they all group together.
    private static func validationEntity(of error: NSError) -> String? {
        if let object = error.userInfo[NSValidationObjectErrorKey] as? NSManagedObject {
            return object.entity.name
        }
        guard let nested = error.userInfo[NSDetailedErrorsKey] as? [NSError] else {
            return nil
        }
        for child in nested {
            if let object = child.userInfo[NSValidationObjectErrorKey] as? NSManagedObject {
                return object.entity.name
            }
        }
        return nil
    }

    /// `Entity.attribute:code` for each error nested under `NSDetailedErrorsKey`.
    private static func detailedFaults(of error: NSError) -> [String] {
        guard let detailed = error.userInfo[NSDetailedErrorsKey] as? [NSError] else {
            return []
        }
        return detailed.map { nested in
            let entity = validationEntity(of: nested) ?? "unknown"
            let attribute = validationAttribute(of: nested) ?? "unknown"
            return "\(entity).\(attribute):\(nested.code)"
        }
    }

    // MARK: - Context shape

    /// The shape of the pending changes on the context that failed to save.
    ///
    /// The reason this matters for HYPERWHISPER-VC: the `viewContext` save path
    /// does NOT roll back on failure, so one rejected row leaves the context
    /// dirty and every later save fails on the same row. A count of pending
    /// objects tells a poisoned context (a growing set that never drains) from
    /// a single bad write, and the entity list says what is stuck.
    ///
    /// Only names and counts are read — never an object's values.
    static func contextShape(_ context: NSManagedObjectContext) -> [String: Any] {
        let inserted = context.insertedObjects
        let updated = context.updatedObjects
        let deleted = context.deletedObjects

        var entityNames = Set<String>()
        for object in inserted.union(updated).union(deleted) {
            entityNames.insert(object.entity.name ?? "unknown")
        }

        return [
            "pending_inserted": inserted.count,
            "pending_updated": updated.count,
            "pending_deleted": deleted.count,
            "pending_entities": entityNames.sorted().joined(separator: ",")
        ]
    }

    // MARK: - Failure streak

    /// How a context has been doing.
    struct Streak {
        /// Consecutive failures on that context, including this one.
        let failureCount: Int
        /// Milliseconds since the first failure of the current run.
        let sinceFirstFailureMs: Int
    }

    /// Streak key for the shared `viewContext`.
    static let viewContextKey = "view_context"

    /// Streak key for the serial background writer context.
    static let writerContextKey = "writer_context"

    /// Consecutive-failure state, keyed by CONTEXT rather than by call site.
    ///
    /// A save that fails once is a bad write. A save that has failed 19 times
    /// over two days is a context nobody can write to any more, and every
    /// transcript that user recorded since the first failure is gone. Those are
    /// different bugs and the report could not tell them apart.
    ///
    /// The key is the context because the fault is a property of the context:
    /// six launch-repair sites and `PersistenceController.save()` all commit the
    /// SAME `viewContext`, so keying on the call site would give each of them a
    /// streak of one and hide the thing worth seeing. The call site still
    /// travels separately, as the `save_site` tag.
    ///
    /// Time is monotonic (`DispatchTime`), not wall-clock: an NTP step or a
    /// timezone change must not make a duration negative.
    ///
    /// `@unchecked Sendable`: all mutable state is behind `lock`, and both save
    /// paths (`viewContext` on the main queue, the serial writer on its own)
    /// reach it.
    private final class StreakStore: @unchecked Sendable {
        private let lock = NSLock()
        private var firstFailureNanos: [String: UInt64] = [:]
        private var failureCount: [String: Int] = [:]

        func recordFailure(key: String, nowNanos: UInt64) -> Streak {
            lock.lock()
            defer { lock.unlock() }
            let start = firstFailureNanos[key] ?? nowNanos
            firstFailureNanos[key] = start
            let count = (failureCount[key] ?? 0) + 1
            failureCount[key] = count
            // Saturating: `nowNanos` is monotonic, so this cannot underflow, but
            // a negative duration in a report is worse than a zero one.
            let elapsedNanos = nowNanos >= start ? nowNanos - start : 0
            return Streak(
                failureCount: count,
                sinceFirstFailureMs: Int(elapsedNanos / 1_000_000)
            )
        }

        func recordSuccess(key: String) {
            lock.lock()
            defer { lock.unlock() }
            firstFailureNanos.removeValue(forKey: key)
            failureCount.removeValue(forKey: key)
        }
    }

    private static let streaks = StreakStore()

    /// Record a failed save on `contextKey` and return the streak it belongs to.
    static func recordFailure(contextKey: String, nowNanos: UInt64 = DispatchTime.now().uptimeNanoseconds) -> Streak {
        streaks.recordFailure(key: contextKey, nowNanos: nowNanos)
    }

    /// Record a save that worked, ending any streak on that context.
    ///
    /// Cheap on purpose — one lock and two dictionary removals. It runs on the
    /// save path, which is already doing file I/O, and never inside an audio
    /// buffer callback.
    static func recordSuccess(contextKey: String) {
        streaks.recordSuccess(key: contextKey)
    }
}
