//
//  ModeCard.swift
//  HyperWhisper
//
//  Individual mode card display component for the modes grid.
//

import SwiftUI

// MARK: - Helper Functions

/// Convert language code to display name
func languageDisplayName(_ code: String) -> String {
    return LanguageData.displayName(for: code)
}

// MARK: - Mode Card

/// Individual mode card in the grid
struct ModeCard: View {
    let mode: Mode
    let isSelected: Bool
    let whisperModelManager: WhisperModelManager
    let parakeetModelManager: ParakeetModelManager
    let qwen3AsrModelManager: Qwen3AsrModelManager
    let transcriptionPipeline: TranscriptionPipeline
    let onSelect: () -> Void
    let onEdit: () -> Void
    let onDelete: () -> Void

    @EnvironmentObject var customEndpointManager: CustomPostProcessingManager
    @EnvironmentObject var nemotronModelManager: NemotronModelManager

    private func displayName(for id: String) -> String {
        TranscriptionModelCatalog(whisper: whisperModelManager, parakeet: parakeetModelManager)
            .displayName(for: id)
    }

    private func isModelDownloaded(_ id: String) -> Bool {
        if id == "apple-speech-analyzer" { return true }
        if id == Qwen3AsrModelManager.Constants.modelId {
            return qwen3AsrModelManager.isDownloaded
        }
        if whisperModelManager.downloadedModels.contains(where: { $0.name == id }) {
            return true
        }
        if let parakeet = parakeetModelManager.availableModels.first(where: { $0.name == id }) {
            return parakeet.isDownloaded
        }
        if let nemotron = nemotronModelManager.availableModels.first(where: { $0.name == id }) {
            return nemotron.isDownloaded
        }
        return false
    }


    @State private var isHovered = false
    @State private var isPressed = false

