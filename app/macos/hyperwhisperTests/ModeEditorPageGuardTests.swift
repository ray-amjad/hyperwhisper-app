//
//  ModeEditorPageGuardTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1525.
//
//  The Edit and Create Mode sheets belong to `ModesView`, and
//  `MainAppView.contentView` switches on `selectedNavigationItem`. So a page
//  change made while a sheet was open (Transcribe File ending, menu bar
//  History… / Settings…, the error toast's Settings action) dropped the sheet
//  and the unsaved edit with no prompt.
//
//  Ray's rule (inbox ask #332): an AUTOMATIC jump keeps the sheet and the page;
//  a USER-CHOSEN jump asks "Discard unsaved changes?" when the editor has
//  unsaved changes and goes only on Discard; a clean editor closes as before.
//
//  The rule is a pure static on AppState and is tested by calling it, and the
//  register / stage / discard steps run on a bare AppState. The callers are
//  view code and a flow that needs a live pipeline, so they are pinned by
//  reading the source, as `ModelLibraryRemoveConfirmationTests` does.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Mode editor keeps its sheet on a page change (#1525)")
struct ModeEditorPageGuardTests {

    // MARK: - The rule

    @Test func withNoEditorOpenEveryTriggerNavigates() {
        for trigger in [NavigationTrigger.automatic, .userChosen, .fromModeEditor] {
            for unsaved in [false, true] {
                #expect(AppState.modeEditorNavigationDecision(
                    editorOpen: false, hasUnsavedChanges: unsaved, trigger: trigger
                ) == .navigate)
            }
        }
    }

    @Test func anAutomaticJumpKeepsAnOpenEditorCleanOrNot() {
        for unsaved in [false, true] {
            #expect(AppState.modeEditorNavigationDecision(
                editorOpen: true, hasUnsavedChanges: unsaved, trigger: .automatic
            ) == .keepEditor)
        }
    }

    @Test func aUserChosenJumpAsksOnlyWhenTheEditorHasUnsavedChanges() {
        #expect(AppState.modeEditorNavigationDecision(
            editorOpen: true, hasUnsavedChanges: true, trigger: .userChosen
        ) == .askToDiscard)
        #expect(AppState.modeEditorNavigationDecision(
            editorOpen: true, hasUnsavedChanges: false, trigger: .userChosen
        ) == .navigate)
    }

    @Test func theEditorsOwnLinkAlwaysGoes() {
        for unsaved in [false, true] {
            #expect(AppState.modeEditorNavigationDecision(
                editorOpen: true, hasUnsavedChanges: unsaved, trigger: .fromModeEditor
            ) == .navigate)
        }
    }

    // MARK: - AppState steps

    @MainActor
    private func appStateOnModesWithEditor(unsaved: Bool) -> (AppState, UUID) {
        let appState = AppState()
        appState.selectedNavigationItem = .modes
        let session = UUID()
        appState.modeEditorDidOpen(session: session)
        appState.modeEditor(session: session, hasUnsavedChanges: unsaved)
        return (appState, session)
    }

    @MainActor
    @Test func anAutomaticJumpLeavesThePageAndStagesNothing() {
        let (appState, _) = appStateOnModesWithEditor(unsaved: true)
        #expect(appState.requestNavigation(to: .history, trigger: .automatic) == false)
        #expect(appState.selectedNavigationItem == .modes)
        #expect(appState.pendingModeEditorNavigation == nil)
    }

    @MainActor
    @Test func navigateToIsAutomaticByDefault() {
        let (appState, _) = appStateOnModesWithEditor(unsaved: false)
        #expect(appState.navigate(to: .settings) == false)
        appState.navigateToSettings(section: "shortcuts")
        appState.navigateToModelLibraryAPIKeys()
        #expect(appState.selectedNavigationItem == .modes)
        // A kept page must not leave the API-keys flag set for the next visit.
        #expect(appState.shouldOpenModelLibraryAPIKeys == false)
        #expect(appState.pendingModeEditorNavigation == nil)
    }

    @MainActor
    @Test func aUserChosenJumpWithUnsavedChangesWaitsForDiscard() {
        let (appState, _) = appStateOnModesWithEditor(unsaved: true)
        #expect(appState.requestNavigation(to: .settings, trigger: .userChosen) == false)
        #expect(appState.selectedNavigationItem == .modes)
        #expect(appState.pendingModeEditorNavigation == .settings)

        appState.discardModeEditorAndNavigate()
        #expect(appState.selectedNavigationItem == .settings)
        #expect(appState.pendingModeEditorNavigation == nil)
    }

    @MainActor
    @Test func keepEditingForgetsTheHeldPage() {
        let (appState, _) = appStateOnModesWithEditor(unsaved: true)
        appState.requestNavigation(to: .history, trigger: .userChosen)
        appState.keepEditingModeEditor()
        #expect(appState.pendingModeEditorNavigation == nil)
        #expect(appState.selectedNavigationItem == .modes)
        // A Discard with nothing held does nothing.
        appState.discardModeEditorAndNavigate()
        #expect(appState.selectedNavigationItem == .modes)
    }

    @MainActor
    @Test func aUserChosenJumpWithACleanEditorGoesAtOnce() {
        let (appState, _) = appStateOnModesWithEditor(unsaved: false)
        #expect(appState.requestNavigation(to: .history, trigger: .userChosen))
        #expect(appState.selectedNavigationItem == .history)
        #expect(appState.pendingModeEditorNavigation == nil)
    }

    @MainActor
    @Test func aClosedEditorNoLongerHoldsThePage() {
        let (appState, session) = appStateOnModesWithEditor(unsaved: true)
        appState.modeEditorDidClose(session: session)
        #expect(appState.isModeEditorOpen == false)
        #expect(appState.modeEditorHasUnsavedChanges == false)
        #expect(appState.requestNavigation(to: .history, trigger: .automatic))
        #expect(appState.selectedNavigationItem == .history)
    }

    @MainActor
    @Test func aStaleCloseDoesNotUnregisterANewerEditor() {
        let (appState, oldSession) = appStateOnModesWithEditor(unsaved: false)
        let newSession = UUID()
        appState.modeEditorDidOpen(session: newSession)
        appState.modeEditorDidClose(session: oldSession)
        appState.modeEditor(session: oldSession, hasUnsavedChanges: true)
        #expect(appState.openModeEditorSession == newSession)
        #expect(appState.modeEditorHasUnsavedChanges == false)
        #expect(appState.requestNavigation(to: .history, trigger: .automatic) == false)
    }

    // MARK: - Unsaved-changes snapshot

    private func snapshot(name: String = "Hyper") -> ModeEditorSnapshot {
        ModeEditorSnapshot(
            name: name,
            preset: "hyper",
            language: "auto",
            model: "base",
            provider: .local,
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            customInstructions: "",
            languageModel: "gpt-5.6-luna",
            postProcessingMode: .off,
            postProcessingProvider: "hyperwhisper",
            cloudProvider: "hyperwhisper",
            cloudAccuracyTier: "elevenlabs-scribe-v2",
            cloudPostProcessingModel: "claude-haiku",
            cloudTranscriptionModel: "scribe_v2",
            cloudTranscriptionDomain: nil,
            englishSpelling: .defaultForCurrentRegion,
            userSystemPrompt: "",
            removeTrailingPeriod: false,
            enableScreenOCR: false,
            geminiCustomPrompt: ""
        )
    }

    @Test func anUnsettledEditorHasNoUnsavedChanges() {
        #expect(ModeEditorSnapshot.hasUnsavedChanges(settled: nil, current: snapshot(name: "Typed")) == false)
    }

    @Test func aChangedFieldIsAnUnsavedChange() {
        #expect(ModeEditorSnapshot.hasUnsavedChanges(settled: snapshot(), current: snapshot()) == false)
        #expect(ModeEditorSnapshot.hasUnsavedChanges(settled: snapshot(), current: snapshot(name: "HyperZZ")))

        var otherModel = snapshot()
        otherModel.model = "qwen3-asr-0.6b"
        #expect(ModeEditorSnapshot.hasUnsavedChanges(settled: snapshot(), current: otherModel))

        var medical = snapshot()
        medical.cloudTranscriptionDomain = "medical"
        #expect(ModeEditorSnapshot.hasUnsavedChanges(settled: snapshot(), current: medical))
    }

    // MARK: - Wiring

    private static let menuBarPath = "app/macos/hyperwhisper/Views/MainAppView.swift"
    private static let fileFlowPath = "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"
    private static let editorPath = "app/macos/hyperwhisper/Views/Modes/ModeEditorView.swift"

    @Test func theMenuBarHistoryAndSettingsAreUserChosen() throws {
        let menu = try ProductionSource.slice(
            of: Self.menuBarPath,
            from: "onReceive(audioManager.$selectedDevice) { device in",
            to: "Text(localized: \"menu.microphone\")"
        )
        #expect(menu.contains("appState.requestNavigation(to: .history, trigger: .userChosen)"))
        #expect(menu.contains("appState.requestNavigation(to: .settings, trigger: .userChosen)"))
        #expect(!menu.contains("selectedNavigationItem ="))
    }

    @Test func theFileJobEndIsAnAutomaticJump() throws {
        let source = try ProductionSource.code(of: Self.fileFlowPath)
        #expect(source.contains("appState?.requestNavigation(to: .history, trigger: .automatic)"))
        #expect(!source.contains("selectedNavigationItem = .history"))
    }

    @Test func theErrorToastsSettingsActionIsAnAutomaticJump() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Models/AppState.swift",
            from: "func openSettingsFromErrorToast() {",
            to: "bringMainWindowToFront()"
        )
        #expect(body.contains("requestNavigation(to: .settings, trigger: .automatic)"))
        #expect(!body.contains("selectedNavigationItem ="))
    }

    @Test func theEditorRegistersReportsAndPrompts() throws {
        let source = try ProductionSource.code(of: Self.editorPath)
        #expect(source.contains("appState.modeEditorDidOpen(session: editorSession)"))
        #expect(source.contains("appState.modeEditorDidClose(session: editorSession)"))
        #expect(source.contains("appState.modeEditor(session: editorSession, hasUnsavedChanges: unsaved)"))
        #expect(source.contains(".alert(\"modes.editor.discard.title\".localized, isPresented: discardPromptIsPresented)"))
        #expect(source.contains("appState.discardModeEditorAndNavigate()"))
        #expect(source.contains("appState.keepEditingModeEditor()"))
        // The editor's own Manage in Library link dismisses first, then goes.
        #expect(source.contains("appState.navigateToModelLibraryAPIKeys(trigger: .fromModeEditor)"))
        #expect(!source.contains("appState.navigateToModelLibraryAPIKeys()"))
    }

    /// Return and Escape both keep the edit; no key presses Discard. Keep
    /// Editing holds Return, so the alert leaves Escape unbound, and a key
    /// monitor that lives only while the prompt is up gives it to Keep Editing.
    @Test func returnAndEscapeKeepEditingAndNoKeyDiscards() throws {
        let alert = try ProductionSource.slice(
            of: Self.editorPath,
            from: ".alert(\"modes.editor.discard.title\".localized, isPresented: discardPromptIsPresented) {",
            to: "} message: {"
        )
        let keepEditing = try #require(alert.components(separatedBy: "Button(role: .destructive)").first)
        #expect(keepEditing.contains("appState.keepEditingModeEditor()"))
        #expect(keepEditing.contains(".keyboardShortcut(.defaultAction)"))
        let discard = try #require(alert.components(separatedBy: "Button(role: .destructive)").last)
        #expect(discard.contains("appState.discardModeEditorAndNavigate()"))
        #expect(!discard.contains(".keyboardShortcut"))

        let source = try ProductionSource.code(of: Self.editorPath)
        #expect(source.contains(".onChange(of: discardPromptIsPresented.wrappedValue) { _, presented in"))
        #expect(source.contains("installDiscardPromptEscapeMonitor()"))
        let monitor = try ProductionSource.slice(
            of: Self.editorPath,
            from: "private func installDiscardPromptEscapeMonitor() {",
            to: "private func removeDiscardPromptEscapeMonitor() {"
        )
        #expect(monitor.contains("NSEvent.addLocalMonitorForEvents(matching: .keyDown)"))
        #expect(monitor.contains("event.keyCode == 53"))
        #expect(monitor.contains("state.keepEditingModeEditor()"))
        #expect(!monitor.contains("discardModeEditorAndNavigate"))
        let teardown = try ProductionSource.slice(
            of: Self.editorPath,
            from: "private func removeDiscardPromptEscapeMonitor() {",
            to: "private var editorHeader: some View {"
        )
        #expect(teardown.contains("NSEvent.removeMonitor(monitor)"))
    }

    @Test func everyLocaleHasThePromptKeys() throws {
        let keys = [
            "modes.editor.discard.title",
            "modes.editor.discard.message",
            "modes.editor.discard.keepEditing",
            "modes.editor.discard.button",
        ]
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            for key in keys {
                let matches = lines.filter { $0.hasPrefix("\"\(key)\" = \"") }
                #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(key)")
                guard let line = matches.first else { continue }
                #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent) \(key)")
                #expect(!line.contains("%"), "\(locale.lastPathComponent) \(key) takes no format argument")
            }
        }
    }
}
