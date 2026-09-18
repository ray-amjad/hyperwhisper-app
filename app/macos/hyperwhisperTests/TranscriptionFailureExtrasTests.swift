//
//  TranscriptionFailureExtrasTests.swift
//  hyperwhisperTests
//
//  Issue #795: every macOS transcription failure used to send the Mode's `name`
//  to Sentry as the `modeName` extra. A Mode's name is free text the user typed
//  — the app itself only ever writes the one name a fresh install seeds — so
//  the field was user content, not metadata, and the `isRedactedExtraKey`
//  deny-list (`transcript` / `text` / `prompt`) never matched it.
//
//  This is the macOS twin of the Windows assertion in
//  `app/windows/HyperWhisper.SmokeTests/Program.cs`:
//
//      Assert(!extras.ContainsKey("mode_name"), ...)
//
//  The guards here are EXACT KEY SETS, not substring filters. A substring
//  filter only catches the spelling whoever wrote it thought of — the first
//  version of this file filtered on `contains("modename")`, which `mode_name`
//  (the spelling Windows actually uses, and the spelling the rest of the macOS
//  extras use) walks straight past. An exact set catches every key nobody
//  predicted, including one that carries the name under a different word.
//
//  Both Sentry payloads in the transcription pipeline are covered: the failure
//  `extras`, which ships, and the `slow_transcription_completed` breadcrumb,
//  which does not ship today only because `beforeSend` drops breadcrumbs.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@Suite("Transcription failure extras")
struct TranscriptionFailureExtrasTests {

    // MARK: - Fixtures

    /// A distinct error type so `errorType` has something to report. The name is
    /// invented; no fixture in this file uses a real Mode name.
    private struct FixtureTranscriptionError: Error {}

    /// One realistic failure, so every test below reads the real dictionary.
    ///
    /// The default `audioURL` lives under `/Users/…` on purpose: the builder must
    /// never put the recording path in the payload.
    private static func extras(
        modePreset: String = "custom",
        modeIsSystemProvided: Bool = false,
        audioURL: URL = URL(fileURLWithPath: "/Users/example/Documents/hyperwhisper/recordings/recording-1.wav"),
        httpStatus: Int? = nil
    ) -> [String: Any] {
        TranscriptionPipeline.transcriptionFailureExtras(
            modePreset: modePreset,
            modeIsSystemProvided: modeIsSystemProvided,
            actualProvider: "openai",
            modelString: "cloud",
            useCloud: true,
            language: "auto",
            postProcessingMode: "cloud",
            postProcessingProvider: "hyperwhisper",
            shouldRunPostProcessing: true,
            isHyperwhisperTranscription: false,
            audioURL: audioURL,
            fileExists: true,
            fileReadable: true,
            fileSizeBytes: 65_536,
            error: FixtureTranscriptionError(),
            classification: TranscriptionPipeline.TranscriptionErrorClassification(
                category: "network",
                kind: "url_timedOut",
                retryable: true,
                httpStatus: httpStatus
            ),
            stage: "transcribe",
            stageElapsedMs: 1_200,
            totalElapsedMs: 4_500,
            stageTimeline: ["start=10ms@10ms", "transcribe=1200ms@4500ms (failed)"],
            transcriptionState: "error",
            recordingSessionID: "nil"
        )
    }

    /// The sibling payload: the `slow_transcription_completed` breadcrumb.
    private static func slowBreadcrumb(
        modePreset: String = "custom",
        modeIsSystemProvided: Bool = false
    ) -> [String: Any] {
        TranscriptionPipeline.slowTranscriptionBreadcrumbData(
            modePreset: modePreset,
            modeIsSystemProvided: modeIsSystemProvided,
            actualProvider: "openai",
            language: "auto",
            postProcessingMode: "cloud",
            postProcessingProvider: "hyperwhisper",
            shouldRunPostProcessing: true,
            wasPostProcessed: true,
            isHyperwhisperTranscription: false,
            totalElapsedMs: 21_000,
            effectiveThresholdMs: 18_000,
            baseThresholdMs: 12_000,
            audioDurationSeconds: 40.0,
            durationAllowanceMs: 6_000,
            finalStage: "post_processing",
            stageTimeline: ["start=10ms@10ms", "post_processing=9000ms@21000ms"],
            recordingSessionID: "nil"
        )
    }

    /// `_` and `-` stripped so `mode_name`, `mode-name` and `modeName` all
    /// collapse onto one spelling. The exact-set tests below are the real guard;
    /// this only sharpens the failure message when a name-shaped key appears.
    private static func normalized(_ key: String) -> String {
        key.lowercased()
            .replacingOccurrences(of: "_", with: "")
            .replacingOccurrences(of: "-", with: "")
            .replacingOccurrences(of: " ", with: "")
    }

