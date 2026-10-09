//
//  PageChangeGateTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1672.
//
//  Changing the main window's page while the shown page still presented a
//  sheet (Modes → Edit Mode, then menu bar Settings…) removed the page and its
//  sheet in one update; AppKit's sheet-close animation then re-entered the
//  update cycle from inside its own commit and the app died with SIGSEGV.
//
//  The fix keeps the SHOWN page (`displayedNavigationItem`) apart from the
//  ASKED-FOR page (`selectedNavigationItem`). With a sheet up the page change
//  is held, the page is asked to close its sheets, and the page changes on a
//  later run-loop turn once the sheet has gone.
//
//  The rules are a plain value (`PageChangeGate`) and are tested by calling
//  it; the AppState steps run on a bare AppState with the window's sheet
//  stood in by `pageSheetPresenceOverride`. Only the view wiring is pinned by
//  reading the source, as `ModeEditorSheetHeightTests` does.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("A page change waits for the shown page's sheet to close (#1672)")
struct PageChangeGateTests {

    // MARK: - The gate

    @Test func withNoSheetThePageChangesAtOnce() {
        var gate = PageChangeGate(displayed: .modes)
        #expect(gate.request(.settings, sheetPresented: false) == .show(.settings))
        #expect(gate.displayed == .settings)
        #expect(gate.pending == nil)
    }

    @Test func withASheetUpThePageWaitsUntilTheSheetHasGone() {
        var gate = PageChangeGate(displayed: .modes)
        #expect(gate.request(.settings, sheetPresented: true) == .waitForSheet)
        #expect(gate.displayed == .modes)
        #expect(gate.pending == .settings)
        #expect(gate.isWaitingForSheet)

        // Still closing: keep waiting.
        #expect(gate.recheck(sheetPresented: true, waitLimitPassed: false) == .waitForSheet)
        #expect(gate.displayed == .modes)

        // Gone: show the held page, once.
        #expect(gate.recheck(sheetPresented: false, waitLimitPassed: false) == .show(.settings))
        #expect(gate.displayed == .settings)
        #expect(gate.pending == nil)
        #expect(gate.recheck(sheetPresented: false, waitLimitPassed: false) == PageChangeGate.Outcome.unchanged)
    }

    @Test func theLastRequestWins() {
        var gate = PageChangeGate(displayed: .modes)
        _ = gate.request(.settings, sheetPresented: true)
        #expect(gate.request(.history, sheetPresented: true) == .waitForSheet)
        #expect(gate.pending == .history)
        #expect(gate.recheck(sheetPresented: false, waitLimitPassed: false) == .show(.history))
        #expect(gate.displayed == .history)
    }

    @Test func askingForTheShownPageRemovesNothingAndDropsAHeldPage() {
        var gate = PageChangeGate(displayed: .modes)
        #expect(gate.request(.modes, sheetPresented: true) == PageChangeGate.Outcome.unchanged)
        #expect(gate.pending == nil)

        _ = gate.request(.settings, sheetPresented: true)
        #expect(gate.request(.modes, sheetPresented: true) == PageChangeGate.Outcome.unchanged)
        #expect(gate.pending == nil)
        #expect(gate.displayed == .modes)
        #expect(gate.recheck(sheetPresented: false, waitLimitPassed: false) == PageChangeGate.Outcome.unchanged)
    }

    @Test func aRequestAfterTheSheetHasGoneShowsAtOnce() {
        var gate = PageChangeGate(displayed: .modes)
        _ = gate.request(.settings, sheetPresented: true)
        #expect(gate.request(.history, sheetPresented: false) == .show(.history))
        #expect(gate.pending == nil)
        #expect(gate.displayed == .history)
    }

    /// A sheet that never answers the close request cannot pin the window to
    /// one page.
    @Test func theWaitLimitShowsTheHeldPageWithTheSheetStillUp() {
        var gate = PageChangeGate(displayed: .modelLibrary)
        _ = gate.request(.home, sheetPresented: true)
        #expect(gate.recheck(sheetPresented: true, waitLimitPassed: true) == .show(.home))
        #expect(gate.displayed == .home)
    }

