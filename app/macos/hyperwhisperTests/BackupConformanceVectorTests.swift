//
//  BackupConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/backup-vectors.json` against the Swift UniFFI
//  binding. Issue #277 moved the universal-v2 settings mapping and the mode
//  normalization into `hw-backup`, so there is exactly one implementation;
//  these vectors prove macOS reads that one implementation's answer unchanged.
//  Rust and C# run the same file:
//
//    shared-core-rs/crates/hw-core/tests/backup_vectors.rs
//    app/shared-dotnet/HyperWhisper.Backup.Application.Tests/Program.cs
//    app/windows/HyperWhisper.SmokeTests/Program.cs
//
//  The `unknownKeyRoundTrip` rows are a native STORE behaviour rather than a
//  core answer; the macOS half of them lives in `BackupTopLevelExtensionsTests`.
//
//  Two scopes, deliberately labelled apart:
//
//  - `macosSettings` is macOS's OWN adapter. `BackupManager` calls exactly these
//    two functions (`macosSettingsToUniversalSettingsJson` on export,
//    `universalSettingsToMacosSettingsJson` on import), so a row that fails here
//    is a change to what this app writes to and reads from a backup file.
//  - `modeNormalization` is NOT on a macOS code path — it is the entry point the
//    Windows and Linux mode importers share. Running it here is a BINDING check:
//    `binding-drift.yml` proves the vendored Swift binding matches its source
//    byte for byte, and nothing else proves the static library behind it still
//    answers the same. The 132 rows are frozen, so any movement is a real one.
//

import Foundation
import Testing
@testable import HyperWhisper

struct BackupConformanceVectorTests {

    // MARK: - Vectors

