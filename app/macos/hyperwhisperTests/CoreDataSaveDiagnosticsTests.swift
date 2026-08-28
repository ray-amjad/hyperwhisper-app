//
//  CoreDataSaveDiagnosticsTests.swift
//  hyperwhisperTests
//
//  Cover for the diagnostics behind Sentry HYPERWHISPER-VC / HYPERWHISPER-VB
//  ("Core Data save failed").
//
//  Two properties are worth a test here, and they pull in opposite directions:
//
//  1. The metadata must be ENOUGH — entity, attribute, code and the nested
//     errors a `1560` hides — or the next report is as undiagnosable as the 19
//     that came before it.
//  2. The metadata must be ONLY that. `NSError.userInfo` on a validation
//     failure also carries `NSValidationErrorValue`, the value the model
//     rejected. On a `Transcript` row that value is the transcript. The
//     privacy test below is the one that must never be deleted.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
struct CoreDataSaveDiagnosticsTests {

    // MARK: - Fixtures

    /// A standalone one-entity model, so the tests do not need the app's own
    /// model or a `PersistenceController` (whose init runs migrations, launch
    /// repairs and default-mode seeding).
    ///
    /// The entity is attached to a model on purpose: `NSManagedObject` requires
    /// its entity to belong to one.
    private static func makeModel(
        entityName: String = "TestRow",
        attributeName: String = "deviceId",
        isOptional: Bool = true
    ) -> NSManagedObjectModel {
        let entity = NSEntityDescription()
        entity.name = entityName
        entity.managedObjectClassName = NSStringFromClass(NSManagedObject.self)

        let attribute = NSAttributeDescription()
        attribute.name = attributeName
        attribute.attributeType = .stringAttributeType
        attribute.isOptional = isOptional
        entity.properties = [attribute]

        let model = NSManagedObjectModel()
        model.entities = [entity]
        return model
    }

    /// A validation error shaped exactly like the one in HYPERWHISPER-VC:
    /// code 1570, `deviceId` missing on a `RecordingSession`.
    private static func makeValidationError(
        code: Int = 1570,
        entityName: String = "RecordingSession",
        attribute: String = "deviceId",
        rejectedValue: String? = nil
    ) -> NSError {
        let model = makeModel(entityName: entityName, attributeName: attribute)
        // `entitiesByName` is force-unwrapped rather than `#require`d so the
        // fixture stays non-throwing; the key is the name just written above.
        let entity = model.entitiesByName[entityName]!
        let object = NSManagedObject(entity: entity, insertInto: nil)
        // The rejected value goes ON THE ROW as well as into `userInfo`. That is
        // what production looks like, and it is what makes the privacy test
        // below able to fail: without it, an implementation that interpolated
        // the whole managed object would still leak nothing, because the object
        // would have nothing in it.
        if let rejectedValue {
            object.setValue(rejectedValue, forKey: attribute)
        }

        var userInfo: [String: Any] = [
            NSValidationKeyErrorKey: attribute,
            NSValidationObjectErrorKey: object,
            NSLocalizedDescriptionKey: "\(attribute) is a required value."
        ]
        if let rejectedValue {
            userInfo[NSValidationValueErrorKey] = rejectedValue
        }

        return NSError(domain: NSCocoaErrorDomain, code: code, userInfo: userInfo)
    }

    // MARK: - Error codes

    @Test func mapsTheCoreDataCodesASaveActuallyFailsWith() {
        #expect(CoreDataSaveDiagnostics.codeName(1570) == "missing_mandatory_property")
        #expect(CoreDataSaveDiagnostics.codeName(1560) == "multiple_validation_errors")
        #expect(CoreDataSaveDiagnostics.codeName(1550) == "managed_object_validation")
        #expect(CoreDataSaveDiagnostics.codeName(133020) == "merge")
        #expect(CoreDataSaveDiagnostics.codeName(134050) == "store_save_conflicts")
    }

    /// An unmapped code still reports the number. The slug is a convenience; the
    /// `coredata_code` field is the fact, so no fault can hide behind a missing
    /// table entry.
    @Test func fallsBackToTheRawCodeForAnythingUnmapped() {
        #expect(CoreDataSaveDiagnostics.codeName(999_999) == "cocoa_999999")
    }

    // MARK: - What the next report will carry

