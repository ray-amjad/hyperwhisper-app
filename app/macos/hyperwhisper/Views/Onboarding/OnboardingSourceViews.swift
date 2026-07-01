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

// MARK: - Onboarding model selection

/// One curated on-device model offered during onboarding. Deliberately spans
/// BOTH local engines (Whisper + Parakeet) behind a single identity so that:
///   • step 4 downloads via the correct manager, and
///   • the default Mode's `model` field is set to exactly the string the
///     transcription router expects (`TranscriptionProviderRouter` keys off the
///     `parakeet-tdt-` prefix to pick the engine).
///
/// `id` is therefore the single source of truth and is written verbatim to
/// `Mode.model`: Whisper uses its short catalog name ("base", "large-v3_turbo");
/// Parakeet uses its full id ("parakeet-tdt-0.6b-v2").
struct OnboardingModelSelection: Identifiable, Equatable {
    enum Kind: Equatable { case whisper, parakeet }

    let id: String
    let kind: Kind
    let displayName: String
    let subtitleKey: String
    let size: String
    /// Speed / accuracy on a 1–5 scale. Values mirror the rating tables in
    /// `ModelLibraryManager` (`whisperRatings` / `parakeetRatings`), duplicated
    /// here because those tables are private to that manager.
    let speed: Int
    let accuracy: Int
    let isRecommended: Bool

    /// The curated onboarding shortlist: Parakeet V2 (recommended) + V3 plus two
    /// Whisper sizes. Sizes/availability are resolved from the live managers so
    /// they stay correct if the catalog changes.
    static func curated(
        whisper: WhisperModelManager,
        parakeet: ParakeetModelManager
    ) -> [OnboardingModelSelection] {
        func whisperSize(_ name: String) -> String {
            whisper.availableModels.first { $0.name == name }?.size ?? ""
        }
        return [
            OnboardingModelSelection(
                id: ParakeetModelManager.Constants.v2ModelId,
                kind: .parakeet,
                displayName: "Parakeet V2",
                subtitleKey: "onboarding.model.parakeetV2.subtitle",
                size: ParakeetModelManager.Constants.v2SizeDescription,
                speed: 5, accuracy: 3, isRecommended: true
            ),
            OnboardingModelSelection(
                id: ParakeetModelManager.Constants.v3ModelId,
                kind: .parakeet,
                displayName: "Parakeet V3",
                subtitleKey: "onboarding.model.parakeetV3.subtitle",
                size: ParakeetModelManager.Constants.v3SizeDescription,
                speed: 5, accuracy: 3, isRecommended: false
            ),
            OnboardingModelSelection(
                id: "base",
                kind: .whisper,
                displayName: "Whisper Base",
                subtitleKey: "onboarding.model.whisperBase.subtitle",
                size: whisperSize("base"),
                speed: 5, accuracy: 1, isRecommended: false
            ),
            OnboardingModelSelection(
                id: "large-v3_turbo",
                kind: .whisper,
                displayName: "Whisper Large v3 Turbo",
                subtitleKey: "onboarding.model.whisperTurbo.subtitle",
                size: whisperSize("large-v3_turbo"),
                speed: 4, accuracy: 3, isRecommended: false
            )
        ]
    }
}

/// Five-bar speed/accuracy gauge, mirroring `ModelRow.gaugeBar(rating:)` (which
/// is private to that view). Kept standalone so onboarding can tint speed and
/// accuracy differently, as in the redesign mockup.
struct OnboardingGaugeBar: View {
    let rating: Int
    let color: Color

