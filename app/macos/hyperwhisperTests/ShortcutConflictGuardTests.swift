//
//  ShortcutConflictGuardTests.swift
//  hyperwhisperTests
//
//  Issue #1474: a shortcut recorder refuses a combo that another HyperWhisper
//  action already uses and names that action. These cover the rules; the
//  Carbon hotkey path that applies them needs a real key press.
//

import Foundation
import Testing
@testable import HyperWhisper

struct ShortcutConflictGuardTests {
    /// Change Mode's combo from the issue: ^⇧J = keyCode 38, mods 4608.
    private struct Combo: Equatable {
        let keyCode: Int
        let modifiers: Int
    }

    private static let controlShiftJ = Combo(keyCode: 38, modifiers: 4608)
    private static let optionSpace = Combo(keyCode: 49, modifiers: 2048)
    private static let escape = Combo(keyCode: 53, modifiers: 0)

    private static let bindings: [(name: String, shortcut: Combo?)] = [
        ("toggleRecordingWithTranscription", optionSpace),
        ("cancelRecording", escape),
        ("changeMode", controlShiftJ),
        ("quickCapture", nil),
    ]

    @Test func anotherActionsComboIsRefusedWithThatActionsName() {
        #expect(ShortcutConflictGuard.conflictingAction(
            for: Self.controlShiftJ, recording: "cancelRecording", bindings: Self.bindings
        ) == "changeMode")
        #expect(ShortcutConflictGuard.conflictingAction(
            for: Self.controlShiftJ, recording: "toggleRecordingWithTranscription", bindings: Self.bindings
        ) == "changeMode")
        #expect(ShortcutConflictGuard.conflictingAction(
            for: Self.optionSpace, recording: "changeMode", bindings: Self.bindings
        ) == "toggleRecordingWithTranscription")
    }

    @Test func theRecordersOwnComboIsNotAConflict() {
        #expect(ShortcutConflictGuard.conflictingAction(
            for: Self.controlShiftJ, recording: "changeMode", bindings: Self.bindings
        ) == nil)
    }

    @Test func anUnusedComboIsNotAConflict() {
        let controlShiftU = Combo(keyCode: 32, modifiers: 4608)
        #expect(ShortcutConflictGuard.conflictingAction(
            for: controlShiftU, recording: "toggleRecordingWithTranscription", bindings: Self.bindings
        ) == nil)
    }

    @Test func onlyLiveFeaturesBlockACombo() {
        let allOff = ShortcutConflictGuard.liveActions(
            pushToTalkMode: .fn, quickCaptureEnabled: false, streamingEnabled: false
        ).map(\.rawValue)
        #expect(allOff == ["toggleRecordingWithTranscription", "cancelRecording", "changeMode"])

        let allOn = ShortcutConflictGuard.liveActions(
            pushToTalkMode: .custom, quickCaptureEnabled: true, streamingEnabled: true
        ).map(\.rawValue)
        #expect(Set(allOn) == [
            "toggleRecordingWithTranscription", "cancelRecording", "changeMode",
            "pushToTalk", "quickCapture", "startStreaming",
        ])
    }

    @Test func everyLiveActionHasAReadableName() {
        let names = ShortcutConflictGuard.liveActions(
            pushToTalkMode: .custom, quickCaptureEnabled: true, streamingEnabled: true
        )
        for name in names {
            let shown = ShortcutConflictGuard.displayName(for: name)
            #expect(!shown.isEmpty)
            #expect(shown != name.rawValue)
            #expect(!shown.hasPrefix("settings."))
        }
    }
}
