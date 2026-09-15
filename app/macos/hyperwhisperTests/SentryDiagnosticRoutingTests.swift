//
//  SentryDiagnosticRoutingTests.swift
//  hyperwhisperTests
//
//  `SentryService.captureMessage` used to send every diagnostic as an Event, so
//  an expected outcome landed in the Issues list beside a crash and spent the
//  error quota. The severity now picks the store, and this file pins that table.
//
//  It pins the DECISION only. It starts no SDK, so it needs no DSN and no
//  network, and it runs in the CI gate. Two things underneath it are not pinned
//  here: the map from `SentryLevel` to `DiagnosticSeverity`, and
//  `options.experimental.enableLogs` in `SentryService.initialize`. Both name
//  Sentry types, and this target does not link the Sentry package.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Sentry diagnostic routing")
struct SentryDiagnosticRoutingTests {

    /// The cheap store takes everything a person would not open one at a time.
    @Test func nonErrorSeveritiesBecomeLogs() {
        #expect(SentryService.store(for: .debug) == .log)
        #expect(SentryService.store(for: .info) == .log)
        #expect(SentryService.store(for: .warning) == .log)
    }

    /// The expensive store keeps what a person is meant to act on.
    ///
    /// Warning: do not move these to save quota. A real defect that stops being
    /// an Issue is a defect nobody is told about.
    @Test func errorSeveritiesStayIssues() {
        #expect(SentryService.store(for: .error) == .issue)
        #expect(SentryService.store(for: .fatal) == .issue)
    }

    /// Every severity has a store. A new case added without a decision is a
    /// compile error in `store(for:)`, and this proves the table stays total.
    @Test func everySeverityIsRouted() {
        #expect(SentryService.DiagnosticSeverity.allCases.count == 5)
        for severity in SentryService.DiagnosticSeverity.allCases {
            let store = SentryService.store(for: severity)
            #expect(store == .log || store == .issue)
        }
    }

    /// Every severity the call sites pass today. If one of them moves between
    /// the stores, that is a change to the error quota, and it shows up here.
    @Test func theCallSitesLandWhereTheyAreMeantTo() {
        let callSites: [(String, SentryService.DiagnosticSeverity, SentryService.DiagnosticStore)] = [
            ("Auto-paste failed (expected outcome)", .warning, .log),
            ("Auto-paste failed (defect)", .error, .issue),
            ("Parakeet runtime load cancelled", .info, .log),
            ("Parakeet preparation rejected", .error, .issue),
            ("Auto-delete aborted: Core Data transaction failed", .error, .issue),
            ("Auto-delete could not remove some audio files", .warning, .log),
        ]

        for (name, severity, expected) in callSites {
            #expect(
                SentryService.store(for: severity) == expected,
                "\(name) moved between the two stores"
            )
        }
    }

    /// Pin the severity used by the production no-speech capture. This reads
    /// the value the call site passes instead of repeating its message/level.
    @Test func noSpeechDiagnosticsUseTheLogsStore() {
        #expect(
            SentryService.store(for: TranscriptionDiagnosticsService.sentrySeverity) == .log
        )
    }

    /// Sentry Cocoa 8.x does not apply scope tags or extras to Logs. Pin the
    /// wrapper's flattening, precedence and privacy behavior without a network.
    @Test func logsKeepScopeContextAndRedactContentKeys() {
        let attributes = SentryService.mergeLogAttributes(
            scopeExtras: ["recording_stage": "captured", "prompt_body": "private"],
            scopeTags: ["build_number": "100", "component": "scope"],
            extras: ["duration_ms": 42, "component": "extra"],
            tags: ["component": "transcription"]
        )

        #expect(attributes["recording_stage"] as? String == "captured")
        #expect(attributes["build_number"] as? String == "100")
        #expect(attributes["duration_ms"] as? Int == 42)
        #expect(attributes["component"] as? String == "transcription")
        #expect(attributes["prompt_body"] as? String == "[redacted]")
    }
}
