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
//  ASKED-FOR page (`selectedNavigationItem`). With a page's sheet up the page
//  change is held, the page is asked to close its sheets, and the page changes
//  on a later run-loop turn once the sheet has gone. The shown page never
//  changes while the sheet is attached: past the wait limit the held change is
//  cancelled and the selection goes back to the shown page.
//
//  The rules are a plain value (`PageChangeGate`) and are tested by calling
//  it; the AppState steps run on a bare AppState with the window's sheet
//  stood in by `pageSheetPresenceOverride`. Only the view wiring is pinned by
//  reading the source, as `ModeEditorSheetHeightTests` does.
//

import AppKit
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

    /// Showing the held page while the sheet is attached would remove the
    /// sheet's presenter: the #1672 crash. No recheck may do it.
    @Test func aHeldPageIsNeverShownWhileTheSheetIsAttached() {
        for limitPassed in [false, true] {
            var gate = PageChangeGate(displayed: .modes)
            _ = gate.request(.settings, sheetPresented: true)
            let outcome = gate.recheck(sheetPresented: true, waitLimitPassed: limitPassed)
            #expect(outcome != .show(.settings))
            #expect(gate.displayed == .modes)
        }
    }

    /// A sheet that never answers the close request cancels the held page
    /// change; it is never forced under the sheet.
    @Test func theWaitLimitCancelsTheHeldPageWithTheSheetStillUp() {
        var gate = PageChangeGate(displayed: .modelLibrary)
        _ = gate.request(.home, sheetPresented: true)
        #expect(gate.recheck(sheetPresented: true, waitLimitPassed: true) == .cancelled(stayOn: .modelLibrary))
        #expect(gate.displayed == .modelLibrary)
        #expect(gate.pending == nil)
        #expect(gate.recheck(sheetPresented: false, waitLimitPassed: true) == PageChangeGate.Outcome.unchanged)
        // Putting the selection back on the shown page is never held again.
        #expect(gate.request(.modelLibrary, sheetPresented: true) == PageChangeGate.Outcome.unchanged)
        #expect(gate.pending == nil)
    }

    @Test func aSheetThatGoesJustAtTheLimitStillShowsTheHeldPage() {
        var gate = PageChangeGate(displayed: .modes)
        _ = gate.request(.history, sheetPresented: true)
        #expect(gate.recheck(sheetPresented: false, waitLimitPassed: true) == .show(.history))
    }

    @Test func theWaitLimitOutlastsASheetCloseAnimation() {
        #expect(PageChangeGate.sheetWaitLimit >= 1.0)
        #expect(PageChangeGate.sheetPollInterval > 0)
        #expect(PageChangeGate.sheetPollInterval < PageChangeGate.sheetWaitLimit)
    }

    // MARK: - AppState steps

    @MainActor
    private func makeAppState(on page: NavigationItem, sheetUp: @escaping () -> Bool) -> AppState {
        let appState = AppState()
        appState.pageSheetPresenceOverride = { false }
        appState.selectedNavigationItem = page
        appState.pageSheetPresenceOverride = sheetUp
        return appState
    }

    @MainActor
    @Test func withNoSheetAppStateShowsTheNewPageAtOnce() {
        let appState = makeAppState(on: .modes, sheetUp: { false })
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
        let appState = makeAppState(on: .modes, sheetUp: { sheetUp })
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
        let appState = makeAppState(on: .modes, sheetUp: { sheetUp })

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
        let appState = makeAppState(on: .modes, sheetUp: { sheetUp })

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
        let appState = makeAppState(on: .modes, sheetUp: { sheetUp })

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
        let appState = makeAppState(on: .modes, sheetUp: { sheetUp })

        appState.selectedNavigationItem = .settings
        appState.selectedNavigationItem = .modes
        sheetUp = false
        #expect(appState.recheckPendingPageChange() == false)
        #expect(appState.displayedNavigationItem == .modes)
    }

    @MainActor
    @Test func aSheetThatNeverClosesCancelsTheHeldPageAndRestoresTheSelection() {
        let appState = makeAppState(on: .modes, sheetUp: { true })
        let before = appState.pageSheetDismissalRequest

        appState.selectedNavigationItem = .history
        #expect(appState.recheckPendingPageChange(now: Date()))
        #expect(appState.displayedNavigationItem == .modes)

        let pastLimit = Date().addingTimeInterval(PageChangeGate.sheetWaitLimit + 1)
        #expect(appState.recheckPendingPageChange(now: pastLimit) == false)
        // Never shown under the sheet; the sidebar matches the content again.
        #expect(appState.displayedNavigationItem == .modes)
        #expect(appState.selectedNavigationItem == .modes)
        // The restore did not re-enter the gate as a new held request.
        #expect(appState.pageChangeGate.isWaitingForSheet == false)
        #expect(appState.pageSheetDismissalRequest == before + 1)
        #expect(appState.recheckPendingPageChange(now: pastLimit) == false)
        #expect(appState.displayedNavigationItem == .modes)
    }

    // MARK: - Which sheets hold a page change

    @MainActor
    private func makeWindow() -> NSWindow {
        NSWindow(contentRect: .zero, styleMask: [.titled], backing: .buffered, defer: true)
    }

    @MainActor
    @Test func noAttachedSheetHoldsNothing() {
        let presentations = PagePresentations()
        presentations.attachedSheet = { nil }
        presentations.report(UUID(), isPresenting: true)
        #expect(presentations.pageOwnsAttachedSheet == false)
    }

    /// MainAppView's alerts and the auto-paste NSAlert are not removed by a
    /// page change, so they neither hold nor cancel navigation.
    @MainActor
    @Test func aSheetNoPageReportsDoesNotHoldAPageChange() {
        let presentations = PagePresentations()
        let rootAlert = makeWindow()
        presentations.attachedSheet = { rootAlert }
        #expect(presentations.pageOwnsAttachedSheet == false)
    }

    @MainActor
    @Test func aPageSheetHoldsUntilAppKitHasDetachedIt() {
        let presentations = PagePresentations()
        let pageSheet = makeWindow()
        var attached: NSWindow? = pageSheet
        presentations.attachedSheet = { attached }
        let page = UUID()

        presentations.report(page, isPresenting: true)
        #expect(presentations.pageOwnsAttachedSheet)

        // The page has closed it; the sheet is still animating out.
        presentations.report(page, isPresenting: false)
        #expect(presentations.openOwners.isEmpty)
        #expect(presentations.pageOwnsAttachedSheet)

        attached = nil
        #expect(presentations.pageOwnsAttachedSheet == false)

        // A later sheet no page reports does not hold.
        let rootAlert = makeWindow()
        attached = rootAlert
        #expect(presentations.pageOwnsAttachedSheet == false)
    }

    @MainActor
    @Test func aPageThatGoesAwayStopsReporting() {
        let presentations = PagePresentations()
        let sheet = makeWindow()
        presentations.attachedSheet = { sheet }
        let page = UUID()
        presentations.report(page, isPresenting: true)
        presentations.remove(page)
        #expect(presentations.openOwners.isEmpty)
        #expect(presentations.pageOwnsAttachedSheet == false)
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

    /// Each page (or sheet) that presents a sheet, alert or dialog reports it
    /// open, and closes every presentation it owns when a page change waits.
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
                from: ".closesPresentationsOnPageChange(",
                to: "}"
            )
            let reported = try ProductionSource.slice(
                of: page.path,
                from: "isPresenting:",
                to: ") {"
            )
            for line in page.closes {
                #expect(handler.contains(line), "\(page.path) must run \(line)")
                let state = String(line.prefix { $0 != " " })
                #expect(reported.contains(state), "\(page.path) must report \(state) as presenting")
            }
        }
    }

    /// The modifier reads nothing from the environment, so a `#Preview` or a
    /// sheet host without AppState does not crash.
    @Test func theModifierNeedsNoAppStateInTheEnvironment() throws {
        let code = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Views/Components/ClosesPresentationsOnPageChange.swift"
        )
        #expect(!code.contains("@EnvironmentObject"))
        #expect(!code.contains("@Environment("))
        #expect(!code.contains("AppState"))
        #expect(code.contains(".onReceive(PagePresentations.shared.closeRequests)"))
    }
}