    @Test func namesTheEntityTheAttributeAndTheCode() {
        let metadata = CoreDataSaveDiagnostics.metadata(for: Self.makeValidationError())

        #expect(metadata["coredata_domain"] as? String == NSCocoaErrorDomain)
        #expect(metadata["coredata_code"] as? Int == 1570)
        #expect(metadata["coredata_error"] as? String == "missing_mandatory_property")
        #expect(metadata["coredata_entity"] as? String == "RecordingSession")
        #expect(metadata["coredata_attribute"] as? String == "deviceId")
    }

    /// Code 1560 says only "several rules broke". Which rules is in
    /// `NSDetailedErrorsKey`, which nothing read before.
    @Test func unpacksTheNestedErrorsHiddenUnderAMultipleValidationFailure() {
        let nested = [
            Self.makeValidationError(entityName: "RecordingSession", attribute: "deviceId"),
            Self.makeValidationError(code: 1670, entityName: "Transcript", attribute: "status")
        ]
        let error = NSError(
            domain: NSCocoaErrorDomain,
            code: 1560,
            userInfo: [NSDetailedErrorsKey: nested]
        )

        let metadata = CoreDataSaveDiagnostics.metadata(for: error)
        #expect(metadata["coredata_error"] as? String == "multiple_validation_errors")
        #expect(metadata["coredata_detailed_count"] as? Int == 2)

        let detailed = metadata["coredata_detailed"] as? String ?? ""
        #expect(detailed.contains("RecordingSession.deviceId:1570"))
        #expect(detailed.contains("Transcript.status:1670"))
    }

    // MARK: - Privacy

    /// The rejected VALUE is the user's data. On the `Transcript` entity it is
    /// the transcript itself, and `NSError.description` — which is what the log
    /// line used to interpolate — inlines it.
    @Test func neverReportsTheValueTheModelRejected() {
        let secret = "the quick brown fox jumped over the lazy dog"
        let error = Self.makeValidationError(
            entityName: "Transcript",
            attribute: "text",
            rejectedValue: secret
        )

        let summary = CoreDataSaveDiagnostics.summary(for: error)
        #expect(!summary.contains(secret))
        #expect(summary.contains("Transcript.text"))

        let metadata = CoreDataSaveDiagnostics.metadata(for: error)
        for (_, value) in metadata {
            #expect(!"\(value)".contains(secret))
        }
    }

    /// `localizedDescription` is free text Cocoa interpolates values into, so
    /// the summary is built from codes and names and never from it.
    @Test func summaryCarriesTheFaultNotTheDescription() {
        let error = Self.makeValidationError()
        let summary = CoreDataSaveDiagnostics.summary(for: error)

        #expect(summary.contains("1570"))
        #expect(summary.contains("missing_mandatory_property"))
        #expect(summary.contains("RecordingSession.deviceId"))
        #expect(!summary.contains("is a required value"))
    }

    // MARK: - Context shape

    @Test func countsPendingChangesByEntityWithoutReadingTheirValues() throws {
        let model = Self.makeModel()

        // A fresh file per run, so parallel tests never share a store.
        let storeURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("core-data-diagnostics-\(UUID().uuidString).sqlite")
        defer {
            for suffix in ["", "-wal", "-shm"] {
                try? FileManager.default.removeItem(
                    at: URL(fileURLWithPath: storeURL.path + suffix)
                )
            }
        }

        let coordinator = NSPersistentStoreCoordinator(managedObjectModel: model)
        try coordinator.addPersistentStore(
            ofType: NSSQLiteStoreType,
            configurationName: nil,
            at: storeURL,
            options: nil
        )

        let context = NSManagedObjectContext(concurrencyType: .mainQueueConcurrencyType)
        context.persistentStoreCoordinator = coordinator

        let entity = try #require(model.entitiesByName["TestRow"])
        for _ in 0..<3 {
            let row = NSManagedObject(entity: entity, insertInto: context)
            row.setValue("device-uid", forKey: "deviceId")
        }

        let shape = CoreDataSaveDiagnostics.contextShape(context)
        #expect(shape["pending_inserted"] as? Int == 3)
        #expect(shape["pending_updated"] as? Int == 0)
        #expect(shape["pending_deleted"] as? Int == 0)
        #expect(shape["pending_entities"] as? String == "TestRow")
    }

