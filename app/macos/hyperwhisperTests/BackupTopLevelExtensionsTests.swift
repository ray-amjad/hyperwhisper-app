//
//  BackupTopLevelExtensionsTests.swift
//  hyperwhisperTests
//
//  The macOS half of top-level `platformExtensions` foreign-slice retention
//  (issue #288, phase 3c).
//
//  Until this change macOS built the TOP-LEVEL map purely from the Rust core's
//  settings record, which only ever holds `macos` — so a Windows or Linux
//  top-level slice died on a macOS round-trip even though the PER-MODE slices
//  had always survived. `encodeBackupV2` and the v2 import path are `private`,
//  so `@testable import` cannot reach them; the two `nonisolated static` seams
//  asserted here are the same shape `mergeDefaultModelByMode` /
//  `remapDefaultModelByMode` already use to make `BackupDefaultModelByModeTests`
//  possible, and the private call sites are covered by the compile.
//
//  The expectations are driven by the SHARED vectors, so all three heads answer
//  the same rows.
//

import Foundation
import Testing
@testable import HyperWhisper

struct BackupTopLevelExtensionsTests {

    // MARK: - Vectors

    /// The vectors are repo data shared by three stacks, not a bundled app
    /// resource, so read them from the source tree — the same idiom
    /// `CatalogConformanceVectorTests` uses.
    private static func rows() throws -> [[String: Any]] {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/backup-vectors.json")
        let data = try Data(contentsOf: url)
        let root = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        let all = root?["unknownKeyRoundTrip"] as? [[String: Any]] ?? []
        return all.filter {
            ($0["kind"] as? String) == "topLevelPlatformExtensions"
                && (($0["heads"] as? [String]) ?? []).contains("macos")
        }
    }

    private static func jsonValue(_ any: Any) throws -> JSONValue {
        let data = try JSONSerialization.data(withJSONObject: any)
        return try JSONDecoder().decode(JSONValue.self, from: data)
    }

    private static func objectKeys(_ value: JSONValue?) -> [String] {
        (value?.objectValue?.keys).map { Array($0).sorted() } ?? []
    }

    // MARK: - Tests

    @Test("Every shared vector row: macOS keeps the foreign top-level slices and its own slice wins")
    func vectorRowsRoundTrip() throws {
        let rows = try Self.rows()
        #expect(!rows.isEmpty, "no topLevelPlatformExtensions vector row names the macos head")

        for row in rows {
            let label = row["name"] as? String ?? "<unnamed>"
            let imported = try Self.jsonValue(row["imported"] as Any)

            // 1. IMPORT — capture exactly the non-`macos` slices.
            let stored = BackupManager.foreignTopLevelExtensions(from: imported)

            let expectedStored = (row["expectedStoredByHead"] as? [String: Any])?["macos"]
            if expectedStored == nil || expectedStored is NSNull {
                #expect(stored == nil, "\(label): expected nothing to be preserved")
            } else {
                let decoded = try #require(
                    stored.flatMap { $0.data(using: .utf8) }
                        .flatMap { try? JSONDecoder().decode(JSONValue.self, from: $0) },
                    "\(label): the preserved blob did not decode")
                let want = try Self.jsonValue(expectedStored as Any)
                #expect(
                    Self.objectKeys(decoded) == Self.objectKeys(want),
                    "\(label): preserved \(Self.objectKeys(decoded)), expected \(Self.objectKeys(want))")
                #expect(
                    decoded.objectValue?["windows"] != nil || want.objectValue?["windows"] == nil,
                    "\(label): a preserved windows slice went missing")
            }

            // 2. EXPORT — our own `macos` slice, plus everything preserved.
            let own = JSONValue.object(["macos": .object(["settings": .object(["freshlyBuilt": .bool(true)])])])
            let merged = BackupManager.mergingForeignTopLevelExtensions(into: own, stored: stored)

            let wantKeys = ((row["expectedReExportedKeysByHead"] as? [String: Any])?["macos"]
                as? [String] ?? []).sorted()
            #expect(
                Self.objectKeys(merged) == wantKeys,
                "\(label): re-exported \(Self.objectKeys(merged)), expected \(wantKeys)")

            // 3. OUR OWN SLICE ALWAYS WINS over a stale preserved copy — the same
            //    rule the per-mode passthrough already follows.
            #expect(
                merged?.objectValue?["macos"]?.objectValue?["settings"]?
                    .objectValue?["freshlyBuilt"] != nil,
                "\(label): the macos slice must be the freshly built one")
        }
    }

    @Test("A stale preserved `macos` slice can never overwrite the freshly built one")
    func ownSliceIsNeverTakenFromTheStore() throws {
        // A store that should not exist, but a hand-edited defaults value could
        // produce one. `foreignTopLevelExtensions` filters `macos` out on capture;
        // `mergingForeignTopLevelExtensions` filters it out again on re-emit, so
        // neither half alone is load-bearing.
        let stale = #"{"macos":{"settings":{"stale":true}},"windows":{"settings":{}}}"#
        let own = JSONValue.object(["macos": .object(["settings": .object(["fresh": .bool(true)])])])

        let merged = BackupManager.mergingForeignTopLevelExtensions(into: own, stored: stale)

        let macos = merged?.objectValue?["macos"]?.objectValue?["settings"]?.objectValue
        #expect(macos?["fresh"] != nil, "the freshly built macos slice must win")
        #expect(macos?["stale"] == nil, "a stale preserved macos slice must be discarded")
        #expect(merged?.objectValue?["windows"] != nil, "the foreign windows slice must survive")
    }

    @Test("Nothing to preserve stays nil rather than becoming an empty object")
    func emptyStaysNil() throws {
        #expect(BackupManager.foreignTopLevelExtensions(from: nil) == nil)
        #expect(BackupManager.foreignTopLevelExtensions(from: .object([:])) == nil)
        // A file carrying ONLY our own slice has no foreign data in it.
        #expect(BackupManager.foreignTopLevelExtensions(
            from: .object(["macos": .object([:])])) == nil)

        // And an absent store leaves the own map exactly as it was.
        let own = JSONValue.object(["macos": .object([:])])
        #expect(Self.objectKeys(
            BackupManager.mergingForeignTopLevelExtensions(into: own, stored: nil)) == ["macos"])
    }
}
