//
//  StreamingPreviewBubble.swift
//  hyperwhisper
//
//  Floating preview bubble shown above the recording dialog when the focused
//  application is unreliable for live streaming insertion (e.g. terminals).
//  Text accumulates inside the bubble as the user speaks and is pasted into
//  the target in a single shot at session end.
//

import SwiftUI

struct StreamingPreviewBubble: View {
    @EnvironmentObject var appState: AppState

    @State private var bobOffset: CGFloat = 0
    @State private var hasStartedBob = false

    private let bubbleContentHeight: CGFloat = 180

    private var bubbleText: String {
        // Once streaming has produced text, always show the transcript.
        if !appState.streamingText.isEmpty {
            return appState.streamingText
        }
        // Otherwise mirror the connection state shown on the recording capsule
        // (see RecordingDialog.statusText) so the user knows the model is
        // warming up / connecting and audio isn't being captured yet.
        switch appState.streamingConnectionState {
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
        case .error(let message):
            return "Error: \(message)"
        case .streaming, .idle:
            return "streaming.status.listening".localized
        }
    }

    private var isPlaceholder: Bool {
        appState.streamingText.isEmpty
    }

    var body: some View {
        VStack(spacing: 0) {
            Spacer(minLength: 0)

            if appState.showStreamingPreview {
                bubble
                    .offset(y: bobOffset)
                    .transition(bubbleTransition)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
        .padding(.bottom, 14)
        .padding(.horizontal, 20)
        .animation(.spring(response: 0.55, dampingFraction: 0.82), value: appState.showStreamingPreview)
        .animation(.spring(response: 0.5, dampingFraction: 0.86), value: appState.streamingText)
        .animation(.easeInOut(duration: 0.25), value: appState.streamingConnectionState)
        .onAppear {
            guard !hasStartedBob else { return }
            hasStartedBob = true
            withAnimation(.easeInOut(duration: 3.0).repeatForever(autoreverses: true)) {
                bobOffset = -3
            }
        }
    }

    private var bubble: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: 0) {
                    Text(bubbleText)
                        .font(.system(size: 13, weight: .medium, design: .default))
                        .foregroundStyle(isPlaceholder ? Color.white.opacity(0.6) : Color.white.opacity(0.95))
                        .lineSpacing(2.5)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: .infinity, minHeight: bubbleContentHeight, alignment: .leading)
                        .contentTransition(.interpolate)

                    Color.clear
                        .frame(height: 0)
                        .id("bubbleEnd")
                }
            }
            .frame(
                minWidth: 180,
                maxWidth: 420,
                minHeight: bubbleContentHeight,
                maxHeight: bubbleContentHeight
            )
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(bubbleBackground)
            .shadow(color: .black.opacity(0.3), radius: 20, x: 0, y: 10)
            .onChange(of: appState.streamingText) { _, _ in
                withAnimation(.easeOut(duration: 0.18)) {
                    proxy.scrollTo("bubbleEnd", anchor: .bottom)
                }
            }
        }
    }

    private var bubbleBackground: some View {
        RoundedRectangle(cornerRadius: 16, style: .continuous)
            .fill(Color.black.opacity(0.85))
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(Color.white.opacity(0.1), lineWidth: 1)
            )
    }

    private var bubbleTransition: AnyTransition {
        .asymmetric(
            insertion: .opacity
                .combined(with: .scale(scale: 0.9, anchor: .bottom))
                .combined(with: .offset(y: 8)),
            removal: .opacity
                .combined(with: .scale(scale: 0.85, anchor: .bottom))
                .combined(with: .offset(y: 24))
        )
    }
}