    /// The vectors are repo data shared by three stacks, not a bundled app
    /// resource, so read them from the source tree — the same idiom
    /// `CatalogConformanceVectorTests` uses.
    private static func document() throws -> [String: Any] {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/backup-vectors.json")
        let data = try Data(contentsOf: url)
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw VectorError("backup-vectors.json is not a JSON object")
        }
        return root
    }

    private static func rows(_ group: String) throws -> [[String: Any]] {
        guard let rows = try document()[group] as? [[String: Any]] else {
            throw VectorError("backup-vectors.json has no `\(group)` rows")
        }
        guard !rows.isEmpty else { throw VectorError("`\(group)` has no rows") }
        return rows
    }

    private struct VectorError: Error, CustomStringConvertible {
        let description: String
        init(_ description: String) { self.description = description }
    }

    // MARK: - JSON helpers

    /// Serialize a vector fragment back to a JSON string for the FFI boundary.
    private static func jsonString(_ any: Any) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: any)
        return String(decoding: data, as: UTF8.self)
    }

    /// Decode any JSON — a vector expectation or an FFI result — into the app's
    /// own `JSONValue`. Numbers land as `Double` on both sides, so a vector that
    /// writes `10` and a core that emits `10.0` compare equal: the rows pin
    /// values, never JSON formatting. Bools stay a separate case, so a bool
    /// that became a number is still caught. This is the same altitude .NET's
    /// `JsonNode.DeepEquals` reads the rows at.
    private static func value(_ any: Any) throws -> JSONValue {
        let data = try JSONSerialization.data(withJSONObject: any)
        return try JSONDecoder().decode(JSONValue.self, from: data)
    }

    private static func value(json: String) throws -> JSONValue {
        try JSONDecoder().decode(JSONValue.self, from: Data(json.utf8))
    }

    /// Structural equality over `JSONValue`. Objects compare as maps, so key
    /// order never matters. Written here rather than as an `Equatable`
    /// conformance so the shipping model type is untouched by a test.
    private static func equal(_ lhs: JSONValue, _ rhs: JSONValue) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): return true
        case (.bool(let a), .bool(let b)): return a == b
        case (.number(let a), .number(let b)): return a == b
        case (.string(let a), .string(let b)): return a == b
        case (.array(let a), .array(let b)):
            return a.count == b.count && zip(a, b).allSatisfy { equal($0, $1) }
        case (.object(let a), .object(let b)):
            guard a.count == b.count else { return false }
            return a.allSatisfy { key, value in
                guard let other = b[key] else { return false }
                return equal(value, other)
            }
        default: return false
        }
    }

    private static func render(_ value: JSONValue) -> String {
        guard let data = try? JSONEncoder().encode(value) else { return "<unencodable>" }
        return String(decoding: data, as: UTF8.self)
    }

    private static func expectJSON(
        _ label: String, _ what: String, expected: JSONValue, actual: JSONValue
    ) {
        #expect(
            equal(expected, actual),
            """
            vector '\(label)': \(what) mismatch
              expected \(render(expected))
              actual   \(render(actual))
            """
        )
    }

    private static func name(_ row: [String: Any]) -> String {
        row["name"] as? String ?? "<unnamed>"
    }

    // MARK: - macosSettings

    @Test("macosSettings rows: the adapter BackupManager ships answers the shared vectors")
    func macosSettingsRowsMatchTheSharedCore() throws {
        let rows = try Self.rows("macosSettings")

        for row in rows {
            let label = Self.name(row)
            let direction = row["direction"] as? String

            switch direction {
            case "toUniversal":
                let macos = try #require(row["macos"], "\(label): missing `macos`")
                // A row may carry an existing `platformExtensions.macos` blob to
                // fold into; absent means the export had nothing to preserve.
                let existing = row["existingMacosExtension"].flatMap { blob -> String? in
                    blob is NSNull ? nil : try? Self.jsonString(blob)
                }
                let produced = try macosSettingsToUniversalSettingsJson(
                    macosJson: Self.jsonString(macos), existingMacosExtJson: existing)
                let expected = try #require(
                    row["expectedUniversal"], "\(label): missing `expectedUniversal`")
                Self.expectJSON(
                    label, "universal settings record",
                    expected: try Self.value(expected), actual: try Self.value(json: produced))

            case "toMacos":
                let universal = try #require(row["universal"], "\(label): missing `universal`")
                let produced = try universalSettingsToMacosSettingsJson(
                    recordJson: Self.jsonString(universal))
                let expected = try #require(
                    row["expectedMacos"], "\(label): missing `expectedMacos`")
                Self.expectJSON(
                    label, "macOS 7-category settings",
                    expected: try Self.value(expected), actual: try Self.value(json: produced))

            default:
                Issue.record("vector '\(label)': unknown direction '\(direction ?? "nil")'")
            }
        }
    }

    // MARK: - modeNormalization

    /// The five cloud-routing fields a `modeNormalization` row pins, each with
    /// the CALLER's default for an absent field.
    ///
    /// `normalizeUniversalModeJson` leaves an absent field absent on purpose —
    /// the entity default belongs to the head, not to the core. The vectors
    /// record the post-default answer (Windows `Mode`'s field initialisers, and
    /// the matching Linux entity), so this table is how the core's output is
    /// read at that same altitude. The Rust suite carries the identical table;
    /// a change is a change to what the apps ship and must be made in both.
    private static let modeFields: [(key: String, callerDefault: String?)] = [
        ("cloudProvider", nil),
        ("cloudTranscriptionModel", nil),
        ("cloudTranscriptionDomain", nil),
        ("cloudAccuracyTier", "elevenLabsScribeV2"),
        ("cloudPostProcessingModel", "anthropic:claude-haiku-4-5"),
    ]

    @Test("modeNormalization rows: the Swift binding answers what Rust and C# answer")
    func modeNormalizationRowsMatchTheSharedCore() throws {
        let rows = try Self.rows("modeNormalization")
        #expect(rows.count == 132, "the frozen modeNormalization row count changed")

        for row in rows {
            let label = Self.name(row)
            let mode = try #require(row["mode"], "\(label): missing `mode`")

            // Phase 1b collapsed every recorded Windows/Linux drift row to a
            // single `expected`, because after the port there is one answer.
            #expect(
                row["expectedWindows"] == nil && row["expectedLinux"] == nil,
                """
                vector '\(label)' carries a per-head expectation; the heads share one \
                normalizer since phase 1b, so a row must pin a single `expected`
                """)
            let expected = try #require(
                row["expected"] as? [String: Any], "\(label): missing `expected`")

            let produced = try normalizeUniversalModeJson(json: Self.jsonString(mode))
            let normalized = try #require(
                Self.value(json: produced).objectValue,
                "\(label): the core returned a non-object")

            for (key, callerDefault) in Self.modeFields {
                #expect(
                    expected.keys.contains(key),
                    "vector '\(label)' is missing the expected field '\(key)'")
                let want = expected[key] as? String
                // Absent OR explicitly null both mean "the core said nothing",
                // so the caller's own default is what the mode ends up carrying.
                let got: String?
                switch normalized[key] {
                case .some(.string(let text)): got = text
                case .some(.null), .none: got = callerDefault
                case .some(let other):
                    Issue.record(
                        "vector '\(label)': \(key) came back as \(Self.render(other)), not a string")
                    continue
                }
                #expect(
                    want == got,
                    "vector '\(label)': \(key) expected \(want ?? "nil"), got \(got ?? "nil")")
            }

            // Every key the row did not pin must survive untouched. The
            // normalizer canonicalises five fields and is not allowed to drop,
            // rename or invent anything else.
            let modeObject = try #require(
                Self.value(mode).objectValue, "\(label): `mode` must be an object")
            for (key, value) in modeObject where !Self.modeFields.contains(where: { $0.key == key })
            {
                let survived = try #require(
                    normalized[key],
                    "vector '\(label)': the untouched key '\(key)' did not survive normalization")
                #expect(
                    Self.equal(value, survived),
                    "vector '\(label)': the untouched key '\(key)' was rewritten")
            }
        }
    }

    // MARK: - Coverage guard

    /// The vectors are only a contract while they have rows in them, and the
    /// macOS suite must keep running the group that names the macos head. An
    /// empty group would make every test above pass by doing nothing.
    @Test("Every vector group macOS reads is populated and names its heads")
    func vectorGroupsArePopulated() throws {
        for group in [
            "modeNormalization", "windowsSettings", "linuxSettings", "macosSettings",
            "unknownKeyRoundTrip",
        ] {
            let rows = try Self.rows(group)
            for row in rows {
                #expect(
                    !(row["name"] as? String ?? "").isEmpty,
                    "a `\(group)` row has an empty `name`; the name is what a failure quotes")
            }
        }

        // `BackupTopLevelExtensionsTests` runs the macos rows of this group.
        // If none named macos, that suite would silently assert nothing.
        let macosOwned = try Self.rows("unknownKeyRoundTrip").filter {
            (($0["heads"] as? [String]) ?? []).contains("macos")
        }
        #expect(!macosOwned.isEmpty, "no unknownKeyRoundTrip row names the macos head")
    }
}
