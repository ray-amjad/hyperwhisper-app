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
        "main_actor_ui_properties",
        "main_actor_ui_states",
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
        trace.recordUIUpdateRequest(property: .showOnboarding, state: .booleanTrue)

        #expect(payloads.isEmpty)
        #expect(trace.currentPayload()["main_actor_ui_properties"] as? [String] == [])
    }

    @Test func enablingErrorLoggingDoesNotPublishPreConsentUIWrites() {
        var isEnabled = false
        var payloads: [[String: Any]] = []
        var nowValues: [Int64] = [200]
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { isEnabled },
            publisher: { payloads.append($0) },
            nowMs: { nowValues.removeFirst() }
        )

        trace.recordUIUpdateRequest(property: .showOnboarding, state: .booleanTrue)
        isEnabled = true
        trace.recordUIUpdateRequest(property: .recordingState, state: .recording)

        #expect(payloads.count == 1)
        #expect(payloads[0]["main_actor_ui_properties"] as? [String] == ["recording_state"])
        #expect(payloads[0]["main_actor_ui_states"] as? [String] == ["recording"])
        #expect(payloads[0]["main_actor_ui_requested_at_ms"] as? [Int64] == [200])
    }

    @Test func disabledErrorLoggingClearsPreviouslyRetainedUIWrites() {
        var isEnabled = true
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { isEnabled },
            publisher: { _ in }
        )

        trace.recordUIUpdateRequest(property: .showOnboarding, state: .booleanTrue)
        isEnabled = false
        trace.recordUIUpdateRequest(property: .recordingState, state: .recording)

        #expect(trace.currentPayload()["main_actor_ui_properties"] as? [String] == [])
    }

    @Test func UIRequestDefaultsAreExplicitBeforeTheFirstRequest() {
        let trace = MainActorHangTrace()
        let payload = trace.currentPayload()

        #expect(payload["main_actor_ui_properties"] as? [String] == [])
        #expect(payload["main_actor_ui_states"] as? [String] == [])
        #expect(payload["main_actor_ui_requested_at_ms"] as? [Int64] == [])
        #expect(Set(payload.keys) == Self.expectedKeys)
    }

    @Test func backToBackUIWritesRemainInOrder() {
        var payloads: [[String: Any]] = []
        var nowValues: [Int64] = [100, 250]
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) },
            nowMs: { nowValues.removeFirst() }
        )

        trace.recordUIUpdateRequest(property: .selectedNavigationItem, state: .history)
        trace.recordUIUpdateRequest(property: .showStreamingPreview, state: .booleanTrue)

        #expect(payloads.count == 2)
        #expect(payloads[0]["main_actor_ui_properties"] as? [String] == ["selected_navigation_item"])
        #expect(payloads[0]["main_actor_ui_states"] as? [String] == ["history"])
        #expect(payloads[0]["main_actor_ui_requested_at_ms"] as? [Int64] == [100])
        #expect(payloads[1]["main_actor_ui_properties"] as? [String] == [
            "selected_navigation_item", "show_streaming_preview"
        ])
        #expect(payloads[1]["main_actor_ui_states"] as? [String] == [
            "history", "true"
        ])
        #expect(payloads[1]["main_actor_ui_requested_at_ms"] as? [Int64] == [100, 250])
    }

    @Test func latestPerPropertySnapshotCannotBeEvictedByAnotherPropertyBurst() {
        var nowMs: Int64 = 0
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { _ in },
            nowMs: {
                nowMs += 1
                return nowMs
            }
        )

        trace.recordUIUpdateRequest(property: .showOnboarding, state: .booleanTrue)
        for _ in 0..<20 {
            trace.recordUIUpdateRequest(property: .recordingState, state: .processing)
        }

        let payload = trace.currentPayload()
        #expect(payload["main_actor_ui_properties"] as? [String] == [
            "recording_state", "show_onboarding"
        ])
        #expect(payload["main_actor_ui_states"] as? [String] == ["processing", "true"])
        #expect(payload["main_actor_ui_requested_at_ms"] as? [Int64] == [21, 1])
    }

    @Test func activeToIdleBoundaryPublishesPreserveTheUIWriteSequence() {
        var payloads: [[String: Any]] = []
        var nowValues: [Int64] = [700, 1_000, 1_020]
        var uptimeValues: [UInt64] = [10_000_000, 12_000_000, 20_000_000]
        let trace = MainActorHangTrace(
            errorLoggingEnabled: { true },
            publisher: { payloads.append($0) },
            nowMs: { nowValues.removeFirst() },
            uptimeNs: { uptimeValues.removeFirst() }
        )

        trace.recordUIUpdateRequest(property: .recordingState, state: .processing)
        trace.withActive(flow: .recordingWindow, step: .open) {}

        #expect(payloads.count == 3)
        for payload in payloads {
            #expect(payload["main_actor_ui_properties"] as? [String] == ["recording_state"])
            #expect(payload["main_actor_ui_states"] as? [String] == ["processing"])
            #expect(payload["main_actor_ui_requested_at_ms"] as? [Int64] == [700])
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
        let propertySlugs = MainActorUIProperty.allCases.map(\.rawValue)
        let stateSlugs = MainActorUIState.allCases.map(\.rawValue)

        for slug in flowSlugs + stepSlugs + propertySlugs + stateSlugs {
            #expect(!slug.isEmpty)
            #expect(slug.allSatisfy { allowed.contains($0) })
        }
        #expect(Set(flowSlugs).count == flowSlugs.count)
        #expect(Set(stepSlugs).count == stepSlugs.count)
        #expect(Set(propertySlugs).count == propertySlugs.count)
        #expect(Set(stateSlugs).count == stateSlugs.count)
        let allSlugs = flowSlugs + stepSlugs + propertySlugs + stateSlugs
        #expect(Set(allSlugs).count == allSlugs.count)
    }

    @Test func everySelectedAppStatePropertyRecordsFromWillSet() throws {
        let expectedProperties = [
            "selectedNavigationItem": ".selectedNavigationItem",
            "recordingState": ".recordingState",
            "showRecordingDialog": ".showRecordingDialog",
            "showCancelConfirmation": ".showCancelConfirmation",
            "showOnboarding": ".showOnboarding",
            "streamingConnectionState": ".streamingConnectionState",
            "showStreamingPreview": ".showStreamingPreview"
        ]

        for (property, propertySlug) in expectedProperties {
            let declaration = try Self.appStatePublishedProperty(named: property)
            #expect(declaration.contains("willSet"), "\(property) must trace before @Published invalidates SwiftUI")
            #expect(declaration.contains("MainActorHangTrace.shared.recordUIUpdateRequest"))
            #expect(declaration.contains("property: \(propertySlug)"))
        }
    }

    @Test func everyNavigationCaseMapsToItsDiagnosticState() {
        let mappings: [(NavigationItem, MainActorUIState)] = [
            (.home, .home),
            (.modes, .modes),
            (.vocabulary, .vocabulary),
            (.modelLibrary, .modelLibrary),
            (.streaming, .streaming),
            (.history, .history),
            (.settings, .settings)
        ]

        #expect(mappings.map(\.0) == NavigationItem.allCases)
        for (value, expected) in mappings {
            #expect(value.mainActorUIState == expected)
        }
    }

    @Test func everyRecordingCaseMapsWithoutAssociatedUserText() {
        let mappings: [(RecordingState, MainActorUIState)] = [
            (.idle, .idle),
            (.recording, .recording),
            (.processing, .processing),
            (.transcribing, .transcribing),
            (.postProcessing, .postProcessing),
            (.complete(""), .complete),
            (.error(""), .error)
        ]

        for (value, expected) in mappings {
            #expect(value.mainActorUIState == expected)
        }
    }

    @Test func everyStreamingCaseUsesTheSharedDiagnosticMapping() {
        let mappings: [(StreamingConnectionState, MainActorUIState)] = [
            (.idle, .idle),
            (.warmingUp, .warmingUp),
            (.connecting, .connecting),
            (.ready, .ready),
            (.streaming, .streaming),
            (.reconnecting, .reconnecting),
            (.disconnecting, .disconnecting),
            (.error(""), .error)
        ]

        for (value, expected) in mappings {
            #expect(value.mainActorUIState == expected)
        }
    }

    @Test func booleanPropertiesUseOneSharedStatePair() throws {
        let booleanProperties = [
            "showRecordingDialog",
            "showCancelConfirmation",
            "showOnboarding",
            "showStreamingPreview"
        ]

        for property in booleanProperties {
            let declaration = try Self.appStatePublishedProperty(named: property)
            #expect(declaration.contains("newValue ? .booleanTrue : .booleanFalse"))
        }
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