    // Break down complex view into computed properties to help compiler
    private var headerSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Header with gradient background
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 6) {
                        let modeDisplayName = mode.name?.isEmpty == false ? mode.name! : "modes.name.unnamed".localized
                        HStack(alignment: .center) {
                            Text(modeDisplayName)
                                .font(.system(size: 16, weight: .semibold))
                                .foregroundColor(isSelected ? .accentColor : .primary)
                                .lineLimit(1)

                            // NO WARM-UP NEEDED with libwhisper.cpp!
                            // Models load instantly (2-5 seconds)

                            // Offline indicator if mode is offline-capable
                            let modeData = ModeData(from: mode)
                            if modeData.isOfflineCapable {
                                HStack(spacing: 2) {
                                    // Use a widely available symbol; avoid missing 'cloud.slash.fill'
                                    Image(systemName: "icloud.slash")
                                        .font(.system(size: 10))
                                        .foregroundColor(.green)
                                    Text(localized: "modes.badge.offline")
                                        .font(.system(size: 9, weight: .medium))
                                        .foregroundColor(.green)
                                }
                                .padding(.horizontal, 5)
                                .padding(.vertical, 2)
                                .background(Color.green.opacity(0.1))
                                .cornerRadius(4)
                            }

                            Spacer()

                            // Settings button in top right
                            Button {
                                onEdit()
                            } label: {
                                Image(systemName: "gearshape.fill")
                                    .font(.system(size: 14))
                                    .foregroundColor(.secondary)
                            }
                            .buttonStyle(.plain)
                            .help("modes.help.edit".localized)
                        }
                        .padding(.bottom, 4)  // Add spacing below the title row

                        // Preset badge with language
                        if let presetString = mode.preset, let preset = PresetType(rawValue: presetString) {
                            HStack(spacing: 12) {
                                HStack(spacing: 4) {
                                    Image(systemName: "doc.text")
                                        .font(.system(size: 11))
                                    Text(preset.displayName)
                                        .font(.system(size: 11, weight: .medium))
                                }
                                .foregroundColor(.secondary)

                                HStack(spacing: 4) {
                                    Image(systemName: "globe")
                                        .font(.system(size: 11))
                                    Text(languageDisplayName(mode.language ?? "en"))
                                        .font(.system(size: 11, weight: .medium))
                                }
                                .foregroundColor(.secondary)
                            }
                            .padding(.bottom, 6)  // Add spacing below the preset badge
                        }

                        HStack(spacing: 12) {

                            let modelString = mode.model ?? "base"
                            if modelString.lowercased() == "cloud" {
                                HStack(spacing: 4) {
                                    Image(systemName: "icloud.fill")
                                        .font(.system(size: 11))
                                    // Display cloud provider name and model
                                    let provider = mode.cloudProvider ?? "hyperwhisper"
                                    let providerDisplay: String = {
                                        if provider == "hyperwhisper" {
                                            return "HyperWhisper"  // Shortened name for HyperWhisper Cloud
                                        }
                                        return CloudProvider(rawValue: provider)?.displayName ?? provider.capitalized
                                    }()

                                    // Show provider name
                                    Text(providerDisplay)
                                        .font(.system(size: 11))

                                    // Show cloud model if available (unless it's HyperWhisper Cloud which uses fixed model)
                                    if let cloudModel = mode.cloudTranscriptionModel,
                                       provider != "hyperwhisper" {
                                        Text("·")
                                            .font(.system(size: 11))
                                            .foregroundColor(.secondary)
                                        Text(CloudTranscriptionModels.displayName(for: cloudModel))
                                            .font(.system(size: 11))
                                    }
                                }
                                .foregroundColor(.blue)
                            } else {
                                HStack(spacing: 4) {
                                    Image(systemName: isModelDownloaded(modelString) ? "cpu" : "cpu.fill")
                                        .font(.system(size: 11))
                                    // USE CACHED DISPLAY NAME FOR MODEL
                                    // Shows proper name like "Tiny (English-only)" instead of "tiny.en"
                                    Text(displayName(for: modelString))
                                        .font(.system(size: 11))
                                }
                                .foregroundColor(isModelDownloaded(modelString) ? .green : .orange)
                            }

                            // Post-processing indicator - Only show if post-processing is enabled
                            // Hide when post-processing is off since it won't be used
                            let modeData = ModeData(from: mode)
                            if modeData.postProcessingMode != .off {
                                HStack(spacing: 4) {
                                    Image(systemName: "brain")
                                        .font(.system(size: 11))
                                    // Get the proper display name based on provider type
                                    let providerString = mode.postProcessingProvider ?? "hyperwhisper"
                                    let displayName: String = {
                                        // HyperWhisper Cloud has built-in post-processing
                                        if providerString == "hyperwhisper" {
                                            return "HyperWhisper"
                                        }

                                        // Custom endpoint: show endpoint name (model is embedded in endpoint config)
                                        if CustomPostProcessingEndpoint.isCustomProviderString(providerString),
                                           let endpointId = CustomPostProcessingEndpoint.parseCustomProviderString(providerString),
                                           let endpoint = customEndpointManager.getEndpoint(id: endpointId) {
                                            return endpoint.name
                                        }

                                        // Built-in provider: show model name
                                        let provider = PostProcessingProvider(rawValue: providerString) ?? .hyperwhisper
                                        let languageModel = mode.languageModel ?? "gpt-4.1-nano"
                                        return PostProcessingModels.displayName(for: languageModel, provider: provider)
                                            .replacingOccurrences(of: " (Q4)", with: "")
                                    }()
                                    Text(displayName)
                                        .font(.system(size: 11))
                                }
                                .foregroundColor(.purple)
                            }
                        }
                    }
                }
            }
            .padding(16)
            .background(
                LinearGradient(
                    colors: isSelected ?
                        [Color.accentColor.opacity(0.08), Color.accentColor.opacity(0.02)] :
                        [Color.primary.opacity(0.02), Color.clear],
                    startPoint: .top,
                    endPoint: .bottom
                )
            )
        }
    }

    private var cardBackground: some View {
        RoundedRectangle(cornerRadius: 16, style: .continuous)
            .fill(.regularMaterial)
            .shadow(color: .black.opacity(0.05), radius: 1, x: 0, y: 1)
    }

    private var cardOverlay: some View {
        RoundedRectangle(cornerRadius: 16, style: .continuous)
            .stroke(
                isSelected ?
                    LinearGradient(
                        colors: [Color.accentColor.opacity(0.5), Color.purple.opacity(0.3)],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    ) :
                    LinearGradient(
                        colors: [Color.primary.opacity(0.1), Color.primary.opacity(0.05)],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    ),
                lineWidth: isSelected ? 2 : 1
            )
    }

    var body: some View {
        headerSection
        .background(cardBackground)
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(cardOverlay)
        .shadow(
            color: isSelected ? .accentColor.opacity(0.2) : .black.opacity(isHovered ? 0.1 : 0.05),
            radius: isSelected ? 12 : (isHovered ? 8 : 4),
            x: 0,
            y: isSelected ? 4 : (isHovered ? 3 : 2)
        )
        .scaleEffect(isPressed ? 0.98 : (isHovered ? 1.02 : 1.0))
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isHovered)
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isSelected)
        .animation(.spring(response: 0.1, dampingFraction: 0.9), value: isPressed)
        .onTapGesture {
            if !isSelected {
                onSelect()
            }
        }
        .onHover { hovering in
            isHovered = hovering
        }
        .contextMenu {
            if !isSelected {
                Button {
                    onSelect()
                } label: {
                    Label(LocalizedStringKey("modes.context.select"), systemImage: "checkmark.circle")
                }
            }
            Button {
                onEdit()
            } label: {
                Label(LocalizedStringKey("common.edit"), systemImage: "pencil")
            }
        }
    }

}

// MARK: - Feature Badge View

struct FeatureBadge: View {
    let icon: String
    let text: String
    let isActive: Bool
    var color: Color = .accentColor

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: icon)
                .font(.system(size: 9))
            Text(text)
                .font(.system(size: 10, weight: .medium))
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(isActive ? color.opacity(0.15) : Color.gray.opacity(0.1))
        )
        .foregroundColor(isActive ? color : .secondary)
    }
}