    @Test func theWaitLimitOutlastsASheetCloseAnimation() {
        #expect(PageChangeGate.sheetWaitLimit >= 1.0)
        #expect(PageChangeGate.sheetPollInterval > 0)
        #expect(PageChangeGate.sheetPollInterval < PageChangeGate.sheetWaitLimit)
    }

    // MARK: - AppState steps

    @MainActor
    private func appState(on page: NavigationItem, sheetUp: @escaping () -> Bool) -> AppState {
        let appState = AppState()
        appState.pageSheetPresenceOverride = { false }
        appState.selectedNavigationItem = page
        appState.pageSheetPresenceOverride = sheetUp
        return appState
    }

    @MainActor
    @Test func withNoSheetAppStateShowsTheNewPageAtOnce() {
        let appState = appState(on: .modes, sheetUp: { false })
        #expect(appState.displayedNavigationItem == .modes)
        let before = appState.pageSheetDismissalRequest

        appState.navigate(to: .history)
        #expect(appState.selectedNavigationItem == .history)
        #expect(appState.displayedNavigationItem == .history)
        #expect(appState.pageSheetDismissalRequest == before)
        #expect(appState.pageChangeGate.isWaitingForSheet == false)
    }

    @MainActor
    @Test func aDirectWriteIsHeldWhileASheetIsUpAndAsksThePageToCloseIt() {
        var sheetUp = true
        let appState = appState(on: .modes, sheetUp: { sheetUp })
        let before = appState.pageSheetDismissalRequest

        // The menu bar's Settings… writes the property directly.
        appState.selectedNavigationItem = .settings
        #expect(appState.selectedNavigationItem == .settings)
        #expect(appState.displayedNavigationItem == .modes)
        #expect(appState.pageSheetDismissalRequest == before + 1)

        #expect(appState.recheckPendingPageChange())
        #expect(appState.displayedNavigationItem == .modes)

        sheetUp = false
        #expect(appState.recheckPendingPageChange() == false)
        #expect(appState.displayedNavigationItem == .settings)
        #expect(appState.pageChangeGate.isWaitingForSheet == false)
    }

    @MainActor
    @Test func theSettingsSectionStillAppliesToAHeldSettingsPage() {
        var sheetUp = true
        let appState = appState(on: .modes, sheetUp: { sheetUp })

        appState.navigateToSettings(section: "shortcuts")
        #expect(appState.displayedNavigationItem == .modes)
        #expect(appState.selectedSettingsSection == "shortcuts")

        sheetUp = false
        appState.recheckPendingPageChange()
        #expect(appState.displayedNavigationItem == .settings)
        #expect(appState.selectedSettingsSection == "shortcuts")
    }

    @MainActor
    @Test func theAPIKeysFlagWaitsForAHeldModelLibraryPage() {
        var sheetUp = true
        let appState = appState(on: .modes, sheetUp: { sheetUp })

        // The mode editor's Manage in Library link, while its sheet closes.
        appState.navigateToModelLibraryAPIKeys()
        #expect(appState.displayedNavigationItem == .modes)
        #expect(appState.shouldOpenModelLibraryAPIKeys)

        sheetUp = false
        appState.recheckPendingPageChange()
        #expect(appState.displayedNavigationItem == .modelLibrary)
        // ModelLibraryView consumes the flag when it appears.
        #expect(appState.shouldOpenModelLibraryAPIKeys)
    }

    @MainActor
    @Test func theLastOfSeveralHeldRequestsIsShown() {
        var sheetUp = true
        let appState = appState(on: .modes, sheetUp: { sheetUp })

        appState.selectedNavigationItem = .settings
        appState.selectedNavigationItem = .history
        #expect(appState.displayedNavigationItem == .modes)

        sheetUp = false
        appState.recheckPendingPageChange()
        #expect(appState.displayedNavigationItem == .history)
    }

    @MainActor
    @Test func goingBackToTheShownPageCancelsTheHeldOne() {
        var sheetUp = true
        let appState = appState(on: .modes, sheetUp: { sheetUp })

        appState.selectedNavigationItem = .settings
        appState.selectedNavigationItem = .modes
        sheetUp = false
        #expect(appState.recheckPendingPageChange() == false)
        #expect(appState.displayedNavigationItem == .modes)
    }

