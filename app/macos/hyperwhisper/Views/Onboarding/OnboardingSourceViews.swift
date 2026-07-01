//
//  OnboardingSourceViews.swift
//  hyperwhisper
//
//  "Choose your transcription source" onboarding screens.
//  Splits the three-card picker + per-source Configure/Setup views out of
//  OnboardingView so the step machine there stays readable. Each card drives
//  the app's EXISTING managers (WhisperModelManager / LicenseManager /
//  APIKeySettingsManager) — no new backend logic. The final choice is applied
//  to the default Mode by OnboardingView at completion.
//

import SwiftUI

// MARK: - Source metadata

/// Presentation spec for one source card. Copy mirrors the validated
/// "Three Cards" prototype (On-Device / HyperWhisper Cloud / Your API Key).
struct OnboardingSourceSpec {
    let source: TranscriptionSource
    let icon: String
    let tint: Color
    let badgeKey: String
    let titleKey: String
    let descriptionKey: String
    let featureKeys: [String]

    static let all: [OnboardingSourceSpec] = [
        OnboardingSourceSpec(
            source: .onDevice,
            icon: "cpu",
            tint: .green,
            badgeKey: "onboarding.source.onDevice.badge",
            titleKey: "onboarding.source.onDevice.title",
            descriptionKey: "onboarding.source.onDevice.description",
            featureKeys: [
                "onboarding.source.onDevice.feature1",
                "onboarding.source.onDevice.feature2",
                "onboarding.source.onDevice.feature3"
            ]
        ),
        OnboardingSourceSpec(
            source: .hyperwhisperCloud,
            icon: "icloud.fill",
            tint: .accentColor,
            badgeKey: "onboarding.source.cloud.badge",
            titleKey: "onboarding.source.cloud.title",
            descriptionKey: "onboarding.source.cloud.description",
            featureKeys: [
                "onboarding.source.cloud.feature1",
                "onboarding.source.cloud.feature2",
                "onboarding.source.cloud.feature3"
            ]
        ),
        OnboardingSourceSpec(
            source: .yourProvider,
            icon: "key.fill",
            tint: .purple,
            badgeKey: "onboarding.source.provider.badge",
            titleKey: "onboarding.source.provider.title",
            descriptionKey: "onboarding.source.provider.description",
            featureKeys: [
                "onboarding.source.provider.feature1",
                "onboarding.source.provider.feature2",
                "onboarding.source.provider.feature3"
            ]
        )
    ]
}

// MARK: - Single source card

struct OnboardingSourceCard: View {
    let spec: OnboardingSourceSpec
    let isSelected: Bool
    let onSelect: () -> Void