    var body: some View {
        HStack(spacing: 3) {
            ForEach(0..<5, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1)
                    .fill(i < rating ? color : Color.primary.opacity(0.12))
                    .frame(width: 12, height: 4)
            }
        }
    }
}

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
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager

    @Binding var selectedModel: OnboardingModelSelection?
    @Binding var licenseKeyInput: String
    @Binding var selectedProvider: CloudProvider
    @Binding var apiKeyInput: String

    // Surfaced to the parent's Continue gate: true only while the inline test above
    // has a *passing* result for the current key/provider. Mirrors the local test
    // state below and is cleared everywhere that state is cleared, so a stale pass
    // can never gate a different key.
    @Binding var keyValidated: Bool

    // Inline "test key" result state. Reset on step appear + provider change so a
    // stale "valid" can never carry over to a different key (mirrors the mockup).
    @State private var isTestingKey = false
    @State private var licenseTestValid: Bool?
    @State private var licenseTestError: String?
    @State private var providerTestHealth: ProviderHealth?

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

    private func resetTestResults() {
        isTestingKey = false
        licenseTestValid = nil
        licenseTestError = nil
        providerTestHealth = nil
        keyValidated = false
    }

    // MARK: On-Device — pick a model to download

    private var curatedModels: [OnboardingModelSelection] {
        OnboardingModelSelection.curated(whisper: whisperModelManager, parakeet: parakeetModelManager)
    }

    private var onDeviceConfigure: some View {
        VStack(spacing: 16) {
            configureHeader(
                icon: "cpu",
                tint: .green,
                title: "onboarding.configure.onDevice.title",
                subtitle: "onboarding.configure.onDevice.subtitle"
            )

            ScrollView {
                VStack(spacing: 9) {
                    ForEach(curatedModels) { model in
                        modelRow(model)
                    }
                }
                .padding(.horizontal, 2)
            }
            .frame(maxWidth: 520, maxHeight: 290)
        }
        .padding(40)
    }

    private func isModelDownloaded(_ model: OnboardingModelSelection) -> Bool {
        switch model.kind {
        case .whisper:
            return whisperModelManager.getModelPath(for: model.id) != nil
        case .parakeet:
            return parakeetModelManager.availableModels.first { $0.id == model.id }?.isDownloaded == true
        }
    }

    private func modelRow(_ model: OnboardingModelSelection) -> some View {
        let isDownloaded = isModelDownloaded(model)
        let isSelected = selectedModel?.id == model.id
        return Button {
            selectedModel = model
        } label: {
            HStack(spacing: 13) {
                Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                    .foregroundColor(isSelected ? .accentColor : .secondary)

                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 8) {
                        Text(model.displayName)
                            .font(.system(size: 13.5, weight: .semibold))
                            .foregroundColor(.primary)
                        pill(model.kind == .parakeet
                                ? "onboarding.model.pill.parakeet"
                                : "onboarding.model.pill.whisper",
                             tint: model.kind == .parakeet ? .green : .secondary)
                        if model.isRecommended {
                            pill("onboarding.model.pill.recommended", tint: .accentColor)
                        }
                    }
                    Text(model.subtitleKey.localized)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                }

                Spacer(minLength: 8)

                VStack(alignment: .leading, spacing: 5) {
                    gaugeRow("onboarding.model.metric.speed", rating: model.speed, color: .accentColor)
                    gaugeRow("onboarding.model.metric.accuracy", rating: model.accuracy, color: .green)
                }
                .frame(width: 112)

                if isDownloaded {
                    Label("onboarding.model.downloaded".localized, systemImage: "checkmark.circle.fill")
                        .labelStyle(.iconOnly)
                        .foregroundColor(.green)
                } else {
                    Text(model.size)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                        .frame(width: 56, alignment: .trailing)
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
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

    private func pill(_ key: String, tint: Color) -> some View {
        Text(key.localized.uppercased())
            .font(.system(size: 9, weight: .bold))
            .tracking(0.3)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Capsule().fill(tint.opacity(0.16)))
            .foregroundColor(tint)
    }

    private func gaugeRow(_ labelKey: String, rating: Int, color: Color) -> some View {
        HStack(spacing: 7) {
            Text(labelKey.localized)
                .font(.system(size: 9.5))
                .foregroundColor(.secondary)
                .frame(width: 52, alignment: .leading)
            OnboardingGaugeBar(rating: rating, color: color)
        }
    }

    // MARK: HyperWhisper Cloud — access key + test

    private var cloudConfigure: some View {
        VStack(spacing: 16) {
            configureHeader(
                icon: "icloud.fill",
                tint: .accentColor,
                title: "onboarding.configure.cloud.title",
                subtitle: "onboarding.configure.cloud.subtitle"
            )

            VStack(alignment: .leading, spacing: 12) {
                TextField("onboarding.configure.cloud.placeholder".localized, text: $licenseKeyInput)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(.body, design: .monospaced))
                    // A fresh key invalidates any prior "valid" result.
                    .onChange(of: licenseKeyInput) { _, _ in
                        licenseTestValid = nil
                        licenseTestError = nil
                        keyValidated = false
                    }

                HStack(spacing: 12) {
                    Button {
                        testAccessKey()
                    } label: {
                        Text("onboarding.configure.cloud.testKey".localized)
                    }
                    .disabled(isTestingKey || licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    cloudTestResult
                }

                Link("onboarding.configure.cloud.getCredits".localized,
                     destination: URL(string: "https://hyperwhisper.com")!)
                    .font(.caption)
            }
            .frame(maxWidth: 380)
        }
        .padding(40)
        .onAppear(perform: resetTestResults)
    }

    @ViewBuilder
    private var cloudTestResult: some View {
        if isTestingKey {
            testLabel("onboarding.configure.test.testing", systemImage: nil, color: .secondary, spinning: true)
        } else if licenseTestValid == true {
            testLabel("onboarding.configure.test.valid", systemImage: "checkmark.circle.fill", color: .green)
        } else if let error = licenseTestError {
            Text(error)
                .font(.system(size: 12))
                .foregroundColor(.red)
                .lineLimit(2)
        }
    }

    /// Validate (does NOT activate — activation stays on step 4) and show the
    /// result inline. Reuses `LicenseManager.validateLicense`.
    private func testAccessKey() {
        let key = licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        Task {
            isTestingKey = true
            licenseTestValid = nil
            licenseTestError = nil
            let result = await licenseManager.validateLicense(key)
            licenseTestValid = result.isValid
            licenseTestError = result.isValid ? nil : (result.errorMessage ?? "app.unknown.error".localized)
            keyValidated = result.isValid
            isTestingKey = false
        }
    }

    // MARK: Your API Key — provider + key + test

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
                // provider than the one it was typed for. Also drop the test
                // result so it can't carry across providers.
                .onChange(of: selectedProvider) { _, _ in
                    apiKeyInput = ""
                    providerTestHealth = nil
                    keyValidated = false
                }

                SecureField("onboarding.configure.provider.keyPlaceholder".localized, text: $apiKeyInput)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(.body, design: .monospaced))
                    .onChange(of: apiKeyInput) { _, _ in
                        providerTestHealth = nil
                        keyValidated = false
                    }

                HStack(spacing: 12) {
                    Button {
                        testAPIKey()
                    } label: {
                        Text("onboarding.configure.provider.testKey".localized)
                    }
                    .disabled(isTestingKey || apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    providerTestResult
                }

                Text("onboarding.configure.provider.keychainNote".localized)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            .frame(maxWidth: 380)
        }
        .padding(40)
        .onAppear(perform: resetTestResults)
    }

    @ViewBuilder
    private var providerTestResult: some View {
        if isTestingKey {
            testLabel("onboarding.configure.test.testing", systemImage: nil, color: .secondary, spinning: true)
        } else if let health = providerTestHealth {
            switch health {
            case .healthy:
                testLabel("onboarding.configure.test.healthy", systemImage: "checkmark.circle.fill", color: .green)
            case .unauthorized:
                Text("onboarding.configure.test.unauthorized".localized)
                    .font(.system(size: 12)).foregroundColor(.orange).lineLimit(2)
            case .unreachable:
                Text("onboarding.configure.test.unreachable".localized)
                    .font(.system(size: 12)).foregroundColor(.orange).lineLimit(2)
            case .unknown, .checking, .notInstalled:
                EmptyView()
            }
        }
    }

    /// Persist the key (health checks read it from Keychain) then run the shared
    /// provider health probe — the same path `ProviderKeySheet.testConnection`
    /// uses. Saving here is harmless: step 4 will simply show "saved".
    private func testAPIKey() {
        let key = apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        settingsManager.apiKeys.setAPIKey(apiKeyInput, for: selectedProvider)
        cloudHealth.registerAPIKeyChange(for: selectedProvider, newValue: key)
        Task {
            isTestingKey = true
            providerTestHealth = nil
            let health = await cloudHealth.ensureHealthy(selectedProvider)
            providerTestHealth = health
            keyValidated = (health == .healthy)
            isTestingKey = false
        }
    }

    @ViewBuilder
    private func testLabel(_ key: String, systemImage: String?, color: Color, spinning: Bool = false) -> some View {
        HStack(spacing: 6) {
            if spinning {
                ProgressView().scaleEffect(0.6)
            } else if let systemImage {
                Image(systemName: systemImage).foregroundColor(color)
            }
            Text(key.localized)
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundColor(color)
        }
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
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var settingsManager: SettingsManager

    @Binding var selectedModel: OnboardingModelSelection?
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

    // MARK: On-Device — download (routes per engine)

    private func isModelReady(_ model: OnboardingModelSelection) -> Bool {
        switch model.kind {
        case .whisper:
            return whisperModelManager.downloadedModels.contains { $0.name == model.id }
        case .parakeet:
            return parakeetModelManager.availableModels.first { $0.id == model.id }?.isDownloaded == true
        }
    }

    private func isModelDownloading(_ model: OnboardingModelSelection) -> Bool {
        switch model.kind {
        case .whisper:
            return whisperModelManager.downloadingModels.contains(model.id)
        case .parakeet:
            return parakeetModelManager.downloads.isDownloading(model.id)
        }
    }

    private func downloadProgress(_ model: OnboardingModelSelection) -> Double {
        switch model.kind {
        case .whisper:
            return whisperModelManager.downloadProgress[model.id] ?? 0
        case .parakeet:
            return parakeetModelManager.downloads.progress[model.id] ?? 0
        }
    }

    private func startModelDownload(_ model: OnboardingModelSelection) {
        switch model.kind {
        case .whisper:
            // Resolve the catalog model by canonical name, then download it.
            guard let whisperModel = whisperModelManager.availableModels.first(where: { $0.name == model.id }) else {
                return
            }
            Task { await whisperModelManager.downloadModel(whisperModel) }
        case .parakeet:
            parakeetModelManager.startDownload(model.id)
        }
    }

    private var onDeviceSetup: some View {
        VStack(spacing: 20) {
            setupHeader(icon: "arrow.down.circle.fill", tint: .green, title: "onboarding.setup.onDevice.title")

            if let model = selectedModel {
                let isReady = isModelReady(model)
                let isDownloading = isModelDownloading(model)
                let progress = downloadProgress(model)

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
                            startModelDownload(model)
                        } label: {
                            Label("onboarding.setup.onDevice.download".localized(arguments: model.displayName),
                                  systemImage: "arrow.down.circle.fill")
                                .frame(width: 240)
                        }
                        .controlSize(.large)
                        .buttonStyle(.borderedProminent)

                        // Surface a failed download so the user isn't left stuck at
                        // the mandatory Set-up gate with no explanation. (Whisper
                        // exposes a shared errorMessage; Parakeet downloads report
                        // their own failures inline via the progress controller.)
                        if model.kind == .whisper, let error = whisperModelManager.errorMessage {
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

// MARK: - Step 5: Microphone (device + live level)

/// Lets the user pick their input device and confirm it registers a live level,
/// before the "Give it a try" step. The level meter is driven by a dedicated
/// idle-metering session on `AudioRecordingManager` (`startInputLevelPreview`),
/// which is started on appear and torn down on disappear so the mic is never
/// held open past this screen.
struct OnboardingMicrophoneView: View {
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var settingsManager: SettingsManager

    private var usingSystemDefault: Bool { audioManager.selectedDevice == nil }

    var body: some View {
        VStack(spacing: 18) {
            Image(systemName: "headphones")
                .font(.system(size: 44))
                .foregroundColor(.accentColor)
                .symbolRenderingMode(.hierarchical)

            VStack(spacing: 8) {
                Text("onboarding.mic.title".localized)
                    .font(.title2)
                    .fontWeight(.semibold)
                Text("onboarding.mic.subtitle".localized)
                    .font(.callout)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 440)
            }

            VStack(alignment: .leading, spacing: 18) {
                // Input device picker (disabled while "use system default" is on).
                VStack(alignment: .leading, spacing: 6) {
                    Text("onboarding.audio.input.label".localized)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                        .textCase(.uppercase)
                    Picker("", selection: deviceBinding) {
                        ForEach(audioManager.availableDevices) { device in
                            Text(device.name).tag(device.id)
                        }
                    }
                    .labelsHidden()
                    .pickerStyle(.menu)
                    .frame(maxWidth: .infinity)
                    .disabled(usingSystemDefault)
                }

                HStack {
                    Text("onboarding.audio.use.system.default".localized)
                    Spacer()
                    Toggle("", isOn: systemDefaultBinding)
                        .labelsHidden()
                        .toggleStyle(.switch)
                }

                // Live level meter.
                VStack(alignment: .leading, spacing: 6) {
                    Text("onboarding.audio.level".localized)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                        .textCase(.uppercase)
                    levelMeter
                }

                if !audioManager.hasMicrophonePermission {
                    Text("onboarding.mic.permissionHint".localized)
                        .font(.caption)
                        .foregroundColor(.orange)
                }

                Button {
                    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.sound") {
                        NSWorkspace.shared.open(url)
                    }
                } label: {
                    Text("onboarding.audio.open.sound".localized)
                }
                .buttonStyle(.link)
            }
            .padding(20)
            .frame(maxWidth: 460)
            .background(
                RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.large)
                    .fill(.thinMaterial)
            )
        }
        .padding(40)
        .onAppear {
            audioManager.updateAvailableDevices()
            audioManager.startInputLevelPreview()
        }
        .onDisappear {
            audioManager.stopInputLevelPreview()
        }
    }

    private var levelMeter: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Color.primary.opacity(0.12))
                Capsule()
                    .fill(LinearGradient(colors: [Color.green.opacity(0.7), .green],
                                         startPoint: .leading, endPoint: .trailing))
                    .frame(width: max(0, geo.size.width * CGFloat(audioManager.idleInputLevel)))
                    .animation(.easeOut(duration: 0.08), value: audioManager.idleInputLevel)
            }
        }
        .frame(height: 9)
    }

    /// Picker selection ↔ the active input device id. Reactive off the published
    /// `selectedDevice`; persists to `selectedMicrophoneId` and re-points the
    /// live meter at the new device.
    private var deviceBinding: Binding<String> {
        Binding(
            get: { audioManager.selectedDevice?.id ?? "" },
            set: { apply(deviceId: $0) }
        )
    }

    /// Toggle ↔ "use the system default input" (an empty persisted id).
    private var systemDefaultBinding: Binding<Bool> {
        Binding(
            get: { audioManager.selectedDevice == nil },
            set: { on in
                apply(deviceId: on ? "" : (audioManager.availableDevices.first?.id ?? ""))
            }
        )
    }

    private func apply(deviceId: String) {
        if deviceId.isEmpty {
            audioManager.selectDevice(nil)
            settingsManager.selectedMicrophoneId = ""
        } else if let device = audioManager.availableDevices.first(where: { $0.id == deviceId }) {
            audioManager.selectDevice(device)
            settingsManager.selectedMicrophoneId = deviceId
        }
        // Re-point the metering session at the newly selected device.
        audioManager.startInputLevelPreview()
    }
}
