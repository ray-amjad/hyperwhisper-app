//
//  MainActorHangTrace.swift
//  hyperwhisper
//
//  Truthful scope diagnostics for synchronous system boundaries on the main actor.
//
//  PRIVACY: every published value is a fixed slug, an app-generated UUID, or a
//  time measurement. No user content, target identity, path, or error is recorded.
//

import Foundation
import Dispatch

enum MainActorHangFlow: String, CaseIterable {
    case recordingWindow = "recording_window"
    case autoPaste = "auto_paste"
}

enum MainActorHangStep: String, CaseIterable {
    case open
    case close
    case focusForInteraction = "focus_for_interaction"
    case restorePreviousFocus = "restore_previous_focus"
    case resolveTarget = "resolve_target"
    case activateTarget = "activate_target"
    case inspectFrontmostApp = "inspect_frontmost_app"
    case inspectFocusedElement = "inspect_focused_element"
    case sendPasteCommand = "send_paste_command"
}

enum MainActorUIProperty: String, CaseIterable {
    case selectedNavigationItem = "selected_navigation_item"
    case recordingState = "recording_state"
    case showRecordingDialog = "show_recording_dialog"
    case showCancelConfirmation = "show_cancel_confirmation"
    case showOnboarding = "show_onboarding"
    case streamingConnectionState = "streaming_connection_state"
    case showStreamingPreview = "show_streaming_preview"
}

enum MainActorUIState: String, CaseIterable {
    case home
    case modes
    case vocabulary
    case modelLibrary = "model_library"
    case streaming
    case history
    case settings
    case idle
    case recording
    case processing
    case transcribing
    case postProcessing = "post_processing"
    case complete
    case error
    case warmingUp = "warming_up"
    case connecting
    case ready
    case reconnecting
    case disconnecting
    case booleanTrue = "true"
    case booleanFalse = "false"
}

extension NavigationItem {
    var mainActorUIState: MainActorUIState {
        switch self {
        case .home: return .home
        case .modes: return .modes
        case .vocabulary: return .vocabulary
        case .modelLibrary: return .modelLibrary
        case .streaming: return .streaming
        case .history: return .history
        case .settings: return .settings
        }
    }
}

extension RecordingState {
    var mainActorUIState: MainActorUIState {
        switch self {
        case .idle: return .idle
        case .recording: return .recording
        case .processing: return .processing
        case .transcribing: return .transcribing
        case .postProcessing: return .postProcessing
        case .complete: return .complete
        case .error: return .error
        }
    }
}

extension StreamingConnectionState {
    var mainActorUIState: MainActorUIState {
        switch self {
        case .idle: return .idle
        case .warmingUp: return .warmingUp
        case .connecting: return .connecting
        case .ready: return .ready
        case .streaming: return .streaming
        case .reconnecting: return .reconnecting
        case .disconnecting: return .disconnecting
        case .error: return .error
        }
    }
}

@MainActor
final class MainActorHangTrace {
    static let shared = MainActorHangTrace()

    static let keyPrefix = "main_actor_flow_"
    static let stateActive = "active"
    static let stateIdle = "idle"
    static let slugNone = "none"

    typealias ExtrasPublisher = ([String: Any]) -> Void

    private struct Frame {
        let flow: MainActorHangFlow
        let step: MainActorHangStep
        let operationId: String
        let startedAtMs: Int64
        let startedAtUptimeNs: UInt64
    }

    private struct CompletedFrame {
        let flow: MainActorHangFlow
        let step: MainActorHangStep
        let operationId: String
        let startedAtMs: Int64
        let completedAtMs: Int64
        let elapsedMs: Int
    }

    private struct UIUpdateRequest {
        let property: MainActorUIProperty
        let state: MainActorUIState
        let requestedAtMs: Int64
    }

    private let errorLoggingEnabled: () -> Bool
    private let publisher: ExtrasPublisher
    private let nowMs: () -> Int64
    private let uptimeNs: () -> UInt64
    private var frames: [Frame] = []
    private var lastCompleted: CompletedFrame?
    // Keep the latest write for every traced property. A burst from one
    // property cannot evict the last transition from another property.
    private var latestUIUpdateRequests: [MainActorUIProperty: UIUpdateRequest] = [:]

    init(
        errorLoggingEnabled: @escaping () -> Bool = { AppLogger.isErrorLoggingEnabled },
        publisher: @escaping ExtrasPublisher = { SentryService.setExtras($0) },
        nowMs: @escaping () -> Int64 = { Int64((Date().timeIntervalSince1970 * 1_000).rounded()) },
        uptimeNs: @escaping () -> UInt64 = { DispatchTime.now().uptimeNanoseconds }
    ) {
        self.errorLoggingEnabled = errorLoggingEnabled
        self.publisher = publisher
        self.nowMs = nowMs
        self.uptimeNs = uptimeNs
    }

