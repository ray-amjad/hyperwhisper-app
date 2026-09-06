//
//  MainActorHangTraceTests.swift
//  hyperwhisperTests
//
//  Pins the complete, privacy-safe Sentry scope contract for synchronous
//  main-actor boundaries.
//

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
        "main_actor_flow_last_completed_elapsed_ms"
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

        #expect(payloads.isEmpty)
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
        #expect(idle["main_actor_flow_started_at_ms"] as? Int64 == 0)
        #expect((idle["main_actor_flow_completed_at_ms"] as? NSNumber)?.int64Value == 0)
        #expect(idle["main_actor_flow_elapsed_ms"] as? Int == 0)
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

        for slug in flowSlugs + stepSlugs {
            #expect(!slug.isEmpty)
            #expect(slug.allSatisfy { allowed.contains($0) })
        }
        #expect(Set(flowSlugs).count == flowSlugs.count)
        #expect(Set(stepSlugs).count == stepSlugs.count)
        #expect(Set(flowSlugs + stepSlugs).count == flowSlugs.count + stepSlugs.count)
    }
}