    /// THE LOAD-BEARING ASSUMPTION. `PersistenceController.save()` reads the
    /// context shape inside its `catch`, which is only worth anything if a save
    /// that throws leaves its pending changes in place. That is also the reason
    /// the view context stays poisoned after one bad row — the whole reading of
    /// HYPERWHISPER-VC rests on it. So exercise it against real Core Data rather
    /// than asserting it in a comment.
    @Test func aSaveThatThrowsKeepsItsPendingChanges() throws {
        let model = Self.makeModel(isOptional: false)

        let storeURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("core-data-diagnostics-\(UUID().uuidString).sqlite")
        defer {
            for suffix in ["", "-wal", "-shm"] {
                try? FileManager.default.removeItem(
                    at: URL(fileURLWithPath: storeURL.path + suffix)
                )
            }
        }

        let coordinator = NSPersistentStoreCoordinator(managedObjectModel: model)
        try coordinator.addPersistentStore(
            ofType: NSSQLiteStoreType,
            configurationName: nil,
            at: storeURL,
            options: nil
        )

        let context = NSManagedObjectContext(concurrencyType: .mainQueueConcurrencyType)
        context.persistentStoreCoordinator = coordinator

        let entity = try #require(model.entitiesByName["TestRow"])
        // `deviceId` is mandatory in this model and is left unset — the exact
        // shape of the production failure.
        _ = NSManagedObject(entity: entity, insertInto: context)

        var thrown: NSError?
        do {
            try context.save()
        } catch {
            thrown = error as NSError
        }

        let error = try #require(thrown)
        let metadata = CoreDataSaveDiagnostics.metadata(for: error)
        #expect(metadata["coredata_entity"] as? String == "TestRow")
        #expect(metadata["coredata_attribute"] as? String == "deviceId")
        #expect(metadata["coredata_code"] as? Int == 1570)

        // The point of the test: the row is still pending after the throw.
        let shape = CoreDataSaveDiagnostics.contextShape(context)
        #expect(shape["pending_inserted"] as? Int == 1)
        #expect(shape["pending_entities"] as? String == "TestRow")
    }

    // MARK: - Failure streak

    /// 19 events four hours apart from one process is not 19 bad writes, it is
    /// one context that stopped accepting writes. Only a streak says so.
    ///
    /// Times are monotonic nanoseconds, injected: a wall-clock source would let
    /// an NTP step report a negative duration.
    @Test func countsConsecutiveFailuresAndTheAgeOfTheStreak() {
        let key = "unit_test_streak_counts"
        let start: UInt64 = 1_000_000_000

        let first = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start)
        #expect(first.failureCount == 1)
        #expect(first.sinceFirstFailureMs == 0)

        let fourHours: UInt64 = 4 * 3600 * 1_000_000_000
        let second = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start + fourHours)
        #expect(second.failureCount == 2)
        #expect(second.sinceFirstFailureMs == 14_400_000)

        CoreDataSaveDiagnostics.recordSuccess(contextKey: key)
    }

    @Test func aSaveThatWorksEndsTheStreak() {
        let key = "unit_test_streak_resets"
        let start: UInt64 = 2_000_000_000

        _ = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start)
        _ = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start + 60_000_000_000)
        CoreDataSaveDiagnostics.recordSuccess(contextKey: key)

        let afterReset = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start + 120_000_000_000)
        #expect(afterReset.failureCount == 1)
        #expect(afterReset.sinceFirstFailureMs == 0)

        CoreDataSaveDiagnostics.recordSuccess(contextKey: key)
    }

    /// Contexts are independent — a poisoned view context must not make the
    /// background writer look poisoned too.
    @Test func keepsOneContextStreakOutOfAnother() {
        let keyA = "unit_test_streak_context_a"
        let keyB = "unit_test_streak_context_b"
        let start: UInt64 = 3_000_000_000

        _ = CoreDataSaveDiagnostics.recordFailure(contextKey: keyA, nowNanos: start)
        _ = CoreDataSaveDiagnostics.recordFailure(contextKey: keyA, nowNanos: start)
        let b = CoreDataSaveDiagnostics.recordFailure(contextKey: keyB, nowNanos: start)

        #expect(b.failureCount == 1)

        CoreDataSaveDiagnostics.recordSuccess(contextKey: keyA)
        CoreDataSaveDiagnostics.recordSuccess(contextKey: keyB)
    }

    /// A clock that steps backwards must not produce a negative age.
    @Test func neverReportsANegativeStreakAge() {
        let key = "unit_test_streak_monotonic"
        let start: UInt64 = 5_000_000_000

        _ = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start)
        let backwards = CoreDataSaveDiagnostics.recordFailure(contextKey: key, nowNanos: start - 1_000_000_000)
        #expect(backwards.sinceFirstFailureMs == 0)

        CoreDataSaveDiagnostics.recordSuccess(contextKey: key)
    }
}