    /// Marks only the synchronous duration of `operation` as active. A nested
    /// boundary restores its parent frame when it completes.
    @discardableResult
    func withActive<Result>(
        flow: MainActorHangFlow,
        step: MainActorHangStep,
        _ operation: () throws -> Result
    ) rethrows -> Result {
        let frame = Frame(
            flow: flow,
            step: step,
            operationId: UUID().uuidString,
            startedAtMs: nowMs(),
            startedAtUptimeNs: uptimeNs()
        )
        frames.append(frame)
        logEntry(flow: flow, step: step)
        publishCurrentState()

        defer {
            let completedAtMs = nowMs()
            let elapsedMs = Self.elapsedMs(from: frame.startedAtUptimeNs, to: uptimeNs())
            if let index = frames.lastIndex(where: { $0.operationId == frame.operationId }) {
                frames.remove(at: index)
            }
            lastCompleted = CompletedFrame(
                flow: frame.flow,
                step: frame.step,
                operationId: frame.operationId,
                startedAtMs: frame.startedAtMs,
                completedAtMs: completedAtMs,
                elapsedMs: elapsedMs
            )
            publishCurrentState()
            logCompletion(flow: flow, step: step, elapsedMs: elapsedMs)
        }

        return try operation()
    }

    func currentPayload() -> [String: Any] {
        payload(active: frames.last, lastCompleted: lastCompleted)
    }

    func recordUIUpdateRequest(
        property: MainActorUIProperty,
        state: MainActorUIState
    ) {
        // UI write state exists only for the opt-in Sentry diagnostic. Do not
        // retain writes that happen before error reporting is enabled.
        guard errorLoggingEnabled() else {
            latestUIUpdateRequests.removeAll()
            return
        }
        let requestedAtMs = nowMs()
        latestUIUpdateRequests[property] = UIUpdateRequest(
            property: property,
            state: state,
            requestedAtMs: requestedAtMs
        )
        publishCurrentState()
    }

    private static func elapsedMs(from startNs: UInt64, to endNs: UInt64) -> Int {
        guard endNs >= startNs else { return 0 }
        return Int(((endNs - startNs) / 1_000_000))
    }

    private func payload(active: Frame?, lastCompleted: CompletedFrame?) -> [String: Any] {
        let activeElapsedMs = active.map { Self.elapsedMs(from: $0.startedAtUptimeNs, to: uptimeNs()) } ?? 0
        let uiRequests = MainActorUIProperty.allCases.compactMap { latestUIUpdateRequests[$0] }
        return [
            "\(Self.keyPrefix)state": active == nil ? Self.stateIdle : Self.stateActive,
            "\(Self.keyPrefix)flow": active?.flow.rawValue ?? Self.slugNone,
            "\(Self.keyPrefix)step": active?.step.rawValue ?? Self.slugNone,
            "\(Self.keyPrefix)operation_id": active?.operationId ?? Self.slugNone,
            "\(Self.keyPrefix)started_at_ms": active?.startedAtMs ?? 0,
            "\(Self.keyPrefix)completed_at_ms": 0,
            "\(Self.keyPrefix)elapsed_ms": activeElapsedMs,
            "\(Self.keyPrefix)last_completed_flow": lastCompleted?.flow.rawValue ?? Self.slugNone,
            "\(Self.keyPrefix)last_completed_step": lastCompleted?.step.rawValue ?? Self.slugNone,
            "\(Self.keyPrefix)last_completed_operation_id": lastCompleted?.operationId ?? Self.slugNone,
            "\(Self.keyPrefix)last_completed_started_at_ms": lastCompleted?.startedAtMs ?? 0,
            "\(Self.keyPrefix)last_completed_at_ms": lastCompleted?.completedAtMs ?? 0,
            "\(Self.keyPrefix)last_completed_elapsed_ms": lastCompleted?.elapsedMs ?? 0,
            "main_actor_ui_properties": uiRequests.map { $0.property.rawValue },
            "main_actor_ui_states": uiRequests.map { $0.state.rawValue },
            "main_actor_ui_requested_at_ms": uiRequests.map(\.requestedAtMs)
        ]
    }

    private func publishCurrentState() {
        guard errorLoggingEnabled() else {
            latestUIUpdateRequests.removeAll()
            return
        }
        publisher(currentPayload())
    }

    private func logEntry(flow: MainActorHangFlow, step: MainActorHangStep) {
        let flowSlug = flow.rawValue
        let stepSlug = step.rawValue
        switch flow {
        case .recordingWindow:
            AppLogger.ui.info("Main-actor boundary entered: flow=\(flowSlug, privacy: .public) step=\(stepSlug, privacy: .public)")
        case .autoPaste:
            AppLogger.accessibility.info("Main-actor boundary entered: flow=\(flowSlug, privacy: .public) step=\(stepSlug, privacy: .public)")
        }
    }

    private func logCompletion(flow: MainActorHangFlow, step: MainActorHangStep, elapsedMs: Int) {
        let flowSlug = flow.rawValue
        let stepSlug = step.rawValue
        switch flow {
        case .recordingWindow:
            AppLogger.ui.info("Main-actor boundary completed: flow=\(flowSlug, privacy: .public) step=\(stepSlug, privacy: .public) elapsed_ms=\(elapsedMs, privacy: .public)")
        case .autoPaste:
            AppLogger.accessibility.info("Main-actor boundary completed: flow=\(flowSlug, privacy: .public) step=\(stepSlug, privacy: .public) elapsed_ms=\(elapsedMs, privacy: .public)")
        }
    }
}
