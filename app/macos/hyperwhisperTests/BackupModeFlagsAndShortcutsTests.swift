//
//  BackupModeFlagsAndShortcutsTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1481.
//
//  A macOS backup wrote no `enableScreenOCR` and no `useStreamingTranscription`
//  per mode, and `importModes` passed neither to `createOrUpdateMode`, so a
//  restore turned both OFF on every mode. The `shortcuts` group carried no key
//  combos, so a restore lost every custom shortcut.
//
//  These drive the real export projections (`BackupMode(from:)`,
//  `UniversalModeDTO(from:)`), the real v2 mode reader
//  (`BackupManager.backupMode(fromV2:)`), the real restore
//  (`PersistenceController.importModes`) and the real shared-core settings
//  adapter. The live KeyboardShortcuts store is the one part replaced: the
//  test host IS the running app, so writing its global hotkeys from a test
//  would change them for real. `BackupKeyboardShortcuts.snapshot` and
//  `.assignments` are the seam, and `liveSnapshot` / `applyLive` only wrap
//  them around `KeyboardShortcuts.getShortcut` / `.setShortcut`.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Backup keeps per-mode OCR, streaming and shortcut keys (#1481)")
struct BackupModeFlagsAndShortcutsTests {

    /// The controller is a PARAMETER and the caller holds it for the length of
    /// the test: a store dropped inside a helper deallocates on return and every
    /// attribute then reads back as its zero value — which would make a `false`
    /// expectation pass for the wrong reason (see `DefaultModeInvariantTests`).
    @MainActor
    private func makeLocalMode(
        in persistence: PersistenceController,
        name: String,
        screenOCR: Bool,
        streaming: Bool
    ) -> Mode {
        persistence.createOrUpdateMode(
            id: UUID(),
            name: name,
            preset: "custom",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            useStreamingTranscription: streaming,
            enableScreenOCR: screenOCR
        )
    }

    // MARK: - Per-mode flags

    @MainActor
    @Test func aV1BackupRoundTripKeepsScreenOCRAndStreamingOn() throws {
        let source = PersistenceController(inMemory: true)
        let mode = makeLocalMode(in: source, name: "Notes", screenOCR: true, streaming: true)
        let modeId = try #require(mode.id)

        let data = try JSONEncoder().encode([BackupMode(from: mode)])
        let wire = try #require(try JSONSerialization.jsonObject(with: data) as? [[String: Any]])
        #expect(wire.first?["enableScreenOCR"] as? Bool == true)
        #expect(wire.first?["useStreamingTranscription"] as? Bool == true)

        let decoded = try JSONDecoder().decode([BackupMode].self, from: data)
        let target = PersistenceController(inMemory: true)
        let result = target.importModes(decoded, resolution: .replace)

        #expect(result.imported == 1)
        let restored = try #require(target.fetchAllModes().first { $0.id == modeId })
        #expect(restored.enableScreenOCR)
        #expect(restored.useStreamingTranscription)
    }

    @MainActor
    @Test func aV2BackupRoundTripKeepsScreenOCRAndStreamingOn() throws {
        let source = PersistenceController(inMemory: true)
        let mode = makeLocalMode(in: source, name: "Notes", screenOCR: true, streaming: true)
        let modeId = try #require(mode.id)

        let data = try JSONEncoder().encode(UniversalModeDTO(from: BackupMode(from: mode)))

        // The values ride in the mode's own `macos` slice: the shared `Mode`
        // schema object is `additionalProperties: false` and has no such field.
        let wire = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(wire["enableScreenOCR"] == nil)
        let slices = wire["platformExtensions"] as? [String: Any]
        let macos = slices?["macos"] as? [String: Any]
        #expect(macos?["enableScreenOCR"] as? Bool == true)
        #expect(macos?["useStreamingTranscription"] as? Bool == true)

        let dto = try JSONDecoder().decode(UniversalModeDTO.self, from: data)
        let backup = try #require(BackupManager.backupMode(fromV2: dto))
        let target = PersistenceController(inMemory: true)
        _ = target.importModes([backup], resolution: .replace)

        let restored = try #require(target.fetchAllModes().first { $0.id == modeId })
        #expect(restored.enableScreenOCR)
        #expect(restored.useStreamingTranscription)
    }

