//
//  ProviderKeySheet.swift
//  hyperwhisper
//
//  Shared connect/recover sheet for adding or fixing a provider's API key.
//  Presented from a locked or error row in the Library, or from the central
//  API keys manager modal.
//

import SwiftUI

enum ProviderKeySheetMode: Equatable {
    case connect
    case recover
}

/// Reference to a single provider that can have a key set on it. Wraps
/// either a CloudProvider (transcription) or a PostProcessingProvider
/// so the shared sheet can write through one or both of them — many
/// providers share a key (OpenAI, Gemini, Groq, Grok), so callers may
/// pass both.
struct ProviderKeyTarget: Identifiable, Equatable {
    let id: String
    let displayName: String
    let apiKeyURL: String
    let placeholder: String
    let cloudProvider: CloudProvider?
    let postProcessingProvider: PostProcessingProvider?
    /// Provider key used for the provider tile artwork.
    let iconKey: LibraryProviderKey

    static func cloud(_ provider: CloudProvider) -> ProviderKeyTarget {
        ProviderKeyTarget(
            id: "cloud-\(provider.rawValue)",
            displayName: provider.displayName,
            apiKeyURL: provider.apiKeyURL,
            placeholder: provider.apiKeyPlaceholder,
            cloudProvider: provider,
            postProcessingProvider: provider.pairedPostProcessing,
            iconKey: .cloud(provider)
        )
    }

    static func post(_ provider: PostProcessingProvider) -> ProviderKeyTarget {
        ProviderKeyTarget(
            id: "post-\(provider.rawValue)",
            displayName: provider.displayName,
            apiKeyURL: provider.apiKeyURL,
            placeholder: provider.apiKeyPlaceholder,
            cloudProvider: provider.pairedCloud,
            postProcessingProvider: provider,
            iconKey: .postProcessing(provider)
        )
    }
}

struct ProviderKeySheet: View {
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject var apiKeys: APIKeySettingsManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager

    let target: ProviderKeyTarget
    let mode: ProviderKeySheetMode

