//
//  ModelStatusBar.swift
//  hyperwhisper
//
//  Compact status bar at the bottom of the content area showing:
//  - Active transcription model and its readiness state
//  - Active local post-processing runtime and model (when active)
//  - Current mode name
//

import SwiftUI
import Combine

struct ModelStatusBar: View {
    @EnvironmentObject private var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject private var appState: AppState

    // SAFE OBSERVATION: Use @State snapshots fed by .onReceive to avoid
    // @MainActor + ObservableObject crash on macOS 26.2 (see MEMORY.md).
    @State private var modelState: TranscriptionModelManager.ModelReadyState = .none
    @State private var llamaServerState: LlamaServerController.State = .stopped
    @State private var modeName: String = ""
    @State private var modeSnapshot: ModeSnapshot?
    @State private var streamingConnectionState: StreamingConnectionState = .idle
    @State private var isStreamingShortcutTriggered = false

    // Pulsing animation for loading indicators
    @State private var isPulsing = false

    var body: some View {
        HStack(spacing: 0) {
            // Left: active engines
            HStack(spacing: 10) {
                modelIndicator

                if showLocalRuntimeStatus {
                    localRuntimeIndicator
                }
            }

            Spacer()

            // Right: current mode name
            Text(modeName)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 5)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) {
            Rectangle()
                .fill(Color.primary.opacity(0.08))
                .frame(height: 0.5)
        }
        .onReceive(transcriptionPipeline.modelCoordinator.$modelReadyState) { modelState = $0 }
        .onReceive(transcriptionPipeline.modelCoordinator.llamaServerController.$state) { llamaServerState = $0 }
        .onReceive(appState.$selectedModeName) { modeName = $0 }
        .onReceive(appState.$selectedModeSnapshot) { modeSnapshot = $0 }
        .onReceive(appState.$streamingConnectionState) { newState in
            streamingConnectionState = newState
            updatePulsingAnimation(for: modelState)
        }
        .onReceive(appState.$isStreamingShortcutTriggered) { isTriggered in
            isStreamingShortcutTriggered = isTriggered
            updatePulsingAnimation(for: modelState)
        }
        .onChange(of: modelState) { _, newValue in
            updatePulsingAnimation(for: newValue)
        }
        .onChange(of: llamaServerState) { _, _ in
            updatePulsingAnimation(for: modelState)
        }
    }

    // MARK: - Model Indicator

    @ViewBuilder
    private var modelIndicator: some View {
        HStack(spacing: 6) {
            switch modelState {
            case .none:
                Circle()
                    .fill(Color.gray.opacity(0.5))
                    .frame(width: 6, height: 6)
                Text("No Model")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)

            case .loading(let name):
                Circle()
                    .fill(Color.gray)
                    .frame(width: 6, height: 6)
                    .opacity(isPulsing ? 0.3 : 1.0)
                Text("\(name)")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Text("(Loading…)")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)

            case .ready(let name):
                Circle()
                    .fill(streamingStartupStatusText == nil ? (name == "Cloud" ? Color.blue : Color.green) : Color.orange)
                    .frame(width: 6, height: 6)
                    .opacity(streamingStartupStatusText == nil ? 1.0 : (isPulsing ? 0.3 : 1.0))
                Text(name)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                if let streamingStartupStatusText {
                    Text("(\(streamingStartupStatusText))")
                        .font(.system(size: 11))
                        .foregroundStyle(.tertiary)
                }
            }
        }
    }

    private var streamingStartupStatusText: String? {
        guard isStreamingShortcutTriggered else { return nil }

        switch streamingConnectionState {
        case .warmingUp:
            return "streaming.status.warming".localized
        case .connecting:
            return "streaming.status.connecting".localized
        case .ready:
            return "streaming.status.ready".localized
        case .reconnecting:
            return "streaming.state.reconnecting".localized
        case .disconnecting:
            return "streaming.status.disconnecting".localized
        case .error:
            return "common.error".localized
        case .idle, .streaming:
            return nil
        }
    }

    // MARK: - Local Runtime Indicator

    private var showLocalRuntimeStatus: Bool {
        switch llamaServerState {
        case .stopped: return false
        case .pending, .starting, .ready, .failed: return true
        }
    }

    private var activeLocalProvider: PostProcessingProvider? {
        guard
            let mode = modeSnapshot,
            (PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off) == .local
        else {
            return nil
        }

        return .localLLM
    }

    private var localRuntimeLabel: String {
        return "Local LLM"
    }

    private var localRuntimeModelLabel: String? {
        guard
            let provider = activeLocalProvider,
            let modelId = modeSnapshot?.languageModel,
            !modelId.isEmpty
        else {
            return nil
        }

        return PostProcessingModels.model(withId: modelId, provider: provider)?.displayName
    }

    private var localRuntimeDisplayName: String {
        localRuntimeModelLabel ?? localRuntimeLabel
    }

    @ViewBuilder
    private var localRuntimeIndicator: some View {
        HStack(spacing: 4) {
            switch llamaServerState {
            case .pending:
                Circle()
                    .fill(Color.orange)
                    .frame(width: 6, height: 6)
                    .opacity(isPulsing ? 0.3 : 1.0)
                Text(localRuntimeDisplayName)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Text("(Warming Up…)")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)

            case .starting:
                Circle()
                    .fill(Color.orange)
                    .frame(width: 6, height: 6)
                    .opacity(isPulsing ? 0.3 : 1.0)
                Text(localRuntimeDisplayName)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Text("(Starting…)")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)
                    .help(localRuntimeDisplayName)

            case .ready:
                Circle()
                    .fill(Color.green)
                    .frame(width: 6, height: 6)
                Text(localRuntimeDisplayName)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .help(localRuntimeDisplayName)

            case .failed(let msg):
                Circle()
                    .fill(Color.red)
                    .frame(width: 6, height: 6)
                Text(localRuntimeDisplayName)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Text("(Error)")
                    .font(.system(size: 11))
                    .foregroundStyle(.red.opacity(0.8))
                    .help(msg)

            case .stopped:
                EmptyView()
            }
        }
    }

    // MARK: - Animation

    private func updatePulsingAnimation(for state: TranscriptionModelManager.ModelReadyState) {
        let shouldPulse: Bool
        switch state {
        case .loading: shouldPulse = true
        default: shouldPulse = false
        }

        // Also pulse for local runtime startup
        let llamaStarting: Bool
        switch llamaServerState {
        case .pending, .starting: llamaStarting = true
        default: llamaStarting = false
        }

        if shouldPulse || llamaStarting || streamingStartupStatusText != nil {
            withAnimation(.easeInOut(duration: 0.8).repeatForever(autoreverses: true)) {
                isPulsing = true
            }
        } else {
            withAnimation(.default) {
                isPulsing = false
            }
        }
    }
}
