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
            scopeExtras: [
                "recording_start_step": "captured", "prompt_body": "private", "transcript_empty": true
            ],
            scopeTags: ["build_number": "100", "component": "scope"],
            extras: ["duration_ms": 42, "component": "extra", "spoken_words": "private"],
            tags: ["component": "transcription", "unreviewed_tag": "private"]
        )

        #expect(attributes["recording_start_step"] as? String == "captured")
        #expect(attributes["build_number"] as? String == "100")
        #expect(attributes["duration_ms"] as? Int == 42)
        #expect(attributes["component"] as? String == "transcription")
        #expect(attributes["prompt_body"] as? String == "[redacted]")
        #expect(attributes["transcript_empty"] as? Bool == true)
        // Not on the allow-list: an extra and a tag both redact on the Logs path.
        #expect(attributes["spoken_words"] as? String == "[redacted]")
        #expect(attributes["unreviewed_tag"] as? String == "[redacted]")
    }

    /// Issue #684: the key rule only flags a field; the value's type decides.
    /// A Bool or a number cannot hold speech, so it survives a key that is not
    /// on the allow-list.
    @Test func redactionKeepsMeasurementsAndRedactsContent() {
        let flag = SentryService.redactedValue(
            forKey: "backend_empty_transcript_without_flag", value: false)
        #expect(flag as? Bool == false)
        #expect(SentryService.redactedValue(
            forKey: "transcript_preview", value: "hello world") as? String == "[redacted]")
        #expect(SentryService.redactedValue(
            forKey: "paste_character_count", value: 286) as? Int == 286)
        #expect(SentryService.redactedValue(
            forKey: "prompt_token_count", value: 42) as? Int == 42)
        #expect(SentryService.redactedValue(
            forKey: "transcript_confidence", value: 0.5) as? Double == 0.5)
        // Containers and null can hold content, or flatten to it, so they redact.
        let containers: [Any] = [["hello"], ["text": "hello"], NSNull()]
        for value in containers {
            #expect(SentryService.redactedValue(
                forKey: "prompt_parts", value: value) as? String == "[redacted]")
        }
        // An allow-listed key is never touched, whatever it holds.
        #expect(SentryService.redactedValue(
            forKey: "recording_start_step", value: "captured") as? String == "captured")
        #expect(SentryService.redactedValue(
            forKey: "stageTimeline", value: ["start=10ms@10ms"]) as? [String] == ["start=10ms@10ms"])
    }

    // MARK: - The exact allow-list (issue #805)

    /// Keys that really hold spoken or typed text stay redacted. None of them is
    /// on the list, and none may ever be added to it.
    @Test func keysThatHoldTranscriptTextAreStillRedacted() {
        let transcript = "please send the quarterly numbers to my accountant"
        let contentKeys = [
            "transcript", "text", "prompt", "transcript_preview", "rawText", "processedText",
            "prompt_body", "modeName", "mode_name", "vocabulary", "filePath", "audioURL"
        ]
        for key in contentKeys {
            #expect(SentryService.isRedactedExtraKey(key), "\(key) must be redacted")
            #expect(SentryService.redactedValue(forKey: key, value: transcript) as? String
                    == "[redacted]", "\(key) leaked its value")
        }
    }

    /// A key nobody has reviewed yet is hidden until someone adds it: its String
    /// becomes `[redacted]`. This is the property the substring rule lacked (#795).
    @Test func aStringUnderAnUnknownKeyIsRedacted() {
        let unknown = "a_key_added_next_month"

        #expect(SentryService.allowedExtraKeys.contains(unknown) == false)
        #expect(SentryService.isRedactedExtraKey(unknown))
        #expect(SentryService.redactedValue(forKey: unknown, value: "anything at all") as? String
                == "[redacted]")
        // Its measurements still survive (#684).
        #expect(SentryService.redactedValue(forKey: unknown, value: true) as? Bool == true)
        #expect(SentryService.redactedValue(forKey: unknown, value: 7) as? Int == 7)
    }

    /// The match is exact: no case folding, no prefix, no substring. A key that
    /// only LOOKS like an allowed one is redacted.
    @Test func theAllowListMatchesExactly() {
        #expect(!SentryService.isRedactedExtraKey("transcriptionState"))
        #expect(!SentryService.isRedactedExtraKey("isHyperwhisperTranscription"))
        #expect(!SentryService.isRedactedExtraKey("fileExtension"))
        #expect(!SentryService.isRedactedExtraKey("audio_file_extension"))
        #expect(!SentryService.isRedactedExtraKey("save_context"))

        #expect(SentryService.isRedactedExtraKey("TranscriptionState"))
        #expect(SentryService.isRedactedExtraKey("transcriptionstate"))
        #expect(SentryService.isRedactedExtraKey("transcriptionState_text"))
        #expect(SentryService.isRedactedExtraKey("fileExtension "))
        #expect(SentryService.isRedactedExtraKey(""))
    }
}