    @MainActor
    @Test func aWindowsBackupRestoresScreenOCRFromItsOwnSlice() throws {
        // The shape `shared-backup/examples/windows-export.hwbackup.json` writes.
        let json = """
            {"id":"AA0E8400-E29B-41D4-A716-446655440010","name":"Hyper",
             "platformExtensions":{"windows":{"enableScreenOCR":true,"localEngine":"whisper"}}}
            """
        let dto = try JSONDecoder().decode(UniversalModeDTO.self, from: Data(json.utf8))
        let backup = try #require(BackupManager.backupMode(fromV2: dto))
        #expect(backup.enableScreenOCR == true)
        // Windows has no per-mode streaming flag, so there is nothing to read.
        #expect(backup.useStreamingTranscription == nil)

        let target = PersistenceController(inMemory: true)
        _ = target.importModes([backup], resolution: .replace)
        let restored = try #require(target.fetchAllModes().first { $0.id == backup.id })
        #expect(restored.enableScreenOCR)
        #expect(restored.useStreamingTranscription == false)
    }

    @Test func theMacosSliceWinsOverAStaleWindowsSlice() throws {
        // A mode first restored from Windows keeps the `windows` slice as a
        // preserved foreign copy; macOS's own value is the live one.
        let json = """
            {"id":"AA0E8400-E29B-41D4-A716-446655440010","name":"Hyper",
             "platformExtensions":{"macos":{"enableScreenOCR":false},
                                   "windows":{"enableScreenOCR":true}}}
            """
        let dto = try JSONDecoder().decode(UniversalModeDTO.self, from: Data(json.utf8))
        let backup = try #require(BackupManager.backupMode(fromV2: dto))
        #expect(backup.enableScreenOCR == false)
    }

    @MainActor
    @Test func anOldBackupWithoutTheFieldsKeepsTheLocalValues() throws {
        // The decision for a backup written before #1481: an absent flag keeps
        // the value of the local mode the row replaces, and is `false` (what
        // every restore wrote before) when there is no such mode.
        let target = PersistenceController(inMemory: true)
        let local = makeLocalMode(in: target, name: "Notes", screenOCR: true, streaming: true)
        let localId = try #require(local.id)
        let newId = UUID()

        // v1 mode objects exactly as main wrote them: no flag keys at all.
        let legacy = """
            [{"id":"\(localId.uuidString)","name":"Notes","preset":"custom","language":"en",
              "model":"base","punctuation":true,"capitalization":true,"profanityFilter":false,
              "postProcessingMode":0,"isDefault":false,"sortOrder":0},
             {"id":"\(newId.uuidString)","name":"Brand New","preset":"custom","language":"en",
              "model":"base","punctuation":true,"capitalization":true,"profanityFilter":false,
              "postProcessingMode":0,"isDefault":false,"sortOrder":1}]
            """
        let modes = try JSONDecoder().decode([BackupMode].self, from: Data(legacy.utf8))
        #expect(modes.allSatisfy { $0.enableScreenOCR == nil && $0.useStreamingTranscription == nil })

        let result = target.importModes(modes, resolution: .replace)
        #expect(result.imported == 2)

        let kept = try #require(target.fetchAllModes().first { $0.id == localId })
        #expect(kept.enableScreenOCR)
        #expect(kept.useStreamingTranscription)

        let fresh = try #require(target.fetchAllModes().first { $0.id == newId })
        #expect(fresh.enableScreenOCR == false)
        #expect(fresh.useStreamingTranscription == false)
    }

    // MARK: - Shortcut key combos

    /// Toggle Recording set to ^⇧U, as in the issue's fuzz profile:
    /// kVK_ANSI_U = 32, controlKey (4096) + shiftKey (512) = 4608.
    private static let controlShiftU = BackupShortcutBinding.combo(carbonKeyCode: 32, carbonModifiers: 4608)

