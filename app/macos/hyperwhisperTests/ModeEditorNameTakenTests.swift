//
//  ModeEditorNameTakenTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1435: Create Mode (and renaming in Edit Mode)
//  accepted a name another mode already used, so Select Mode and Transcribe
//  File listed two identical rows. The editor now refuses it with the Local
//  API's own MODE_NAME_TAKEN rule.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Mode editor refuses a taken name")
struct ModeEditorNameTakenTests {

    private let hyper = ModeNameEntry(id: UUID(), name: "Hyper")
    private let locQwen = ModeNameEntry(id: UUID(), name: "LocQwen")
    private let cafe = ModeNameEntry(id: UUID(), name: "Café")

    private var store: [ModeNameEntry] { [hyper, locQwen, cafe] }

    // MARK: - Create

    @Test func createRefusesAnExistingName() {
        #expect(ModeEditorDefaults.nameIsTaken("LocQwen", editing: nil, existingModes: store))
    }

    @Test func createRefusesADifferentCaseOfAnExistingName() {
        // The shared core's key lowercases, so these are the same name.
        #expect(ModeEditorDefaults.nameIsTaken("locqwen", editing: nil, existingModes: store))
        #expect(ModeEditorDefaults.nameIsTaken("LOCQWEN", editing: nil, existingModes: store))
    }

    @Test func createRefusesAPaddedOrDecomposedCopy() {
        // ModeNamePolicy.normalized runs first: the trim and NFC are macOS's.
        #expect(ModeEditorDefaults.nameIsTaken("  LocQwen \n", editing: nil, existingModes: store))
        #expect(ModeEditorDefaults.nameIsTaken("CAFE\u{301}", editing: nil, existingModes: store))
    }

    @Test func createAllowsAUniqueName() {
        #expect(!ModeEditorDefaults.nameIsTaken("LocQwen 2", editing: nil, existingModes: store))
        #expect(!ModeEditorDefaults.nameIsTaken("Meetings", editing: nil, existingModes: store))
        #expect(!ModeEditorDefaults.nameIsTaken("Meetings", editing: nil, existingModes: []))
    }

    @Test func aBlankNameIsNotReportedAsTaken() {
        // Blank is the editor's other refusal; calling it "taken" would name
        // the wrong problem.
        let blank = ModeNameEntry(id: UUID(), name: "")
        #expect(!ModeEditorDefaults.nameIsTaken("", editing: nil, existingModes: [blank]))
        #expect(!ModeEditorDefaults.nameIsTaken("   ", editing: nil, existingModes: store))
    }

    // MARK: - Edit

    @Test func renameToAnotherModesNameIsRefused() {
        #expect(ModeEditorDefaults.nameIsTaken("Hyper", editing: locQwen, existingModes: store))
        #expect(ModeEditorDefaults.nameIsTaken("hyper", editing: locQwen, existingModes: store))
    }

    @Test func keepingOrRecasingItsOwnNameIsAllowed() {
        #expect(!ModeEditorDefaults.nameIsTaken("LocQwen", editing: locQwen, existingModes: store))
        #expect(!ModeEditorDefaults.nameIsTaken("locqwen", editing: locQwen, existingModes: store))
        #expect(!ModeEditorDefaults.nameIsTaken(" LocQwen ", editing: locQwen, existingModes: store))
    }

    @Test func renameToAUniqueNameIsAllowed() {
        #expect(!ModeEditorDefaults.nameIsTaken("Dictation", editing: locQwen, existingModes: store))
    }

    @Test func anAlreadyDuplicatedModeCanStillSaveUnderItsOwnName() {
        // Two rows an older build wrote with one name: like the Local API
        // PATCH, an unchanged name is not checked, so the mode stays editable.
        let twin = ModeNameEntry(id: UUID(), name: "LocQwen")
        #expect(!ModeEditorDefaults.nameIsTaken("LocQwen", editing: twin, existingModes: store + [twin]))
        // A recased rename is a change, and the other twin still holds it.
        #expect(ModeEditorDefaults.nameIsTaken("LOCQWEN", editing: twin, existingModes: store + [twin]))
    }

    @Test func theRuleIsTheLocalAPIsOwn() {
        // Same answer as the shared core function MODE_NAME_TAKEN calls.
        for candidate in ["LocQwen", "locqwen", "Hyper", "Meetings", "CAFÉ"] {
            let expected = localApiModeNameConflict(
                candidate: ModeNamePolicy.normalized(candidate) ?? "",
                otherNames: store.compactMap { $0.name }
            )
            #expect(ModeEditorDefaults.nameIsTaken(candidate, editing: nil, existingModes: store) == expected)
        }
    }

    // MARK: - Wiring

    private static let editorPath = "app/macos/hyperwhisper/Views/Modes/ModeEditorView.swift"
    private static let modesViewPath = "app/macos/hyperwhisper/Views/Modes/ModesView.swift"

    @Test func theEditorDisablesSaveAndExplains() throws {
        let check = try ProductionSource.slice(
            of: Self.editorPath,
            from: "private var nameIsTaken: Bool {",
            to: "@State private var lastDirectCloudProvider: String?"
        )
        #expect(check.contains("ModeEditorDefaults.nameIsTaken("))
        #expect(check.contains("existingModes: existingModes"))

        let editor = try ProductionSource.code(of: Self.editorPath)
        #expect(editor.contains("Text(localized: \"modes.field.name.taken\")"))
        #expect(editor.contains("guard hasValidDictationLanguage, !nameIsTaken else { return }"))
        #expect(editor.contains(".disabled(normalizedName == nil || nameIsTaken ||"))
    }

    @Test func bothSheetsPassTheLiveModeList() throws {
        let view = try ProductionSource.code(of: Self.modesViewPath)
        #expect(view.components(separatedBy: "existingModes: modeNameEntries").count - 1 == 2)
    }

    @Test func everyLocaleHasTheMessage() throws {
        let root = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(atPath: root.path)
            .filter { $0.hasSuffix(".lproj") }
        #expect(!locales.isEmpty)
        for locale in locales {
            let strings = try ProductionSource.text(
                of: root.appendingPathComponent(locale).appendingPathComponent("Localizable.strings")
            )
            #expect(strings.contains("\"modes.field.name.taken\" = \""), "missing in \(locale)")
        }
    }
}
