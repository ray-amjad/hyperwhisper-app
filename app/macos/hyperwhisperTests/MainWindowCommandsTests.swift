//
//  MainWindowCommandsTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1526.
//
//  After cmd+W hid the main window there was no keyboard or menu way back:
//  cmd+, did nothing, the app menu had no Settings…, and the Window menu's
//  window list was emptied. Ray's decisions (inbox ask #333): Settings… with
//  cmd+, in the app menu opens the main window on Settings, and the Window menu
//  gets one item that reopens the main window instead of the system list.
//
//  What Settings… does is a static on `MainWindowCommands` and is tested by
//  calling it on a bare AppState. Which menu group each item lands in and its
//  shortcut are SwiftUI `Commands` wiring, so they are pinned by reading the
//  source, as `ModeEditorPageGuardTests` does for the menu bar.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Settings… and the Window menu reopen the main window (#1526)")
struct MainWindowCommandsTests {

    // MARK: - Settings…

    @MainActor
    @Test func settingsShowsTheWindowThenOpensTheSettingsPage() {
        let appState = AppState()
        appState.selectedNavigationItem = .history
        var shown = 0

        MainWindowCommands.openSettings(appState: appState) { shown += 1 }

        #expect(shown == 1)
        #expect(appState.selectedNavigationItem == .settings)
    }

    @MainActor
    @Test func settingsIsAUserChosenJumpThatAsksAnUnsavedEditorFirst() {
        let appState = AppState()
        appState.selectedNavigationItem = .modes
        let session = UUID()
        appState.modeEditorDidOpen(session: session)
        appState.modeEditor(session: session, hasUnsavedChanges: true)
        var shown = 0

        MainWindowCommands.openSettings(appState: appState) { shown += 1 }

        // The window still comes forward, so the "Discard unsaved changes?"
        // prompt is visible; the page waits for Discard.
        #expect(shown == 1)
        #expect(appState.selectedNavigationItem == .modes)
        #expect(appState.pendingModeEditorNavigation == .settings)
    }

    // MARK: - Wiring

    private static let appPath = "app/macos/hyperwhisper/hyperwhisperApp.swift"

    @Test func theAppCommandsUseMainWindowCommands() throws {
        let commands = try ProductionSource.slice(
            of: Self.appPath,
            from: ".commands {",
            to: "MenuBarExtra {"
        )
        #expect(commands.contains("MainWindowCommands("))
        // The Window menu item reads the window's own title, from one place.
        #expect(commands.contains("windowTitle: mainWindowTitle"))
        let app = try ProductionSource.code(of: Self.appPath)
        #expect(app.contains("WindowGroup(mainWindowTitle, id: \"mainWindow\")"))
        // The empty window list this issue was about is gone from the scene.
        #expect(!commands.contains("CommandGroup(replacing: .windowList) { }"))
    }

    @Test func settingsSitsInTheAppMenuWithCommandComma() throws {
        let group = try ProductionSource.slice(
            of: Self.appPath,
            from: "CommandGroup(replacing: .appSettings) {",
            to: "CommandGroup(replacing: .windowList) {"
        )
        #expect(group.contains("Label(\"menu.settings\".localized, systemImage: \"gearshape\")"))
        #expect(group.contains("Self.openSettings(appState: appState, showMainWindow: showMainWindow)"))
        #expect(group.contains(".keyboardShortcut(\",\", modifiers: .command)"))
    }

    @Test func theWindowMenuHasOneItemThatReopensTheMainWindow() throws {
        let group = try ProductionSource.slice(
            of: Self.appPath,
            from: "CommandGroup(replacing: .windowList) {",
            to: "static func openSettings("
        )
        #expect(group.contains("Button(windowTitle)"))
        #expect(group.contains("showMainWindow()"))
        #expect(group.contains(".keyboardShortcut(\"0\", modifiers: .command)"))
        #expect(group.components(separatedBy: "Button(").count == 2)
    }

    @Test func showMainWindowReusesTheWindowOrOpensOne() throws {
        let body = try ProductionSource.slice(
            of: Self.appPath,
            from: "private func showMainWindow() {",
            to: "openWindow(id: \"mainWindow\")"
        )
        #expect(body.contains("HyperWhisperApp.suppressLaunchMinimizedHide()"))
        #expect(body.contains("MainWindowStore.window"))
        #expect(body.contains("makeKeyAndOrderFront(nil)"))
    }
}