    var body: some View {
        Button(action: onSelect) {
            VStack(alignment: .leading, spacing: 12) {
                // Icon + badge row
                HStack(alignment: .top) {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium)
                        .fill(spec.tint.gradient)
                        .frame(width: 40, height: 40)
                        .overlay(
                            Image(systemName: spec.icon)
                                .font(.system(size: 18, weight: .semibold))
                                .foregroundColor(.white)
                        )

                    Spacer(minLength: 4)

                    Text(spec.badgeKey.localized)
                        .font(.system(size: 10, weight: .semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(
                            Capsule().fill(spec.tint.opacity(0.18))
                        )
                        .foregroundColor(spec.tint)
                }

                Text(spec.titleKey.localized)
                    .font(.headline)
                    .foregroundColor(.primary)

                Text(spec.descriptionKey.localized)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .multilineTextAlignment(.leading)

                VStack(alignment: .leading, spacing: 6) {
                    ForEach(spec.featureKeys, id: \.self) { key in
                        HStack(spacing: 6) {
                            Image(systemName: "checkmark")
                                .font(.system(size: 9, weight: .bold))
                                .foregroundColor(spec.tint)
                            Text(key.localized)
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                    }
                }
                .padding(.top, 2)

                Spacer(minLength: 8)

                HStack(spacing: 6) {
                    Text((isSelected ? "onboarding.source.selected" : "onboarding.source.choose").localized)
                        .font(.system(size: 13, weight: .semibold))
                    Image(systemName: isSelected ? "checkmark.circle.fill" : "arrow.right")
                        .font(.system(size: 13, weight: .semibold))
                }
                .foregroundColor(spec.tint)
            }
            .padding(16)
            .frame(maxWidth: .infinity, minHeight: 300, alignment: .topLeading)
            .background(
                RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.large)
                    .fill(.thinMaterial)
            )
            .overlay(
                RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.large)
                    .stroke(isSelected ? spec.tint : Color.gray.opacity(0.2),
                            lineWidth: isSelected ? 2 : 1)
            )
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Step 2: Choose source (3 cards)

struct OnboardingSourcePicker: View {
    @Binding var selectedSource: TranscriptionSource?

    var body: some View {
        VStack(spacing: 16) {
            Text("onboarding.source.step".localized)
                .font(.system(size: 11, weight: .semibold))
                .tracking(1.2)
                .foregroundColor(.accentColor)

            Text("onboarding.source.title".localized)
                .font(.title)
                .fontWeight(.semibold)

            Text("onboarding.source.subtitle".localized)
                .font(.callout)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 520)

            HStack(alignment: .top, spacing: 12) {
                ForEach(OnboardingSourceSpec.all, id: \.source) { spec in
                    OnboardingSourceCard(
                        spec: spec,
                        isSelected: selectedSource == spec.source,
                        onSelect: {
                            withAnimation(.easeInOut(duration: 0.15)) {
                                selectedSource = spec.source
                            }
                        }
                    )
                }
            }
            .padding(.top, 4)

            Text("onboarding.source.footer".localized)
                .font(.caption)
                .foregroundColor(.secondary)
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 24)
    }
}

// MARK: - Step 3: Configure (branches per source)

struct OnboardingConfigureView: View {
    /// BYOK providers offered during onboarding — matches the "OpenAI · Deepgram ·
    /// Groq" copy on the source card. Others stay available in Settings.
    static let onboardingProviders: [CloudProvider] = [.openai, .deepgram, .groq]

    let source: TranscriptionSource

    @EnvironmentObject var whisperModelManager: WhisperModelManager

    @Binding var selectedModel: WhisperCppModel?
    @Binding var licenseKeyInput: String
    @Binding var selectedProvider: CloudProvider
    @Binding var apiKeyInput: String

    var body: some View {
        switch source {
        case .onDevice:
            onDeviceConfigure
        case .hyperwhisperCloud:
            cloudConfigure
        case .yourProvider:
            providerConfigure
        }
    }

    // MARK: On-Device — pick a model to download

    private var onDeviceConfigure: some View {
        VStack(spacing: 16) {
            configureHeader(
                icon: "cpu",
                tint: .green,
                title: "onboarding.configure.onDevice.title",
                subtitle: "onboarding.configure.onDevice.subtitle"
            )

            ScrollView {
                VStack(spacing: 8) {
                    ForEach(whisperModelManager.availableModels, id: \.name) { model in
                        modelRow(model)
                    }
                }
                .padding(.horizontal, 2)
            }
            .frame(maxWidth: 460, maxHeight: 260)
        }
        .padding(40)
    }

    private func modelRow(_ model: WhisperCppModel) -> some View {
        let isDownloaded = whisperModelManager.getModelPath(for: model.name) != nil
        let isSelected = selectedModel?.name == model.name
        return Button {
            selectedModel = model
        } label: {
            HStack(spacing: 12) {
                Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                    .foregroundColor(isSelected ? .accentColor : .secondary)
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.displayName)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundColor(.primary)
                    Text(model.size)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                Spacer()
                if isDownloaded {
                    Label("onboarding.model.downloaded".localized, systemImage: "checkmark.circle.fill")
                        .labelStyle(.iconOnly)
                        .foregroundColor(.green)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(
                RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium)
                    .fill(isSelected ? Color.accentColor.opacity(0.1) : Color.gray.opacity(0.06))
            )
            .overlay(
                RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium)
                    .stroke(isSelected ? Color.accentColor : Color.clear, lineWidth: 1.5)
            )
        }
        .buttonStyle(.plain)
    }

    // MARK: HyperWhisper Cloud — license key

    private var cloudConfigure: some View {
        VStack(spacing: 16) {
            configureHeader(
                icon: "icloud.fill",
                tint: .accentColor,
                title: "onboarding.configure.cloud.title",
                subtitle: "onboarding.configure.cloud.subtitle"
            )

            TextField("onboarding.configure.cloud.placeholder".localized, text: $licenseKeyInput)
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
                .frame(maxWidth: 380)

            Link("onboarding.configure.cloud.getCredits".localized,
                 destination: URL(string: "https://hyperwhisper.com")!)
                .font(.caption)
        }
        .padding(40)
    }

    // MARK: Your API Key — provider + key

    private var providerConfigure: some View {
        VStack(spacing: 16) {
            configureHeader(
                icon: "key.fill",
                tint: .purple,
                title: "onboarding.configure.provider.title",
                subtitle: "onboarding.configure.provider.subtitle"
            )

            VStack(alignment: .leading, spacing: 12) {
                Picker("onboarding.configure.provider.pickerLabel".localized, selection: $selectedProvider) {
                    // Keep onboarding focused on the providers the source card
                    // advertises (OpenAI · Deepgram · Groq). The full BYOK provider
                    // list remains available later in Settings.
                    ForEach(Self.onboardingProviders) { provider in
                        Text(provider.displayName).tag(provider)
                    }
                }
                .pickerStyle(.menu)
                // Clear any entered key when the provider changes so a masked,
                // stale key can't be saved into the keychain under a different
                // provider than the one it was typed for.
                .onChange(of: selectedProvider) { _, _ in apiKeyInput = "" }

                SecureField("onboarding.configure.provider.keyPlaceholder".localized, text: $apiKeyInput)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(.body, design: .monospaced))

                Text("onboarding.configure.provider.keychainNote".localized)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            .frame(maxWidth: 380)
        }
        .padding(40)
    }

    // MARK: Shared header

    private func configureHeader(icon: String, tint: Color, title: String, subtitle: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: icon)
                .font(.system(size: 44))
                .foregroundColor(tint)
                .symbolRenderingMode(.hierarchical)
            Text(title.localized)
                .font(.title2)
                .fontWeight(.semibold)
            Text(subtitle.localized)
                .font(.callout)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 440)
        }
    }
}