    /// Every key the failure payload is allowed to carry, `errorHttpStatus`
    /// excluded — that one is conditional and has its own test.
    private static let expectedFailureExtraKeys: Set<String> = [
        "actualProvider",
        "modelString",
        "useCloud",
        "modePreset",
        "modeIsSystemProvided",
        "language",
        "postProcessingMode",
        "postProcessingProvider",
        "shouldRunPostProcessing",
        "isHyperwhisperTranscription",
        "fileExists",
        "fileReadable",
        "fileSizeBytes",
        "fileExtension",
        "usedCAFFallback",
        "errorType",
        "errorDomain",
        "errorCode",
        "errorCategory",
        "errorKind",
        "errorRetryable",
        "errorStage",
        "stageElapsedMs",
        "totalElapsedMs",
        "stageTimeline",
        "transcriptionState",
        "recordingSessionID"
    ]

    /// Every key the `slow_transcription_completed` breadcrumb is allowed to
    /// carry.
    private static let expectedSlowBreadcrumbKeys: Set<String> = [
        "actualProvider",
        "modePreset",
        "modeIsSystemProvided",
        "language",
        "postProcessingMode",
        "postProcessingProvider",
        "shouldRunPostProcessing",
        "wasPostProcessed",
        "isHyperwhisperTranscription",
        "totalElapsedMs",
        "effectiveThresholdMs",
        "baseThresholdMs",
        "audioDurationSeconds",
        "durationAllowanceMs",
        "finalStage",
        "stageTimeline",
        "recordingSessionID"
    ]

    // MARK: - The Mode's name must never be reported

    /// The assertion this file exists for, as an EXACT key set.
    ///
    /// Warning: do not "fix" a failure here by truncating or hashing the name. A
    /// truncated mode name is still the mode name, and a hash is a stable
    /// per-person identifier. Report `modePreset` instead, or report nothing.
    ///
    /// Adding a genuinely new, non-user-content extra is expected to fail this
    /// test once. Add the key to `expectedFailureExtraKeys` in the same change —
    /// that edit is the review prompt this test is for.
    @Test func theFailureExtrasCarryExactlyTheExpectedKeys() {
        let keys = Set(Self.extras().keys)

        #expect(keys == Self.expectedFailureExtraKeys)
    }

    /// The same guard on the breadcrumb. It does not ship today — `beforeSend`
    /// sets `event.breadcrumbs = nil` — which is exactly why it needs a test:
    /// nothing else would notice the name coming back here.
    @Test func theSlowTranscriptionBreadcrumbCarriesExactlyTheExpectedKeys() {
        let keys = Set(Self.slowBreadcrumb().keys)

        #expect(keys == Self.expectedSlowBreadcrumbKeys)
    }

    /// A second net, spelling-insensitive, for the clearer failure message.
    /// `modeName`, `mode_name` and `mode-name` all trip it.
    @Test func noPayloadCarriesANameShapedKey() {
        for payload in [Self.extras(), Self.slowBreadcrumb()] {
            #expect(payload["modeName"] == nil)
            #expect(payload["mode_name"] == nil)

            let nameShaped = payload.keys.filter { Self.normalized($0).contains("modename") }
            #expect(nameShaped.isEmpty, "no entry may carry the Mode's name")
        }
    }

    /// The replacement field, and the question it answers: which KIND of Mode
    /// was running, and was it one the app seeded or one the person made.
    @Test func modePresetAndSystemProvidedAreReported() {
        let extras = Self.extras(modePreset: "custom", modeIsSystemProvided: false)

        #expect(extras["modePreset"] as? String == "custom")
        #expect(extras["modeIsSystemProvided"] as? Bool == false)

        let seeded = Self.extras(modePreset: "hyper", modeIsSystemProvided: true)
        #expect(seeded["modePreset"] as? String == "hyper")
        #expect(seeded["modeIsSystemProvided"] as? Bool == true)

        let breadcrumb = Self.slowBreadcrumb(modePreset: "hyper", modeIsSystemProvided: true)
        #expect(breadcrumb["modePreset"] as? String == "hyper")
        #expect(breadcrumb["modeIsSystemProvided"] as? Bool == true)
    }

    // MARK: - The reported preset is a closed set (issue #795, round 1)

    /// `Mode.preset` is an unvalidated `String` column: `hw-localapi` checks
    /// only its length, `PATCH /modes` assigns the body raw, and a restored v2
    /// backup writes whatever the file held. So the raw column is free text too,
    /// and reporting it unchecked would have swapped one user-content field for
    /// another. Every call site maps it through `PresetType.reportingValue`
    /// first, and this is the test of that map.
    @Test func aKnownPresetIsReportedAsItself() {
        for preset in PresetType.allCases {
            #expect(PresetType.reportingValue(forRawPreset: preset.rawValue) == preset.rawValue)
        }
    }

