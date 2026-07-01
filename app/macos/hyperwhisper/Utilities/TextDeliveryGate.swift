//
//  TextDeliveryGate.swift
//  hyperwhisper
//
//  Single source of truth for whether transcribed text may be delivered into
//  another application right now.
//

import Atomics
import Foundation

/// Gate that every transcript-delivery sink consults before emitting text into
/// another application.
///
/// Transcription reaches other apps through three independent sinks:
/// - CGEvent keystroke typing + clipboard paste — `TextInputService`
///   (`typeText`, `typeTextAsync`, `typeSegment`, `pasteTextForStreaming`)
/// - accessibility-driven paste — `AutoPasteHandler.handleAutoPaste`
/// - AppleScript into Notes — `NotesDestination.send`
///
/// Each sink checks `isSuppressed` before emitting, so suppression is defined in
/// exactly ONE place and no *caller* can leak text by forgetting a per-call
/// guard. New delivery paths inherit suppression for free the moment they route
/// through a sink — which is what previously went wrong: guards lived at caller
/// sites, so the streaming live-typing path was missed.
///
/// Currently suppressed for the entire lifetime of the first-run onboarding sheet
/// (driven by `AppState.showOnboarding`), where transcripts are surfaced inline
/// only and must never reach another app — regardless of trigger (global
/// shortcut, streaming shortcut, or the in-onboarding "give it a try" button).
///
/// Backed by an atomic because the streaming callbacks and `TextInputService`'s
/// serial coordinator both run off the main actor and must read the flag without
/// hopping to `@MainActor`.
enum TextDeliveryGate {
    private static let suppressed = ManagedAtomic<Bool>(false)

    /// True when delivery into other apps is blocked. Safe to read from any thread.
    static var isSuppressed: Bool { suppressed.load(ordering: .acquiring) }

    /// Update the suppression state. Driven by `AppState.showOnboarding`'s `didSet`.
    static func setSuppressed(_ value: Bool) {
        suppressed.store(value, ordering: .releasing)
    }
}
