//
//  SilentMicrophoneWarningView.swift
//  hyperwhisper
//
//  Issue #2411: the one-line amber "No sound from {mic}" warning shown beside the
//  recording dialog while the microphone hears nothing. Hosted in its own panel by
//  `SilentMicWarningWindowManager`, so the 200x40 recording pill, its stop button
//  and its waveform stay exactly as they are.
//

import SwiftUI

struct SilentMicrophoneWarningView: View {
    /// The microphone the capture opened. Cut with "…" when long.
    let deviceName: String

    static let size = CGSize(width: 280, height: 26)

    /// Amber, so it reads as a warning and not as the red of an error or the stop button.
    static let amber = Color(red: 1.0, green: 0.76, blue: 0.03)

    private var message: String {
        "recording.warning.micSilent".localized(
            arguments: SilentMicrophoneWatcher.displayName(deviceName)
        )
    }

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "mic.slash.fill")
                .font(.system(size: 10, weight: .semibold))

            Text(message)
                .font(.system(size: 11, weight: .medium))
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .foregroundColor(Self.amber)
        .padding(.horizontal, 12)
        .frame(width: Self.size.width, height: Self.size.height)
        .background(
            Capsule()
                .fill(Color.black.opacity(0.85))
        )
        .overlay(
            Capsule()
                .stroke(Self.amber.opacity(0.35), lineWidth: 1)
        )
        .accessibilityElement(children: .combine)
        .accessibilityLabel(message)
    }
}

#Preview {
    VStack(spacing: 12) {
        SilentMicrophoneWarningView(deviceName: "MacBook Pro Microphone")
        SilentMicrophoneWarningView(deviceName: "A Very Long USB Audio Interface Name With Channels 1-2")
    }
    .padding()
    .background(Color.gray.opacity(0.3))
}
