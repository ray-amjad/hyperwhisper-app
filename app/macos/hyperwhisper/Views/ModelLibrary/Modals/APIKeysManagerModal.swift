//
//  APIKeysManagerModal.swift
//  hyperwhisper
//
//  Central API keys manager. Lists every cloud + post-processing
//  provider with a status pill and an Add/Edit button that opens
//  ProviderKeySheet for the selected provider.
//

import SwiftUI

struct APIKeysManagerModal: View {
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject var apiKeys: APIKeySettingsManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager

    @State private var sheetTarget: ProviderKeyTarget?

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(providerEntries, id: \.id) { entry in
                        providerRow(entry)
                        Divider().padding(.horizontal, 16)
                    }
                }
            }
            Divider()
            footer
        }
        .frame(width: 560, height: 540)
        .sheet(item: $sheetTarget) { target in
            let mode: ProviderKeySheetMode = isErrorState(target: target) ? .recover : .connect
            ProviderKeySheet(target: target, mode: mode)
                .environmentObject(apiKeys)
                .environmentObject(cloudHealth)
        }
    }

    private var header: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text("API keys")
                    .font(.headline)
                Text("Stored in your macOS Keychain. Never sent to HyperWhisper servers.")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
            Button {
                dismiss()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(.secondary)
            }
            .buttonStyle(.plain)
        }
        .padding(16)
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button("Done") { dismiss() }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
        }
        .padding(16)
    }

    private func providerRow(_ entry: APIKeyProviderEntry) -> some View {
        HStack(spacing: 12) {
            ProviderIconView(providerKey: entry.target.iconKey, size: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.target.displayName)
                    .font(.system(size: 13, weight: .medium))
                capabilityBadge(transcription: entry.supportsTranscription, postProcessing: entry.supportsPostProcessing)
            }
            Spacer()
            statusPill(for: entry.health, hasKey: entry.hasKey)
            Button(action: { sheetTarget = entry.target }) {
                Text(entry.hasKey ? "Edit" : "Add")
                    .frame(minWidth: 50)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .contentShape(Rectangle())
    }

    /// Caption under the provider name stating which capabilities its key unlocks.
    /// Providers like OpenAI, Groq, Grok, and Gemini support both — their one key serves both.
    @ViewBuilder
    private func capabilityBadge(transcription: Bool, postProcessing: Bool) -> some View {
        let text: String = {
            if transcription && postProcessing { return "Transcription · Post-processing" }
            if postProcessing { return "Post-processing" }
            return "Transcription"
        }()
        Text(text)
            .font(.system(size: 10, weight: .medium))
            .foregroundColor(.secondary)
    }

    @ViewBuilder
    private func statusPill(for status: ProviderHealth, hasKey: Bool) -> some View {
        let (text, color): (String, Color) = {
            switch status {
            case .healthy: return ("Valid", .green)
            case .unauthorized: return ("Invalid", .orange)
            case .unreachable: return ("Unreachable", .orange)
            case .checking: return ("Checking…", .secondary)
            case .unknown: return (hasKey ? "Untested" : "No key", .secondary)
            case .notInstalled: return ("Not installed", .secondary)
            }
        }()
        Text(text)
            .font(.system(size: 10, weight: .semibold))
            .foregroundColor(color)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(color.opacity(0.12))
            .clipShape(Capsule())
    }

    private func isErrorState(target: ProviderKeyTarget) -> Bool {
        let status: ProviderHealth = {
            if let cloud = target.cloudProvider { return cloudHealth.status(for: cloud) }
            if let post = target.postProcessingProvider { return cloudHealth.status(for: post) }
            return .unknown
        }()
        switch status {
        case .unauthorized, .unreachable: return true
        default: return false
        }
    }

    // MARK: - Provider lists

    private struct APIKeyProviderEntry {
        let id: String
        let target: ProviderKeyTarget
        let supportsTranscription: Bool
        let supportsPostProcessing: Bool
        let hasKey: Bool
        let health: ProviderHealth
    }

    /// One row per distinct provider key. Providers whose key serves both
    /// transcription and post-processing (OpenAI, Groq, Grok, Gemini) appear
    /// once and are labelled as supporting both, rather than being duplicated.
    private var providerEntries: [APIKeyProviderEntry] {
        var entries: [APIKeyProviderEntry] = []

        // Cloud transcription providers — some also do post-processing (shared key).
        for provider in CloudProvider.allCases where provider.requiresAPIKey {
            entries.append(APIKeyProviderEntry(
                id: "cloud-\(provider.rawValue)",
                target: .cloud(provider),
                supportsTranscription: true,
                supportsPostProcessing: provider.pairedPostProcessing != nil,
                hasKey: apiKeys.hasAPIKey(for: provider),
                health: cloudHealth.status(for: provider)
            ))
        }

        // Post-processing-only providers (no paired cloud → not already listed above).
        for provider in PostProcessingProvider.allCases where provider.requiresAPIKey && provider.pairedCloud == nil {
            entries.append(APIKeyProviderEntry(
                id: "post-\(provider.rawValue)",
                target: .post(provider),
                supportsTranscription: false,
                supportsPostProcessing: true,
                hasKey: apiKeys.hasPostProcessingAPIKey(for: provider),
                health: cloudHealth.status(for: provider)
            ))
        }

        return entries
    }
}