    @MainActor
    @Test func aSheetThatNeverClosesIsOutwaited() {
        let appState = appState(on: .modes, sheetUp: { true })

        appState.selectedNavigationItem = .history
        #expect(appState.recheckPendingPageChange(now: Date()))
        #expect(appState.displayedNavigationItem == .modes)

        let pastLimit = Date().addingTimeInterval(PageChangeGate.sheetWaitLimit + 1)
        #expect(appState.recheckPendingPageChange(now: pastLimit) == false)
        #expect(appState.displayedNavigationItem == .history)
    }

    @MainActor
    @Test func noWindowMeansNoSheet() {
        #expect(AppState.windowHasSwiftUISheet(nil) == false)
    }

    // MARK: - Wiring

    private static let mainAppViewPath = "app/macos/hyperwhisper/Views/MainAppView.swift"

    /// The window shows the displayed page, so every writer of
    /// `selectedNavigationItem` (sidebar, menu bar, navigate(to:), flows) is
    /// covered by the gate.
    @Test func theContentAreaSwitchesOnTheDisplayedPage() throws {
        let content = try ProductionSource.slice(
            of: Self.mainAppViewPath,
            from: "private var contentView: some View {",
            to: "case .home:"
        )
        #expect(content.contains("switch appState.displayedNavigationItem"))
        #expect(!content.contains("selectedNavigationItem"))
    }

    @Test func everyWriteToTheSelectedPageGoesThroughTheGate() throws {
        let property = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Models/AppState.swift",
            from: "@Published var selectedNavigationItem: NavigationItem = .home {",
            to: "@Published private(set) var displayedNavigationItem"
        )
        #expect(property.contains("didSet { routePageChange(to: selectedNavigationItem) }"))
    }

    /// Each page (or sheet) that presents a sheet, alert or dialog closes it
    /// when a page change waits, and closes every presentation it owns.
    private static let pagesThatClose: [(path: String, closes: [String])] = [
        ("app/macos/hyperwhisper/Views/Modes/ModesView.swift",
         ["showingCreateMode = false", "selectedMode = nil", "showingDeleteConfirm = false", "showingLastModeAlert = false"]),
        ("app/macos/hyperwhisper/Views/ModelLibrary/ModelLibraryView.swift",
         ["sheetTarget = nil", "showAPIKeysManager = false", "showCustomEndpointSheet = false", "showingModelInUseAlert = false", "pendingRemoval = nil"]),
        ("app/macos/hyperwhisper/Views/ModelLibrary/Modals/APIKeysManagerModal.swift",
         ["sheetTarget = nil"]),
        ("app/macos/hyperwhisper/Views/Settings/BackupSettingsSection.swift",
         ["importRequest = nil", "showResultAlert = false", "showLocalDownloadPrompt = false"]),
        ("app/macos/hyperwhisper/Views/Settings/CloudAccountSettingsSection.swift",
         ["showLicenseSuccess = false", "showLicenseError = false"]),
        ("app/macos/hyperwhisper/Views/Settings/ShortcutsSettingsSection.swift",
         ["showResetShortcutsConfirmation = false"]),
        ("app/macos/hyperwhisper/Views/Settings/VocabularySettingsSection.swift",
         ["showRestartAlert = false"]),
        ("app/macos/hyperwhisper/Views/VocabularyView.swift",
         ["showDuplicateAlert = false"]),
    ]

    @Test func everyPageWithAPresentationClosesItOnAPageChange() throws {
        for page in Self.pagesThatClose {
            let handler = try ProductionSource.slice(
                of: page.path,
                from: ".closesPresentationsOnPageChange {",
                to: "}"
            )
            for line in page.closes {
                #expect(handler.contains(line), "\(page.path) must run \(line)")
            }
        }
    }

    /// The API keys manager's own sheet reads AppState for its close, and a
    /// macOS sheet is not guaranteed to inherit environment objects.
    @Test func theAPIKeysManagerSheetIsGivenAppState() throws {
        let sheet = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Views/ModelLibrary/ModelLibraryView.swift",
            from: ".sheet(isPresented: $showAPIKeysManager) {",
            to: ".sheet(isPresented: $showCustomEndpointSheet"
        )
        #expect(sheet.contains(".environmentObject(appState)"))
    }
}
