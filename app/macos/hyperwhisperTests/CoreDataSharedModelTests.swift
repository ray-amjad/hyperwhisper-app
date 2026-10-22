//
//  CoreDataSharedModelTests.swift
//  hyperwhisperTests
//
//  Issue #1653: macos-ci went red on `main` in PendingRetrySavesTranscriptTests.
//  The writes in those tests saved nothing, and only in the full parallel run.
//
//  Cause: every `PersistenceController` loaded its own copy of the
//  `HyperWhisper` model. With two or more copies alive, `+[Transcript entity]`
//  cannot tell them apart, so `Transcript(context:)` sometimes took another
//  copy's entity. That insert has no store, `obtainPermanentIDs` throws, and
//  `createFailedTranscriptInBackground` returns nil. A CI probe logged it:
//  `inserted=["Transcript same=false temp=true"]`.
//
//  Fix: one model per process (`PersistenceController.managedObjectModel`).
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Every persistence controller shares one Core Data model (#1653)")
struct CoreDataSharedModelTests {

    /// The cause itself: two controllers, one model.
    @Test func twoControllersUseTheSameModel() {
        let first = PersistenceController(inMemory: true)
        let second = PersistenceController(inMemory: true)

        #expect(first.container.managedObjectModel === second.container.managedObjectModel,
                "each controller loaded its own copy of the model again")
    }

    /// With other controllers alive, a generated-subclass insert takes the
    /// entity of its own context's model, so the row reaches a store.
    @MainActor
    @Test func aWriteSavesWhileOtherControllersAreAlive() async throws {
        let others = (0..<4).map { _ in PersistenceController(inMemory: true) }
        let persistence = PersistenceController(inMemory: true)

        #expect(Transcript.entity().managedObjectModel === persistence.container.managedObjectModel)
        for _ in 0..<20 {
            let id = await persistence.createFailedTranscriptInBackground(
                duration: 1,
                mode: "LocNemo",
                audioFilePath: "/tmp/hw-1653-shared-model.wav",
                failedReason: "Audio file could not be read",
                errorText: "Error: Audio file could not be read"
            )
            #expect(id != nil, "the write saved nothing")
        }
        withExtendedLifetime(others) {}
    }
}