// MARK: - Step 4: Set up (perform the action)

struct OnboardingSetupView: View {
    let source: TranscriptionSource

    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var settingsManager: SettingsManager

    @Binding var selectedModel: WhisperCppModel?
    @Binding var licenseKeyInput: String
    @Binding var selectedProvider: CloudProvider
    @Binding var apiKeyInput: String

    var body: some View {
        switch source {
        case .onDevice:
            onDeviceSetup
        case .hyperwhisperCloud:
            cloudSetup
        case .yourProvider:
            providerSetup
        }
    }

    // MARK: On-Device — download

    private var onDeviceSetup: some View {
        VStack(spacing: 20) {
            setupHeader(icon: "arrow.down.circle.fill", tint: .green, title: "onboarding.setup.onDevice.title")

            if let model = selectedModel {
                let isReady = whisperModelManager.downloadedModels.contains { $0.name == model.name }
                let isDownloading = whisperModelManager.downloadingModels.contains(model.name)
                let progress = whisperModelManager.downloadProgress[model.name] ?? 0

                VStack(spacing: 12) {
                    Text(model.displayName)
                        .font(.headline)

                    if isReady {
                        statusBadge(icon: "checkmark.circle.fill",
                                    text: "onboarding.setup.onDevice.ready".localized,
                                    color: .green)
                    } else if isDownloading {
                        VStack(spacing: 8) {
                            ProgressView(value: progress)
                                .progressViewStyle(.linear)
                                .frame(width: 240)
                            Text("onboarding.setup.onDevice.downloading".localized(arguments: Int(progress * 100)))
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                    } else {
                        Button {
                            Task { await whisperModelManager.downloadModel(model) }
                        } label: {
                            Label("onboarding.setup.onDevice.download".localized(arguments: model.displayName),
                                  systemImage: "arrow.down.circle.fill")
                                .frame(width: 240)
                        }
                        .controlSize(.large)
                        .buttonStyle(.borderedProminent)

                        // Surface a failed download so the user isn't left stuck at
                        // the mandatory Set-up gate with no explanation.
                        if let error = whisperModelManager.errorMessage {
                            errorText(error)
                        }
                    }
                }
            } else {
                selectFirstNotice
            }
        }
        .padding(40)
    }

    // MARK: HyperWhisper Cloud — activate

    private var cloudSetup: some View {
        VStack(spacing: 20) {
            setupHeader(icon: "icloud.fill", tint: .accentColor, title: "onboarding.setup.cloud.title")

            if licenseManager.licenseStatus == .active {
                statusBadge(icon: "checkmark.circle.fill",
                            text: "onboarding.setup.cloud.active".localized,
                            color: .green)
            } else {
                Button {
                    Task { _ = await licenseManager.activateLicense(licenseKeyInput) }
                } label: {
                    HStack {
                        if licenseManager.isValidating {
                            ProgressView().scaleEffect(0.7)
                            Text("onboarding.setup.cloud.activating".localized)
                        } else {
                            Label("onboarding.setup.cloud.activate".localized, systemImage: "checkmark.seal.fill")
                        }
                    }
                    .frame(width: 240)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .disabled(licenseManager.isValidating || licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                if let error = licenseManager.lastError {
                    errorText(error)
                }
            }
        }
        .padding(40)
    }

    // MARK: Your API Key — save + verify

    private var providerSetup: some View {
        VStack(spacing: 20) {
            setupHeader(icon: "key.fill", tint: .purple, title: "onboarding.setup.provider.title")

            if settingsManager.apiKeys.hasAPIKey(for: selectedProvider) {
                statusBadge(icon: "checkmark.circle.fill",
                            text: "onboarding.setup.provider.saved".localized,
                            color: .green)
            } else {
                Button {
                    settingsManager.apiKeys.setAPIKey(apiKeyInput, for: selectedProvider)
                } label: {
                    Label("onboarding.setup.provider.save".localized, systemImage: "lock.fill")
                        .frame(width: 240)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .disabled(apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                if let error = settingsManager.apiKeys.validationError {
                    errorText(error)
                }
            }
        }
        .padding(40)
    }

    // MARK: Shared pieces

    private func setupHeader(icon: String, tint: Color, title: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: icon)
                .font(.system(size: 44))
                .foregroundColor(tint)
                .symbolRenderingMode(.hierarchical)
            Text(title.localized)
                .font(.title2)
                .fontWeight(.semibold)
        }
    }

    private func statusBadge(icon: String, text: String, color: Color) -> some View {
        HStack(spacing: 8) {
            Image(systemName: icon)
                .foregroundColor(color)
            Text(text)
                .font(.callout)
                .foregroundColor(color)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 8).fill(color.opacity(0.12)))
    }

    private func errorText(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundColor(.red)
            .multilineTextAlignment(.center)
            .frame(maxWidth: 380)
    }

    private var selectFirstNotice: some View {
        Text("onboarding.setup.selectFirst".localized)
            .font(.callout)
            .foregroundColor(.secondary)
    }
}
