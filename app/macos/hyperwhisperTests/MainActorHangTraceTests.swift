//
//  MainActorHangTraceTests.swift
//  hyperwhisperTests
//
//  Pins the complete, privacy-safe Sentry scope contract for synchronous
//  main-actor boundaries.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct MainActorHangTraceTests {
    private static let expectedKeys: Set<String> = [
        "main_actor_flow_state",
        "main_actor_flow_flow",
        "main_actor_flow_step",
        "main_actor_flow_operation_id",
        "main_actor_flow_started_at_ms",
        "main_actor_flow_completed_at_ms",
        "main_actor_flow_elapsed_ms",
        "main_actor_flow_last_completed_flow",
        "main_actor_flow_last_completed_step",
        "main_actor_flow_last_completed_operation_id",
        "main_actor_flow_last_completed_started_at_ms",
        "main_actor_flow_last_completed_at_ms",
        "main_actor_flow_last_completed_elapsed_ms",
        "main_actor_ui_surface",
        "main_actor_ui_transition",
        "main_actor_ui_requested_at_ms"
    ]

    private enum TestError: Error, Equatable {
        case expected
    }

    @Test func everyPublishCarriesTheSameCompleteKeySet() {
        var payloads: [[String: Any]] = []
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) }
        )

        trace.withActive(flow: .recordingWindow, step: .open) {}
        trace.withActive(flow: .autoPaste, step: .sendPasteCommand) {}

        #expect(payloads.count == 4)
        for payload in payloads {
            #expect(Set(payload.keys) == Self.expectedKeys)
        }
    }

    @Test func disabledErrorLoggingPublishesNoScopeExtras() {
        var payloads: [[String: Any]] = []
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { false },
            publisher: { payloads.append($0) }
        )

        trace.withActive(flow: .recordingWindow, step: .close) {}
        trace.recordUIUpdateRequest(surface: .onboarding, transition: .present)

        #expect(payloads.isEmpty)
    }

    @Test func UIRequestDefaultsAreExplicitBeforeTheFirstRequest() {
        let trace = MainActorHangTrace()
        let payload = trace.currentPayload()

        #expect(payload["main_actor_ui_surface"] as? String == "none")
        #expect(payload["main_actor_ui_transition"] as? String == "none")
        #expect(payload["main_actor_ui_requested_at_ms"] as? Int64 == 0)
        #expect(Set(payload.keys) == Self.expectedKeys)
    }

    @Test func aLaterUIRequestReplacesAllFieldsFromTheEarlierRequest() {
        var payloads: [[String: Any]] = []
        var nowValues: [Int64] = [100, 250]
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) },
            nowMs: { nowValues.removeFirst() }
        )

        trace.recordUIUpdateRequest(surface: .mainWindow, transition: .navigateHistory)
        trace.recordUIUpdateRequest(surface: .streamingPreview, transition: .show)

        #expect(payloads.count == 2)
        #expect(payloads[0]["main_actor_ui_surface"] as? String == "main_window")
        #expect(payloads[0]["main_actor_ui_transition"] as? String == "navigate_history")
        #expect(payloads[0]["main_actor_ui_requested_at_ms"] as? Int64 == 100)
        #expect(payloads[1]["main_actor_ui_surface"] as? String == "streaming_preview")
        #expect(payloads[1]["main_actor_ui_transition"] as? String == "show")
        #expect(payloads[1]["main_actor_ui_requested_at_ms"] as? Int64 == 250)
    }

    @Test func activeToIdleBoundaryPublishesPreserveTheLastUIRequest() {
        var payloads: [[String: Any]] = []
        var nowValues: [Int64] = [700, 1_000, 1_020]
        var uptimeValues: [UInt64] = [10_000_000, 12_000_000, 20_000_000]
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) },
            nowMs: { nowValues.removeFirst() },
            uptimeNs: { uptimeValues.removeFirst() }
        )

        trace.recordUIUpdateRequest(surface: .recordingDialog, transition: .recordingProcessing)
        trace.withActive(flow: .recordingWindow, step: .open) {}

        #expect(payloads.count == 3)
        for payload in payloads {
            #expect(payload["main_actor_ui_surface"] as? String == "recording_dialog")
            #expect(payload["main_actor_ui_transition"] as? String == "recording_processing")
            #expect(payload["main_actor_ui_requested_at_ms"] as? Int64 == 700)
        }
        #expect(payloads[1]["main_actor_flow_state"] as? String == "active")
        #expect(payloads[2]["main_actor_flow_state"] as? String == "idle")
    }

    @Test func operationPublishesActiveThenIdleWithMeasurementsAndUniqueIds() {
        var payloads: [[String: Any]] = []
        var nowValues: [Int64] = [1_000, 1_020, 2_000, 2_030]
        var uptimeValues: [UInt64] = [
            10_000_000, 12_000_000, 20_000_000,
            30_000_000, 35_000_000, 60_000_000
        ]
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) },
            nowMs: { nowValues.removeFirst() },
            uptimeNs: { uptimeValues.removeFirst() }
        )

        trace.withActive(flow: .recordingWindow, step: .focusForInteraction) {}
        trace.withActive(flow: .autoPaste, step: .inspectFocusedElement) {}

        let firstActive = payloads[0]
        let firstIdle = payloads[1]
        let secondActive = payloads[2]
        let secondIdle = payloads[3]
        let firstId = firstActive["main_actor_flow_operation_id"] as? String
        let secondId = secondActive["main_actor_flow_operation_id"] as? String

        #expect(firstActive["main_actor_flow_state"] as? String == "active")
        #expect(firstActive["main_actor_flow_flow"] as? String == "recording_window")
        #expect(firstActive["main_actor_flow_step"] as? String == "focus_for_interaction")
        #expect(firstActive["main_actor_flow_started_at_ms"] as? Int64 == 1_000)
        #expect(firstActive["main_actor_flow_elapsed_ms"] as? Int == 2)
        #expect(firstIdle["main_actor_flow_state"] as? String == "idle")
        #expect(firstIdle["main_actor_flow_last_completed_at_ms"] as? Int64 == 1_020)
        #expect(firstIdle["main_actor_flow_last_completed_elapsed_ms"] as? Int == 10)
        #expect(secondIdle["main_actor_flow_last_completed_at_ms"] as? Int64 == 2_030)
        #expect(secondIdle["main_actor_flow_last_completed_elapsed_ms"] as? Int == 30)
        #expect(firstId?.isEmpty == false)
        #expect(secondId?.isEmpty == false)
        #expect(firstId != secondId)
    }

    /// `SentryService.setExtras` cannot remove keys. Each idle publish must
    /// overwrite every active-only field, or a later hang can inherit stale
    /// data from a completed boundary.
    @Test func idlePayloadOverwritesAllStaleActiveFields() {
        var payloads: [[String: Any]] = []
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) }
        )

        trace.withActive(flow: .autoPaste, step: .activateTarget) {}

        let active = payloads[0]
        let idle = payloads[1]
        #expect(active["main_actor_flow_flow"] as? String == "auto_paste")
        #expect(active["main_actor_flow_step"] as? String == "activate_target")
        #expect(active["main_actor_flow_operation_id"] as? String != "none")
        #expect(idle["main_actor_flow_state"] as? String == "idle")
        #expect(idle["main_actor_flow_flow"] as? String == "none")
        #expect(idle["main_actor_flow_step"] as? String == "none")
        #expect(idle["main_actor_flow_operation_id"] as? String == "none")
        let activeOnlyNumericKeys: Set<String> = [
            "main_actor_flow_started_at_ms",
            "main_actor_flow_completed_at_ms",
            "main_actor_flow_elapsed_ms"
        ]
        let clearedNumericFields = NSDictionary(
            dictionary: idle.filter { activeOnlyNumericKeys.contains($0.key) }
        )
        let expectedClearedNumericFields = NSDictionary(
            dictionary: Dictionary(uniqueKeysWithValues: activeOnlyNumericKeys.map { ($0, 0) })
        )
        #expect(clearedNumericFields == expectedClearedNumericFields)
        #expect(Set(idle.keys) == Set(active.keys))
    }

    @Test func nestedWrapperRestoresParentBeforePublishingIdle() {
        var payloads: [[String: Any]] = []
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) }
        )

        trace.withActive(flow: .recordingWindow, step: .open) {
            trace.withActive(flow: .recordingWindow, step: .close) {}
        }

        #expect(payloads.count == 4)
        #expect(payloads[0]["main_actor_flow_step"] as? String == "open")
        #expect(payloads[1]["main_actor_flow_step"] as? String == "close")
        #expect(payloads[2]["main_actor_flow_state"] as? String == "active")
        #expect(payloads[2]["main_actor_flow_step"] as? String == "open")
        #expect(payloads[2]["main_actor_flow_last_completed_step"] as? String == "close")
        #expect(payloads[3]["main_actor_flow_state"] as? String == "idle")
        #expect(payloads[3]["main_actor_flow_last_completed_step"] as? String == "open")
    }

    @Test func thrownErrorLeavesTraceIdleAndPropagatesUnchanged() {
        var payloads: [[String: Any]] = []
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) }
        )

        #expect(throws: TestError.expected) {
            try trace.withActive(flow: .autoPaste, step: .resolveTarget) {
                throw TestError.expected
            }
        }

        #expect(payloads.last?["main_actor_flow_state"] as? String == "idle")
        #expect(trace.currentPayload()["main_actor_flow_state"] as? String == "idle")
    }

    @Test func enumSlugsAreUniqueLowerSnakeCaseValues() {
        let allowed = Set("abcdefghijklmnopqrstuvwxyz0123456789_")
        let flowSlugs = MainActorHangFlow.allCases.map(\.rawValue)
        let stepSlugs = MainActorHangStep.allCases.map(\.rawValue)
        let surfaceSlugs = MainActorUISurface.allCases.map(\.rawValue)
        let transitionSlugs = MainActorUITransition.allCases.map(\.rawValue)

        for slug in flowSlugs + stepSlugs + surfaceSlugs + transitionSlugs {
            #expect(!slug.isEmpty)
            #expect(slug.allSatisfy { allowed.contains($0) })
        }
        #expect(Set(flowSlugs).count == flowSlugs.count)
        #expect(Set(stepSlugs).count == stepSlugs.count)
        #expect(Set(surfaceSlugs).count == surfaceSlugs.count)
        #expect(Set(transitionSlugs).count == transitionSlugs.count)
    }

    @Test func everySelectedAppStatePropertyRecordsFromWillSet() throws {
        let expectedSurfaces = [
            "selectedNavigationItem": ".mainWindow",
            "recordingState": ".recordingDialog",
            "showRecordingDialog": ".recordingDialog",
            "showCancelConfirmation": ".cancelConfirmation",
            "showOnboarding": ".onboarding",
            "streamingConnectionState": ".streamingConnection",
            "showStreamingPreview": ".streamingPreview"
        ]

        for (property, surface) in expectedSurfaces {
            let declaration = try Self.appStatePublishedProperty(named: property)
            #expect(declaration.contains("willSet"), "\(property) must trace before @Published invalidates SwiftUI")
            #expect(declaration.contains("MainActorHangTrace.shared.recordUIUpdateRequest"))
            #expect(declaration.contains("surface: \(surface)"))
        }
    }

    @Test func associatedRecordingAndStreamingErrorTextNeverEntersTheMapping() throws {
        let recording = try Self.appStatePublishedProperty(named: "recordingState")
        let streaming = try Self.appStatePublishedProperty(named: "streamingConnectionState")

        #expect(recording.contains("case .complete: transition = .recordingComplete"))
        #expect(recording.contains("case .error: transition = .recordingError"))
        #expect(!recording.contains(".complete(let"))
        #expect(!recording.contains(".error(let"))
        #expect(streaming.contains("case .error: transition = .streamingError"))
        #expect(!streaming.contains(".error(let"))
    }

    private static func appStatePublishedProperty(named name: String) throws -> String {
        let source = try ProductionSource.code(of: "app/macos/hyperwhisper/Models/AppState.swift")
        let marker = "@Published var \(name)"
        guard let start = source.range(of: marker) else {
            throw ProductionSource.Failure.anchorNotFound(anchor: marker, file: "AppState.swift")
        }
        let remainder = source[start.lowerBound...]
        guard let next = remainder.dropFirst(marker.count).range(of: "\n    @Published var ") else {
            return String(remainder)
        }
        return String(remainder[..<next.lowerBound])
    }
}
