//
//  HistoryRowIdentityTests.swift
//  hyperwhisperTests
//
//  #1459: deleting a History row also hid the NEXT row and drew the last row
//  twice. The cause was the row ForEach's identity: the bare NSManagedObjectID
//  (the same class the selection `.tag` uses). After an in-place removal the
//  sidebar List re-bound the surviving row views off by one. A value-type
//  identity (`HistoryRowID`) renders correctly.
//
//  The rendering fault itself only shows in a real window with the real
//  HistoryView and a seeded store, so it was measured on a Mac and is not run
//  here. What these tests pin is the part that can be checked without a
//  window: that `HistoryRowID` keeps the old identity semantics (same row ->
//  equal, different rows -> distinct, stable across a re-fetch in another
//  context), and that the History list still keys its row ForEach off it.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
struct HistoryRowIdentityTests {

    private static let historyView = "app/macos/hyperwhisper/Views/HistoryView.swift"

    private func savedTranscripts(_ count: Int) throws -> (PersistenceController, [NSManagedObjectID]) {
        let controller = PersistenceController(inMemory: true)
        let context = controller.container.viewContext
        var transcripts: [Transcript] = []
        for index in 0..<count {
            let transcript = Transcript(context: context)
            transcript.id = UUID()
            transcript.date = Date().addingTimeInterval(TimeInterval(-index * 60))
            transcript.text = "row \(index)"
            transcripts.append(transcript)
        }
        try context.save()
        return (controller, transcripts.map(\.objectID))
    }

    @Test func theSameRowGivesAnEqualIdentity() throws {
        let (_, ids) = try savedTranscripts(1)
        let first = HistoryRowID(objectID: ids[0])
        let second = HistoryRowID(objectID: ids[0])
        #expect(first == second)
        #expect(first.hashValue == second.hashValue)
    }

    @Test func differentRowsGiveDistinctIdentities() throws {
        let (_, ids) = try savedTranscripts(8)
        let rowIDs = Set(ids.map(HistoryRowID.init(objectID:)))
        #expect(rowIDs.count == ids.count)
    }

    /// The list is rebuilt from snapshots the background loader fetches in its
    /// own context, so a row's identity must survive a re-fetch.
    @Test func aRowKeepsItsIdentityAcrossAReFetchInAnotherContext() async throws {
        let (controller, ids) = try savedTranscripts(3)
        let background = controller.container.newBackgroundContext()
        let refetched: [NSManagedObjectID] = try await background.perform {
            let request = NSFetchRequest<Transcript>(entityName: "Transcript")
            request.sortDescriptors = [NSSortDescriptor(key: "date", ascending: false)]
            return try background.fetch(request).map(\.objectID)
        }
        #expect(refetched.map(HistoryRowID.init(objectID:)) == ids.map(HistoryRowID.init(objectID:)))
    }

    /// After one row is deleted, every surviving row keeps the identity it had,
    /// so the diff is a single removal (the shape the List got wrong when the
    /// identity was the bare class ID).
    @Test func deletingOneRowLeavesTheOthersIdentitiesUnchanged() throws {
        let (controller, ids) = try savedTranscripts(8)
        let context = controller.container.viewContext
        let before = ids.map(HistoryRowID.init(objectID:))

        context.delete(try context.existingObject(with: ids[1]))
        try context.save()

        let request = NSFetchRequest<Transcript>(entityName: "Transcript")
        request.sortDescriptors = [NSSortDescriptor(key: "date", ascending: false)]
        let after = try context.fetch(request).map { HistoryRowID(objectID: $0.objectID) }

        #expect(after == before.enumerated().filter { $0.offset != 1 }.map(\.element))
        let diff = after.difference(from: before)
        #expect(diff.removals.count == 1)
        #expect(diff.insertions.isEmpty)
    }

    /// Source guard: the snapshot's identity is the value type, and the row
    /// ForEach keys off it. Switching back to the bare NSManagedObjectID brings
    /// #1459 back, and no unit test can see that rendering fault.
    @Test func theHistoryListKeysRowsOffTheValueTypeIdentity() throws {
        let snapshot = try ProductionSource.slice(
            of: Self.historyView,
            from: "private struct HistoryItemSnapshot",
            to: "struct HistoryRowID"
        )
        #expect(snapshot.contains("var id: HistoryRowID"), "\(snapshot)")
        #expect(!snapshot.contains("var id: NSManagedObjectID"), "\(snapshot)")

        let rowType = try ProductionSource.slice(
            of: Self.historyView,
            from: "struct HistoryRowID",
            to: "}"
        )
        #expect(rowType.contains(": Hashable"), "\(rowType)")

        let list = try ProductionSource.slice(
            of: Self.historyView,
            from: "List(selection: $selectedTranscriptIDs)",
            to: ".listStyle(.sidebar)"
        )
        // The row ForEach may use the Identifiable overload or an explicit
        // key path; either way it must not key by the bare objectID, in any
        // spelling (`id: \.objectID`, `id:\.objectID`,
        // `id: \HistoryItemSnapshot.objectID`).
        #expect(list.contains("ForEach(section.items"), "\(list)")
        let compact = list.filter { !$0.isWhitespace }
        #expect(!compact.contains("id:\\.objectID"), "\(list)")
        #expect(!compact.contains("id:\\HistoryItemSnapshot.objectID"), "\(list)")
        #expect(list.contains(".tag(item.objectID)"), "selection must stay keyed by objectID: \(list)")
    }
}