    @State private var keyInput: String = ""
    @State private var originalKey: String = ""
    @State private var revealKey: Bool = false
    @State private var isTesting: Bool = false
    @State private var lastTestResult: ProviderHealth?
    @State private var didCommit: Bool = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            body_
            Divider()
            footer
        }
        .frame(width: 520)
        .onAppear {
            originalKey = currentKey
            keyInput = originalKey
        }
        .onDisappear {
            guard !didCommit, currentKey != originalKey else { return }
            persistKey(originalKey)
        }
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 12) {
            ProviderIconView(providerKey: target.iconKey, size: 36)
            VStack(alignment: .leading, spacing: 2) {
                Text(mode == .connect ? "Connect \(target.displayName)" : "Reconnect \(target.displayName)")
                    .font(.headline)
                Text(subtitleText)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button(action: cancelAndDismiss) {
                Image(systemName: "xmark")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(.secondary)
            }
            .buttonStyle(.plain)
        }
        .padding(16)
    }

    private var body_: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("API key")
                    .font(.caption)
                    .foregroundColor(.secondary)
                Spacer()
                if let url = URL(string: target.apiKeyURL), !target.apiKeyURL.isEmpty {
                    Link(destination: url) {
                        Text("Where do I get this? ↗")
                            .font(.caption)
                            .foregroundColor(.accentColor)
                    }
                }
            }

            HStack {
                Group {
                    if revealKey {
                        TextField(target.placeholder, text: $keyInput)
                    } else {
                        SecureField(target.placeholder, text: $keyInput)
                    }
                }
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
                .onChange(of: keyInput) { _, newValue in
                    if newValue.trimmingCharacters(in: .whitespacesAndNewlines) != currentKey {
                        lastTestResult = nil
                    }
                }

                Button(action: { revealKey.toggle() }) {
                    Image(systemName: revealKey ? "eye.slash" : "eye")
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
            }

            statusBanner

            HStack(spacing: 8) {
                Button(action: testConnection) {
                    HStack(spacing: 6) {
                        if isTesting {
                            ProgressView().controlSize(.small)
                        }
                        Text("Test connection")
                    }
                }
                .disabled(keyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isTesting)
                Spacer()
            }

            Text("Stored in your macOS Keychain. Never sent to HyperWhisper servers.")
                .font(.caption)
                .foregroundColor(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(16)
    }

    private var footer: some View {
        HStack {
            if !currentKey.isEmpty {
                Button("Remove key") {
                    keyInput = ""
                    lastTestResult = nil
                }
                .foregroundColor(.red)
            }
            Spacer()
            Button("Cancel") { cancelAndDismiss() }
            Button("Done") {
                didCommit = true
                persistKey(keyInput)
                dismiss()
            }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
        }
        .padding(16)
    }

    @ViewBuilder
    private var statusBanner: some View {
        let status = lastTestResult ?? (hasStagedKeyChange ? .unknown : currentHealth)
        switch status {
        case .healthy:
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundColor(.green)
                Text("Everything's good to go.")
                    .font(.caption)
                Spacer()
            }
            .padding(8)
            .background(Color.green.opacity(0.1))
            .clipShape(RoundedRectangle(cornerRadius: 6))
        case .unauthorized:
            statusBannerView(symbol: "exclamationmark.triangle.fill", color: .orange,
                             text: "Provider rejected this key. Double-check it's still valid.")
        case .unreachable:
            statusBannerView(symbol: "wifi.exclamationmark", color: .orange,
                             text: "Couldn't reach the provider. Check your internet, then test again.")
        case .checking:
            statusBannerView(symbol: "arrow.triangle.2.circlepath", color: .secondary, text: "Checking…")
        case .unknown:
            EmptyView()
        case .notInstalled:
            EmptyView()
        }
    }

    private func statusBannerView(symbol: String, color: Color, text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: symbol).foregroundColor(color)
            Text(text).font(.caption)
            Spacer()
        }
        .padding(8)
        .background(color.opacity(0.1))
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }

    // MARK: - Helpers

    private var subtitleText: String {
        switch mode {
        case .connect:
            return "Add your API key for \(target.displayName)."
        case .recover:
            return "Your key stopped working. Paste a fresh key to restore access."
        }
    }

    private var currentKey: String {
        if let cloud = target.cloudProvider {
            return apiKeys.apiKey(for: cloud)
        }
        if let post = target.postProcessingProvider {
            return apiKeys.postProcessingAPIKey(for: post)
        }
        return ""
    }

    private var currentHealth: ProviderHealth {
        if let cloud = target.cloudProvider {
            return cloudHealth.status(for: cloud)
        }
        if let post = target.postProcessingProvider {
            return cloudHealth.status(for: post)
        }
        return .unknown
    }

    private var hasStagedKeyChange: Bool {
        keyInput.trimmingCharacters(in: .whitespacesAndNewlines) != currentKey
    }

    private func persistKey(_ raw: String) {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if let cloud = target.cloudProvider {
            apiKeys.setAPIKey(trimmed, for: cloud)
            cloudHealth.registerAPIKeyChange(for: cloud, newValue: trimmed)
        }
        if let post = target.postProcessingProvider {
            apiKeys.setPostProcessingAPIKey(trimmed, for: post)
            cloudHealth.registerAPIKeyChange(for: post, newValue: trimmed)
        }
        lastTestResult = nil
    }

    private func cancelAndDismiss() {
        didCommit = true
        if currentKey != originalKey {
            persistKey(originalKey)
        }
        dismiss()
    }

    private func testConnection() {
        let testedKey = keyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        persistKey(keyInput)
        isTesting = true
        let cloudProvider = target.cloudProvider
        let postProvider = target.postProcessingProvider
        Task {
            // Probe cloud + post in parallel for shared-key providers
            // (OpenAI, Gemini, Groq, Grok); halves the wait when both apply.
            async let cloudResult: ProviderHealth? = {
                guard let c = cloudProvider else { return nil }
                return await cloudHealth.ensureHealthy(c)
            }()
            async let postResult: ProviderHealth? = {
                guard let p = postProvider else { return nil }
                return await cloudHealth.ensureHealthy(p)
            }()
            let cloud = await cloudResult
            let post = await postResult

            // Surface the worst probe so users see the failing surface.
            let result: ProviderHealth = {
                if let c = cloud, c != .healthy { return c }
                if let p = post { return p }
                return cloud ?? .unknown
            }()
            await MainActor.run {
                if keyInput.trimmingCharacters(in: .whitespacesAndNewlines) == testedKey {
                    lastTestResult = result
                }
                isTesting = false
            }
        }
    }
}