    @Test func everyRegisteredShortcutIsExported() {
        #expect(Set(BackupKeyboardShortcuts.registeredNames) == [
            "toggleRecordingWithTranscription", "cancelRecording", "pushToTalk",
            "startStreaming", "changeMode", "quickCapture",
        ])
    }

    @Test func aCustomComboRoundTripsThroughAV1Backup() throws {
        let names = ["toggleRecordingWithTranscription", "cancelRecording", "quickCapture"]
        let live: [String: BackupShortcutBinding] = [
            "toggleRecordingWithTranscription": Self.controlShiftU,
            "cancelRecording": .combo(carbonKeyCode: 53, carbonModifiers: 0),
            "quickCapture": .unassigned,
        ]
        let snapshot = BackupKeyboardShortcuts.snapshot(names: names) { live[$0] ?? .unassigned }
        let exported = BackupShortcutSettings(
            pushToTalkMode: "disabled",
            pushToTalkDoublePressEnabled: false,
            quickCaptureEnabled: nil,
            quickCaptureModeId: nil,
            keyboardShortcuts: snapshot
        )

        let data = try JSONEncoder().encode(exported)
        let wire = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let combos = try #require(wire["keyboardShortcuts"] as? [String: Any])
        let toggle = combos["toggleRecordingWithTranscription"] as? [String: Any]
        #expect(toggle?["carbonKeyCode"] as? Int == 32)
        #expect(toggle?["carbonModifiers"] as? Int == 4608)
        // "No shortcut" is written, as null, so a restore clears a stale combo.
        #expect(combos["quickCapture"] is NSNull)

        let imported = try JSONDecoder().decode(BackupShortcutSettings.self, from: data)
        var restored: [String: BackupShortcutBinding] = [:]
        for assignment in BackupKeyboardShortcuts.assignments(from: imported.keyboardShortcuts, knownNames: names) {
            restored[assignment.name] = assignment.binding
        }
        #expect(restored == live)
    }

    @Test func aCustomComboRoundTripsThroughTheUniversalV2Adapter() throws {
        // The v2 export hands the macOS `shortcuts` category to the shared core,
        // which parks it under platformExtensions.macos.settings.shortcuts; the
        // import asks the core for it back.
        let exported = BackupShortcutSettings(
            pushToTalkMode: "disabled",
            pushToTalkDoublePressEnabled: false,
            quickCaptureEnabled: nil,
            quickCaptureModeId: nil,
            keyboardShortcuts: [
                "toggleRecordingWithTranscription": Self.controlShiftU,
                "quickCapture": .unassigned,
            ]
        )
        let shortcuts = try JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(exported))
        let macosJson = try #require(String(
            data: try JSONEncoder().encode(JSONValue.object(["shortcuts": shortcuts])),
            encoding: .utf8
        ))

        let record = try macosSettingsToUniversalSettingsJson(macosJson: macosJson, existingMacosExtJson: nil)
        let back = try universalSettingsToMacosSettingsJson(recordJson: record)

        let backValue = try JSONDecoder().decode(JSONValue.self, from: Data(back.utf8))
        let shortcutsBack = try #require(backValue.objectValue?["shortcuts"])
        let imported = try JSONDecoder().decode(
            BackupShortcutSettings.self,
            from: JSONEncoder().encode(shortcutsBack)
        )
        #expect(imported.keyboardShortcuts == exported.keyboardShortcuts)
    }

    @Test func anOldShortcutsGroupLeavesEveryShortcutAlone() throws {
        let legacy = """
            {"pushToTalkMode":"disabled","pushToTalkDoublePressEnabled":false,
             "quickCaptureEnabled":false,"quickCaptureModeId":""}
            """
        let imported = try JSONDecoder().decode(BackupShortcutSettings.self, from: Data(legacy.utf8))
        #expect(imported.keyboardShortcuts == nil)
        #expect(BackupKeyboardShortcuts.assignments(
            from: imported.keyboardShortcuts,
            knownNames: BackupKeyboardShortcuts.registeredNames
        ).isEmpty)
    }

    @Test func absentAndUnknownNamesWriteNothing() throws {
        // A name a newer build added is ignored; a name the file leaves out is
        // left alone; a combo without modifiers is a bare key.
        let json = """
            {"pushToTalkMode":"disabled","pushToTalkDoublePressEnabled":false,
             "keyboardShortcuts":{"someFutureAction":{"carbonKeyCode":1,"carbonModifiers":0},
                                  "changeMode":{"carbonKeyCode":40}}}
            """
        let imported = try JSONDecoder().decode(BackupShortcutSettings.self, from: Data(json.utf8))
        let assignments = BackupKeyboardShortcuts.assignments(
            from: imported.keyboardShortcuts,
            knownNames: ["toggleRecordingWithTranscription", "changeMode"]
        )
        #expect(assignments.count == 1)
        #expect(assignments.first?.name == "changeMode")
        #expect(assignments.first?.binding == .combo(carbonKeyCode: 40, carbonModifiers: 0))
    }
}