    /// The value below is invented for this test and is not a Mode name from
    /// anywhere; it stands in for anything a person could type into the column
    /// through the Local API or a hand-edited backup.
    @Test func anUnknownPresetIsReportedAsTheUnrecognizedSentinel() {
        let notAPreset = "zzz-not-a-preset-value"

        #expect(PresetType(rawValue: notAPreset) == nil)
        #expect(PresetType.reportingValue(forRawPreset: notAPreset)
                == PresetType.unrecognizedPresetReportingValue)
        #expect(PresetType.reportingValue(forRawPreset: nil)
                == PresetType.unrecognizedPresetReportingValue)
        #expect(PresetType.reportingValue(forRawPreset: "")
                == PresetType.unrecognizedPresetReportingValue)
        // Case matters: the raw values are lowercase, and a near-miss is still a
        // value that did not come from this app.
        #expect(PresetType.reportingValue(forRawPreset: "Hyper")
                == PresetType.unrecognizedPresetReportingValue)
    }

    /// Three conditions, three distinct reported values. "No Mode was resolved"
    /// and "the Mode's preset is not a known preset" are different facts, and a
    /// triager reading a Sentry event has nothing else to tell them apart.
    ///
    /// Neither sentinel may collide with a real preset — `custom` in particular,
    /// which is the value a reviewer suggested for the unknown case and which
    /// would have made the report state something untrue.
    @Test func theTwoSentinelsAreDistinctFromEachOtherAndFromEveryPreset() {
        let presets = Set(PresetType.allCases.map(\.rawValue))

        #expect(PresetType.noModeReportingValue != PresetType.unrecognizedPresetReportingValue)
        #expect(presets.contains(PresetType.noModeReportingValue) == false)
        #expect(presets.contains(PresetType.unrecognizedPresetReportingValue) == false)
        #expect(presets.contains("custom"))
    }

    /// The `Mode?` overload, against real Core Data rows, because a nil Mode and
    /// a Mode holding an unknown preset must NOT collapse onto the same value —
    /// and `mode?.preset` flattens the two optionals, which is how they would.
    ///
    /// The controller is held for the length of the test on purpose: a store
    /// dropped inside a helper deallocates on return, the `Mode` faults, and
    /// every attribute reads back as its zero value. Same trap as
    /// `DefaultModeInvariantTests`.
    @MainActor
    @Test func aNilModeAndAnUnknownPresetReportDifferently() {
        let persistence = PersistenceController(inMemory: true)

        let known = Mode(context: persistence.container.viewContext)
        known.id = UUID()
        known.preset = "meeting"

        let unknown = Mode(context: persistence.container.viewContext)
        unknown.id = UUID()
        unknown.preset = "zzz-not-a-preset-value"

        #expect(PresetType.reportingValue(for: known) == "meeting")
        #expect(PresetType.reportingValue(for: unknown)
                == PresetType.unrecognizedPresetReportingValue)
        #expect(PresetType.reportingValue(for: nil) == PresetType.noModeReportingValue)

        // The free text that went into the column must not come back out.
        #expect(PresetType.reportingValue(for: unknown) != "zzz-not-a-preset-value")
    }

    /// The hole the map on its own leaves: nothing stops a LATER call site from
    /// passing `mode?.preset` straight in, and no unit test can reach those call
    /// sites (they sit inside a `catch` in a `@MainActor` pipeline that needs a
    /// provider, an audio file and a Core Data stack). So both builders re-check
    /// the value at the boundary, and this is the test of that re-check.
    @Test func aPayloadBuiltFromARawPresetStillReportsTheClosedSet() {
        let freeText = "zzz-whatever-a-person-typed"

        #expect(Self.extras(modePreset: freeText)["modePreset"] as? String
                == PresetType.unrecognizedPresetReportingValue)
        #expect(Self.slowBreadcrumb(modePreset: freeText)["modePreset"] as? String
                == PresetType.unrecognizedPresetReportingValue)

        // The old fallback strings are not in the closed set either, so a
        // regression to `mode?.preset ?? "unknown"` also collapses.
        #expect(Self.extras(modePreset: "unknown")["modePreset"] as? String
                == PresetType.unrecognizedPresetReportingValue)
        #expect(Self.extras(modePreset: "nil")["modePreset"] as? String
                == PresetType.unrecognizedPresetReportingValue)

        // Everything the map is allowed to produce passes through untouched.
        for allowed in PresetType.reportableValues {
            #expect(Self.extras(modePreset: allowed)["modePreset"] as? String == allowed)
            #expect(Self.slowBreadcrumb(modePreset: allowed)["modePreset"] as? String == allowed)
        }
    }

    /// `reportableValues` is what the boundary check tests against, so it has to
    /// stay in step with the enum and the two sentinels.
    @Test func theReportableSetIsTheSevenPresetsPlusTheTwoSentinels() {
        #expect(PresetType.reportableValues.count == PresetType.allCases.count + 2)
        for preset in PresetType.allCases {
            #expect(PresetType.reportableValues.contains(preset.rawValue))
        }
        #expect(PresetType.reportableValues.contains(PresetType.noModeReportingValue))
        #expect(PresetType.reportableValues.contains(PresetType.unrecognizedPresetReportingValue))
        #expect(PresetType.sanitizedReportingValue("zzz-not-a-preset-value")
                == PresetType.unrecognizedPresetReportingValue)
    }

    // MARK: - The recording path stays out (HYPERWHISPER-T2)

    /// The recording path carries the account name, so it is absent by design.
    /// `fileExtension` (plus `fileExists` / `fileReadable` / `fileSizeBytes`)
    /// answers every question the path was there to answer.
    @Test func theRecordingPathIsAbsentButTheExtensionIsPresent() {
        let extras = Self.extras()

        #expect(extras["fileExtension"] as? String == "wav")
        #expect(extras["fileExists"] as? Bool == true)
        #expect(extras["fileReadable"] as? Bool == true)
        #expect(extras["fileSizeBytes"] as? Int64 == 65_536)
        #expect(extras["usedCAFFallback"] as? Bool == false)

        let leaked = extras.filter { String(describing: $0.value).contains("/Users/") }
        #expect(leaked.isEmpty, "no extra may carry the recording path")
    }

    /// The CAF fallback flag is derived from the extension, not from the path.
    @Test func theCAFFallbackIsFlaggedFromTheExtension() {
        let extras = Self.extras(
            audioURL: URL(fileURLWithPath: "/Users/example/Documents/hyperwhisper/recordings/recording-1.CAF")
        )

        #expect(extras["usedCAFFallback"] as? Bool == true)
        let leaked = extras.filter { String(describing: $0.value).contains("/Users/") }
        #expect(leaked.isEmpty, "no extra may carry the recording path")
    }

    // MARK: - The rest of the payload survived the lift out of the catch block

    /// The HTTP status is conditional: present when the classification has one,
    /// absent otherwise. This was a separate write on the dictionary before the
    /// builder existed, and it is the easiest part of the lift to drop.
    @Test func theHTTPStatusIsReportedOnlyWhenTheClassificationHasOne() {
        #expect(Self.extras(httpStatus: nil)["errorHttpStatus"] == nil)
        #expect(Self.extras(httpStatus: 429)["errorHttpStatus"] as? Int == 429)
        #expect(Set(Self.extras(httpStatus: 429).keys)
                == Self.expectedFailureExtraKeys.union(["errorHttpStatus"]))
    }

    /// The provider/error axes the Sentry issues are triaged on.
    @Test func theProviderAndErrorAxesAreReported() {
        let extras = Self.extras()

        #expect(extras["actualProvider"] as? String == "openai")
        #expect(extras["modelString"] as? String == "cloud")
        #expect(extras["useCloud"] as? Bool == true)
        #expect(extras["errorCategory"] as? String == "network")
        #expect(extras["errorKind"] as? String == "url_timedOut")
        #expect(extras["errorRetryable"] as? Bool == true)
        #expect(extras["errorStage"] as? String == "transcribe")
        // Matched as a substring: `String(describing:)` on a metatype may or may
        // not qualify a nested type, and that is not what this test is about.
        let errorType = extras["errorType"] as? String
        #expect(errorType?.contains("FixtureTranscriptionError") == true)
        #expect(extras["totalElapsedMs"] as? Int == 4_500)
        #expect(extras["stageElapsedMs"] as? Int == 1_200)
    }

    /// The breadcrumb's own numbers, so the lift out of the call site cannot
    /// quietly drop one.
    @Test func theSlowTranscriptionBreadcrumbReportsItsThresholds() {
        let breadcrumb = Self.slowBreadcrumb()

        #expect(breadcrumb["totalElapsedMs"] as? Int == 21_000)
        #expect(breadcrumb["effectiveThresholdMs"] as? Int == 18_000)
        #expect(breadcrumb["baseThresholdMs"] as? Int == 12_000)
        #expect(breadcrumb["durationAllowanceMs"] as? Int == 6_000)
        #expect(breadcrumb["audioDurationSeconds"] as? Double == 40.0)
        #expect(breadcrumb["finalStage"] as? String == "post_processing")
        #expect(breadcrumb["wasPostProcessed"] as? Bool == true)
    }
}
