//
//  ModePresetPicker.swift
//  HyperWhisper
//
//  Preset selection dropdown with tooltips for mode configuration.
//

import SwiftUI
#if os(macOS)
import AppKit
#endif

// MARK: - Preset Picker View

/// Preset picker component with dropdown and tooltips
struct PresetPickerView: View {
    @Binding var preset: String
    @Binding var customInstructions: String
    @State private var showingInfo = false
    @State private var hoveredPreset: PresetType?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(localized: "modes.preset.title")
                    .frame(width: 120, alignment: .leading)

                Picker("", selection: $preset) {
                    ForEach(PresetType.allCases, id: \.self) { presetType in
                        Text(presetType.displayName).tag(presetType.rawValue)
                    }
                }
                .pickerStyle(.menu)
                .labelsHidden()

                Button {
                    showingInfo.toggle()
                } label: {
                    Image(systemName: "info.circle")
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
                .popover(isPresented: $showingInfo, arrowEdge: .trailing) {
                    VStack(alignment: .leading, spacing: 10) {
                        if let currentPreset = PresetType(rawValue: preset) {
                            Text(currentPreset.displayName)
                                .font(.headline)
                            Text(currentPreset.previewDescription)
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .padding()
                    .frame(width: 300)
                }
                .help("modes.help.preset".localized)

                Spacer()
            }

            // Show custom instructions field when Custom preset is selected
            if preset == PresetType.custom.rawValue {
                VStack(alignment: .leading, spacing: 6) {
                    Text(localized: "modes.customInstructions")
                        .font(.caption2)
                        .foregroundColor(.secondary)
                    TextEditor(text: $customInstructions)
                        .font(.system(size: 12, design: .monospaced))
                        .frame(height: 100)
                        .background(
                            RoundedRectangle(cornerRadius: 6)
                                .fill(Color(NSColor.controlBackgroundColor))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 6)
                                .stroke(Color.secondary.opacity(0.2), lineWidth: 1)
                        )
                }
            }
        }
    }
}
