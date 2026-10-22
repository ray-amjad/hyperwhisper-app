//
//  MicrophonePickerRow.swift
//  hyperwhisper
//
//  Settings → Sound → Microphone: choose the microphone HyperWhisper records from,
//  and test it.
//
//  - The list is "Default · <current default>" followed by every connected input,
//    and refreshes on plug/unplug (it reads the device manager's published list).
//  - Picking one saves it for HyperWhisper only; the macOS default input is not
//    changed. The menu-bar Microphone menu reads and writes the same saved value.
//  - A saved microphone that is unplugged stays selected as "<name> (not connected)",
//    with one amber line saying which device is used until it returns.
//  - Visiting the page does not open the microphone. `Test` opens it for ~10 s and
//    drives the level bar; leaving the page, closing Settings, a second click or a
//    recording starting stops it.
//

import SwiftUI

struct MicrophonePickerRow: View {
    @EnvironmentObject var audioManager: AudioRecordingManager

    private static let segmentCount = 24

    var body: some View {
        let devices = audioManager.availableDevices
        let preferred = audioManager.preferredMicrophone
        let missingChoice = MicrophoneResolution.resolve(
            savedUID: preferred?.uid,
            savedName: preferred?.name,
            devices: devices
        ).missingChoice
        let defaultName = MicrophonePickerLabels.defaultDeviceName(
            devices: devices,
            systemDefaultUID: audioManager.systemDefaultDeviceUID
        )
        let isTesting = audioManager.isMicrophoneTestRunning

        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .center, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(localized: "settings.sound.microphone.title")
                        .font(.headline)
                    Text(localized: "settings.sound.microphone.subtitle")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }

                Spacer(minLength: 12)

                Picker("", selection: selectionBinding) {
                    Text(verbatim: MicrophonePickerLabels.defaultOption(defaultDeviceName: defaultName))
                        .tag("")
                    if let missingChoice {
                        Text(verbatim: MicrophonePickerLabels.notConnected(missingChoice))
                            .tag(missingChoice.uid)
                    }
                    ForEach(devices) { device in
                        Text(verbatim: device.name)
                            .tag(device.uid)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .frame(width: 240)
            }

            if let missingChoice {
                Text(verbatim: MicrophonePickerLabels.fallbackNotice(fallbackName: defaultName, choice: missingChoice))
                    .font(.caption)
                    .foregroundColor(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(alignment: .center, spacing: 12) {
                MicrophoneLevelBar(
                    level: isTesting ? audioManager.idleInputLevel : 0,
                    isActive: isTesting,
                    segmentCount: Self.segmentCount
                )
                .frame(height: 8)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(Text(localized: "settings.sound.microphone.level"))

                Button {
                    if isTesting {
                        audioManager.stopMicrophoneTest()
                    } else {
                        audioManager.startMicrophoneTest()
                    }
                } label: {
                    Text(localized: isTesting ? "settings.sound.microphone.stop" : "settings.sound.microphone.test")
                        .frame(minWidth: 44)
                }
                .disabled(!isTesting && (!audioManager.hasMicrophonePermission || audioManager.isRecording))
            }
        }
        .padding(DesignConstants.Spacing.rowPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .onDisappear {
            // Leaving the Sound page or closing Settings ends a running test.
            audioManager.stopMicrophoneTest()
        }
    }

    /// "" is "Default". Choosing the "(not connected)" entry keeps the saved choice.
    private var selectionBinding: Binding<String> {
        Binding(
            get: { audioManager.preferredMicrophone?.uid ?? "" },
            set: { uid in
                guard uid != (audioManager.preferredMicrophone?.uid ?? "") else { return }
                if uid.isEmpty {
                    audioManager.selectDevice(nil)
                } else if let device = audioManager.availableDevices.first(where: { $0.uid == uid }) {
                    audioManager.selectDevice(device)
                } else {
                    return
                }
                // A running test follows the newly chosen microphone.
                if audioManager.isMicrophoneTestRunning {
                    audioManager.startMicrophoneTest()
                }
            }
        )
    }
}

/// A segmented input-level bar. Empty and grey while idle.
struct MicrophoneLevelBar: View {
    let level: Float
    let isActive: Bool
    var segmentCount: Int = 24

    var body: some View {
        let lit = isActive ? MicrophoneLevelMeter.litSegments(level: level, segmentCount: segmentCount) : 0
        HStack(spacing: 2) {
            ForEach(0..<segmentCount, id: \.self) { index in
                RoundedRectangle(cornerRadius: 1.5, style: .continuous)
                    .fill(color(forSegment: index, lit: lit))
            }
        }
        .animation(.linear(duration: 0.05), value: lit)
    }

    private func color(forSegment index: Int, lit: Int) -> Color {
        guard index < lit else { return Color.secondary.opacity(0.25) }
        let position = Double(index) / Double(max(segmentCount - 1, 1))
        if position >= 0.85 { return .red }
        if position >= 0.65 { return .yellow }
        return .green
    }
}

#Preview {
    MicrophonePickerRow()
        .environmentObject(AudioRecordingManager())
        .frame(width: 600)
        .padding()
}
