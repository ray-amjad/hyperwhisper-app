//
//  ModelessAlertTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1539.
//
//  The Transcribe File "Transcription Error" alert used `runModal()`, which held
//  the main actor until OK. The Local API is `@MainActor`, so every route —
//  `/health` included — answered nothing while the alert stayed open.
//
//  `ModelessAlert.show` must return at once with the alert on screen, and a
//  button click must close it and report which button. The flow's wiring is
//  pinned by reading the source, as `ModeEditorPageGuardTests` does.
//

import AppKit
import Testing
@testable import HyperWhisper

@MainActor
struct ModelessAlertTests {
    private static let fileFlowPath = "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"

    @Test func showReturnsWithTheAlertOnScreenAndAClickClosesIt() {
        let alert = NSAlert()
        alert.messageText = "Transcription Error"
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Other")

        var responses: [NSApplication.ModalResponse] = []
        // Under runModal this call would not return until a click, and the
        // test would hang here.
        ModelessAlert.show(alert) { responses.append($0) }

        #expect(alert.window.isVisible)
        #expect(responses.isEmpty)

        alert.buttons[1].performClick(nil)

        #expect(!alert.window.isVisible)
        #expect(responses == [.alertSecondButtonReturn])
    }

    @Test func theMainActorStaysFreeWhileTheAlertIsOpen() async {
        let alert = NSAlert()
        alert.addButton(withTitle: "OK")
        ModelessAlert.show(alert)
        defer { alert.buttons[0].performClick(nil) }

        // Another main-actor job (a Local API route, say) runs while it is open.
        let ran = await Task { @MainActor in true }.value
        #expect(ran)
        #expect(alert.window.isVisible)
    }

    @Test func theFileFlowErrorAlertDoesNotRunModal() throws {
        let source = try ProductionSource.code(of: Self.fileFlowPath)
        #expect(!source.contains("runModal()"))
        #expect(source.contains("ModelessAlert.show(alert)"))
    }
}
