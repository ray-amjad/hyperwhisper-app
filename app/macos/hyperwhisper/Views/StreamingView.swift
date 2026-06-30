//
//  StreamingView.swift
//  hyperwhisper
//
//  STREAMING VIEW
//  Dedicated navigation section for real-time streaming transcription settings.
//
//  This view provides a standalone interface for configuring streaming transcription,
//  which operates independently from the mode-based recording system. Users can:
//  - Enable/disable streaming transcription
//  - Choose a streaming provider (HyperWhisper Cloud, Deepgram, ElevenLabs, xAI)
//  - Configure provider-specific settings (model, fast formatting)
//  - Customize the keyboard shortcut for activating streaming
//  - Select the language for streaming transcription
//
//  PROVIDER CAPABILITIES:
//  | Provider         | API Key | Vocabulary | Model Selection | Fast Formatting |
//  |-----------------|---------|------------|-----------------|-----------------|
//  | HyperWhisper    | No      | Yes        | No              | No              |
//  | Deepgram        | Yes     | Yes*       | Yes (Nova 3)    | Yes             |
//  | ElevenLabs      | Yes     | No         | No              | No              |
//  | xAI             | Yes     | No         | No              | No              |
//
//  *Deepgram vocabulary only works with explicit language (not auto-detect)

import SwiftUI
import KeyboardShortcuts
import FluidAudio

// MARK: - Streaming View

/// Main view for streaming transcription settings.
///
/// LAYOUT ORDER (when streaming enabled):
/// 1. Enable toggle
/// 2. Provider picker (HyperWhisper Cloud | Deepgram | ElevenLabs | xAI)
/// 3. Model picker (Deepgram only: Nova 3 General | Nova 3 Medical)
/// 4. Fast Formatting toggle (Deepgram only)
/// 5. Warnings (API key missing, vocabulary unsupported, vocabulary auto-detect)
/// 6. Keyboard shortcut
/// 7. Language picker
struct StreamingView: View {
    /// Access to app-wide settings including streaming language preference
    @EnvironmentObject private var settingsManager: SettingsManager

    /// Access to app state for navigation (e.g., navigating to Settings for API key configuration)
    @EnvironmentObject private var appState: AppState
    @EnvironmentObject private var cloudProviderHealthManager: CloudProviderHealthManager

    /// Local Parakeet model lifecycle (download / delete / state).
    /// Optional — only used by the on-device streaming branch; avoids
    /// forcing every other consumer of StreamingView to inject it.
    @EnvironmentObject private var parakeetModelManager: ParakeetModelManager

    /// Local Nemotron 3.5 model lifecycle. Same rationale as
    /// `parakeetModelManager` — only the on-device Nemotron branch reads it.
    @EnvironmentObject private var nemotronManager: NemotronModelManager

    /// The currently selected streaming provider, derived from settings.
    /// Falls back to HyperWhisper Cloud if the stored value is invalid.
    private var selectedProvider: StreamingTranscriptionProvider {
        StreamingTranscriptionProvider(rawValue: settingsManager.streamingProvider) ?? .hyperwhisperCloud
    }

    /// API key value for the currently selected direct streaming provider.
    /// Uses live SettingsManager values so the warning updates immediately as users edit keys.
    private var selectedProviderAPIKey: String {
        switch selectedProvider {
        case .deepgram:
            return settingsManager.deepgramAPIKey
        case .elevenLabs:
            return settingsManager.elevenLabsAPIKey
        case .openAI:
            return settingsManager.openAIAPIKey
        case .xai:
            return settingsManager.grokAPIKey
        case .hyperwhisperCloud, .parakeetLocal, .nemotronLocal:
            return ""
        }
    }

    /// Whether the selected provider requires an API key that hasn't been configured yet.
    private var isAPIKeyMissing: Bool {
        guard selectedProvider.requiresAPIKey else { return false }
        return selectedProviderAPIKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// CloudProvider counterpart for selected streaming provider (only direct providers use API keys).
    private var selectedCloudProviderForHealthCheck: CloudProvider? {
        switch selectedProvider {
        case .deepgram:
            return .deepgram
        case .elevenLabs:
            return .elevenLabs
        case .openAI:
            return .openai
        case .xai:
            return .grok
        case .hyperwhisperCloud, .parakeetLocal, .nemotronLocal:
            return nil
        }
    }

    /// Whether selected provider has an API key configured but failing authorization health checks.
    private var isAPIKeyInvalid: Bool {
        guard let provider = selectedCloudProviderForHealthCheck else { return false }
        guard !isAPIKeyMissing else { return false }
        let status = cloudProviderHealthManager.status(for: provider)
        return status == .unauthorized
    }

    /// Whether vocabulary items exist in the database.
    /// Used to show warnings when the selected provider doesn't support vocabulary
    /// or when auto-detect language is selected (vocabulary requires explicit language).
    private var hasVocabularyItems: Bool {
        !PersistenceController.shared.fetchAllVocabularyItems().isEmpty
    }

    /// Whether auto-detect language currently disables vocabulary boosting.
    /// Shown directly under the language row for better context.
    private var shouldShowVocabAutoDetectWarning: Bool {
        settingsManager.streamingLanguage == "auto"
            && hasVocabularyItems
            && selectedProvider != .elevenLabs
            && selectedProvider != .openAI
            && selectedProvider != .xai
    }

    private func normalizeStreamingLanguageForCurrentProvider() {
        switch selectedProvider {
        case .parakeetLocal:
            let modelId = settingsManager.streamingLocalParakeetVersion
            if modelId == ParakeetModelManager.Constants.v2ModelId {
                if settingsManager.streamingLanguage != "en" {
                    settingsManager.streamingLanguage = "en"
                }
                return
            }

            if !ParakeetModelManager.Constants.v3Languages.keys.contains(settingsManager.streamingLanguage) {
                settingsManager.streamingLanguage = "en"
            }

        case .nemotronLocal:
            let modelId = settingsManager.streamingLocalNemotronVariant
            guard let supported = NemotronModelManager.supportedLanguages(forModelId: modelId) else { return }
            // Nemotron variants don't intrinsically include "auto" — but we let
            // multilingual users pick "auto" because FluidAudio falls back to
            // the model's `default_prompt_id` for unknown codes (safe).
            let allowed: Set<String>
            if modelId == NemotronModelManager.Constants.multilingualModelId {
                allowed = Set(supported.keys).union([LanguageData.automaticCode])
            } else {
                allowed = Set(supported.keys)
            }
            if !allowed.contains(settingsManager.streamingLanguage) {
                let fallback = modelId == NemotronModelManager.Constants.multilingualModelId
                    ? LanguageData.automaticCode
                    : "en"
                settingsManager.streamingLanguage = fallback
            }

        default:
            break
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Header
            headerSection

            Divider()

            // Settings content
            settingsSection

            Spacer()
        }
        .background(VisualEffectBackground())
        .navigationTitle("streaming.title".localized)
        .onAppear {
            normalizeStreamingLanguageForCurrentProvider()
            refreshSelectedProviderHealth(force: true)
        }
        .onChange(of: settingsManager.streamingProvider) { _, _ in
            normalizeStreamingLanguageForCurrentProvider()
            refreshSelectedProviderHealth(force: true)
        }
        .onChange(of: settingsManager.streamingEnabled) { _, enabled in
            if enabled {
                refreshSelectedProviderHealth(force: true)
            }
            // Keep OS-level hotkey registration in sync with the toggle
            // (hyperwhisperApp.syncFeatureGatedHotkeys observes this).
            NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
        }
        .onChange(of: settingsManager.deepgramAPIKey) { _, _ in
            if selectedProvider == .deepgram {
                refreshSelectedProviderHealth(force: true)
            }
        }
        .onChange(of: settingsManager.elevenLabsAPIKey) { _, _ in
            if selectedProvider == .elevenLabs {
                refreshSelectedProviderHealth(force: true)
            }
        }
        .onChange(of: settingsManager.openAIAPIKey) { _, _ in
            if selectedProvider == .openAI {
                refreshSelectedProviderHealth(force: true)
            }
        }
        .onChange(of: settingsManager.grokAPIKey) { _, _ in
            if selectedProvider == .xai {
                refreshSelectedProviderHealth(force: true)
            }
        }
    }

    // MARK: - Header Section

    private var headerSection: some View {
        PageHeader(
            title: "streaming.title".localized,
            subtitle: "streaming.description".localized
        )
    }

    // MARK: - Settings Section

    /// Main settings area using macOS System Settings–style grouped cards.
    ///
    /// GROUPS:
    /// - General: Enable + Shortcut
    /// - Engine: Provider + provider-specific rows (Deepgram model/formatting,
    ///           Parakeet version/install status)
    /// - Language: Language picker
    ///
    /// Provider warnings (missing/invalid API key, unsupported vocabulary) sit
    /// beneath the Engine card; the vocab/auto-detect warning sits beneath the
    /// Language card.
    private var settingsSection: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                // GENERAL
                groupTitle("General")
                streamingCard {
                    enableToggleSection

                    if settingsManager.streamingEnabled {
                        Divider()
                        shortcutSection
                    }
                }

                if settingsManager.streamingEnabled {
                    // ENGINE
                    groupTitle("Engine")
                    streamingCard {
                        providerSection

                        if selectedProvider == .deepgram {
                            Divider()
                            modelSection

                            Divider()
                            fastFormattingSection
                        }

                        if selectedProvider == .parakeetLocal {
                            Divider()
                            parakeetVersionSection

                            Divider()
                            parakeetModelStatusSection
                        }

                        if selectedProvider == .nemotronLocal {
                            Divider()
                            nemotronVariantSection

                            Divider()
                            nemotronModelStatusSection
                        }
                    }

                    // Engine-related warnings / badges sit just beneath the card
                    if selectedProvider == .parakeetLocal || selectedProvider == .nemotronLocal {
                        localProviderBadge
                    } else {
                        providerWarningSection
                        warningsSection
                    }

                    // LANGUAGE
                    groupTitle("Language")
                    streamingCard {
                        languageSection
                    }

                    if shouldShowVocabAutoDetectWarning {
                        warningRow(
                            message: "streaming.warning.vocabAutoDetect".localized
                        )
                    }
                }
            }
            .padding()
        }
    }

    // MARK: - Grouped Card Helpers

    /// Section header label shown above each grouped card (e.g. "General").
    private func groupTitle(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 13, weight: .semibold))
            .foregroundColor(.secondary)
            .padding(.leading, 4)
    }

    /// Rounded `.thinMaterial` card that wraps related rows. Matches the
    /// card style already used in Settings (see `SettingsSharedStyles`).
    @ViewBuilder
    private func streamingCard<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            content()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.thinMaterial))
    }

    // MARK: - Enable Toggle Section

    /// Toggle to enable/disable streaming transcription
    /// LAYOUT: Horizontal row with icon + label on LEFT, toggle switch on RIGHT
    private var enableToggleSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "waveform.badge.mic")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("streaming.enable.title".localized)
                .font(.headline)

            Spacer()

            Toggle("", isOn: $settingsManager.streamingEnabled)
                .toggleStyle(.switch)
                .labelsHidden()
        }
    }

    // MARK: - Provider Section

    /// Provider picker for selecting the streaming transcription backend.
    /// LAYOUT: Horizontal row with icon + label on LEFT, picker on RIGHT
    ///
    /// PROVIDERS:
    /// - HyperWhisper Cloud: Default, no API key needed, routes through Fly.io backend
    /// - Deepgram: Direct WebSocket, requires API key, supports model selection
    /// - ElevenLabs: Direct WebSocket, requires API key, no vocabulary support
    /// - xAI: Direct WebSocket, requires Grok/xAI API key, no vocabulary support
    private var providerSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "cloud")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("streaming.provider.picker.title".localized)
                .font(.headline)

            Spacer()

            // Provider picker using StreamingTranscriptionProvider enum for type-safe selection
            Picker("", selection: $settingsManager.streamingProvider) {
                ForEach(StreamingTranscriptionProvider.allCases) { provider in
                    Text(provider.displayName).tag(provider.rawValue)
                }
            }
            .labelsHidden()
            .frame(width: 200, alignment: .trailing)
            .frame(maxWidth: .infinity, alignment: .trailing)
        }
    }

    /// Provider-specific key warnings shown directly under the provider picker for quick remediation.
    @ViewBuilder
    private var providerWarningSection: some View {
        if isAPIKeyMissing {
            warningRow(
                message: String(format: "streaming.warning.apiKeyRequired".localized, selectedProvider.displayName),
                actionLabel: "streaming.warning.apiKeyRequired.action".localized,
                action: navigateToAPIKeySettings
            )
        } else if isAPIKeyInvalid {
            warningRow(
                message: String(format: "streaming.warning.apiKeyInvalid".localized, selectedProvider.displayName),
                actionLabel: "streaming.warning.apiKeyInvalid.action".localized,
                action: navigateToAPIKeySettings
            )
        }
    }

    // MARK: - Model Section (Deepgram Only)

    /// Model picker for Deepgram streaming — Nova 3 General or Nova 3 Medical.
    /// LAYOUT: Horizontal row with icon + label on LEFT, picker on RIGHT
    ///
    /// WHY NOVA 3 ONLY:
    /// Nova 3 is Deepgram's best streaming model family. Offering older models
    /// (Nova 2, Enhanced) would add decision fatigue without meaningful benefit.
    /// Medical variant is available for healthcare-specific terminology.
    private var modelSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "cpu")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("streaming.model.title".localized)
                .font(.headline)

            Spacer()

            Picker("", selection: $settingsManager.streamingDeepgramModel) {
                Text("streaming.model.nova3general".localized).tag("nova-3-general")
                Text("streaming.model.nova3medical".localized).tag("nova-3-medical")
            }
            .labelsHidden()
            .frame(width: 200, alignment: .trailing)
            .frame(maxWidth: .infinity, alignment: .trailing)
        }
    }

    // MARK: - Fast Formatting Section (Deepgram Only)

    /// Toggle for Deepgram's no_delay fast formatting mode.
    /// LAYOUT: Horizontal row with icon + label + description on LEFT, toggle on RIGHT
    ///
    /// WHAT IT DOES:
    /// When enabled, Deepgram returns smart formatting results immediately without
    /// waiting for additional context. This prioritizes typing speed over formatting
    /// accuracy (e.g., numbers, dates, emails may format less perfectly).
    ///
    /// WHY DEFAULT ON:
    /// Users choosing streaming are optimizing for speed. The formatting delay
    /// (waiting for surrounding context) adds noticeable latency to typed output.
    private var fastFormattingSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "bolt")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            VStack(alignment: .leading, spacing: 2) {
                Text("streaming.fastFormatting.title".localized)
                    .font(.headline)

                Text("streaming.fastFormatting.description".localized)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Spacer()

            Toggle("", isOn: $settingsManager.streamingFastFormatting)
                .toggleStyle(.switch)
                .labelsHidden()
        }
    }

    // MARK: - Warnings Section

    /// Contextual warnings shown based on current provider and settings.
    ///
    /// Currently shows vocabulary support warnings. API key warnings are rendered
    /// directly under the provider picker via `providerWarningSection`.
    @ViewBuilder
    private var warningsSection: some View {
        // WARNING: Some realtime APIs have no vocabulary boosting capability.
        if (selectedProvider == .elevenLabs || selectedProvider == .openAI || selectedProvider == .xai) && hasVocabularyItems {
            warningRow(
                message: String(format: "streaming.warning.vocabUnsupported".localized, selectedProvider.displayName)
            )
        }

    }

    private func navigateToAPIKeySettings() {
        appState.navigateToModelLibraryAPIKeys()
    }

    private func refreshSelectedProviderHealth(force: Bool) {
        guard let provider = selectedCloudProviderForHealthCheck else { return }
        cloudProviderHealthManager.refresh(provider, force: force)
    }

    private var languageCloudProviderId: String {
        selectedProvider == .xai ? CloudProvider.grok.rawValue : CloudProvider.hyperwhisper.rawValue
    }

    private var languageCloudModelId: String {
        selectedProvider == .xai ? "" : "nova-3"
    }

    // MARK: - Warning Row Helper

    /// Reusable warning row with yellow accent, icon, message, and optional action button.
    /// LAYOUT: HStack with warning icon + message text + optional action button
    ///
    /// WHY SEPARATE HELPER:
    /// Multiple warnings share the same visual style. Extracting the layout
    /// ensures consistency and reduces duplication across warning types.
    ///
    /// - Parameters:
    ///   - message: The warning text to display
    ///   - actionLabel: Optional button label (e.g., "Configure")
    ///   - action: Optional closure to execute when the button is tapped
    private func warningRow(message: String, actionLabel: String? = nil, action: (() -> Void)? = nil) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundColor(.yellow)
                .font(.system(size: 13))

            Text(message)
                .font(.system(size: 12))
                .foregroundColor(.secondary)

            if let actionLabel = actionLabel, let action = action {
                Button(action: action) {
                    Text(actionLabel)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(.accentColor)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.leading, 42) // Align with content area (30px icon frame + 12px spacing)
    }

    // MARK: - Parakeet (On-Device) Sections

    /// "Runs locally" badge shown instead of API-key warnings when the
    /// on-device provider is selected.
    private var localProviderBadge: some View {
        HStack(spacing: 8) {
            Image(systemName: "lock.shield.fill")
                .foregroundColor(.green)
                .font(.system(size: 13))

            Text("Runs locally on this Mac. Transcribes during your pauses.")
                .font(.system(size: 12))
                .foregroundColor(.secondary)
        }
        .padding(.leading, 42)
    }

    /// Parakeet V2 vs V3 version picker. V2 is English-only; V3 supports
    /// 25 European languages. Settings stores the model id string.
    private var parakeetVersionSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "cpu")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("Model")
                .font(.headline)

            Spacer()

            Picker("", selection: $settingsManager.streamingLocalParakeetVersion) {
                Text("Parakeet V3 (Multilingual)").tag(ParakeetModelManager.Constants.v3ModelId)
                Text("Parakeet V2 (English)").tag(ParakeetModelManager.Constants.v2ModelId)
            }
            .labelsHidden()
            .frame(width: 240, alignment: .trailing)
            .frame(maxWidth: .infinity, alignment: .trailing)
            .onChange(of: settingsManager.streamingLocalParakeetVersion) { _, _ in
                normalizeStreamingLanguageForCurrentProvider()
            }
        }
    }

    /// Model download / deletion status row. Reuses the global
    /// `ParakeetModelManager` so the same underlying bundle powers batch
    /// and streaming — no double downloads.
    private var parakeetModelStatusSection: some View {
        let modelId = settingsManager.streamingLocalParakeetVersion
        let model = parakeetModelManager.availableModels.first(where: { $0.id == modelId })
        let isDownloading = parakeetModelManager.isDownloading(modelId)
        let isInstalled = model?.isDownloaded ?? false

        return HStack(spacing: 12) {
            Image(systemName: isInstalled ? "checkmark.circle.fill" : "arrow.down.circle")
                .font(.title2)
                .foregroundColor(isInstalled ? .green : .secondary)
                .frame(width: 30)

            VStack(alignment: .leading, spacing: 2) {
                Text(model?.displayName ?? "Parakeet")
                    .font(.headline)
                Text(statusLine(isInstalled: isInstalled, isDownloading: isDownloading, size: model?.size))
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Spacer()

            if isDownloading {
                ProgressView()
                    .controlSize(.small)
            } else if !isInstalled {
                Button("Install") {
                    parakeetModelManager.startDownload(modelId)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
                .frame(minWidth: 78)
            } else {
                Button("Manage") {
                    appState.selectedNavigationItem = .modelLibrary
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
                .frame(minWidth: 78)
            }
        }
    }

    private func statusLine(isInstalled: Bool, isDownloading: Bool, size: String?) -> String {
        if isDownloading { return "Downloading…" }
        if isInstalled {
            return "Installed\(size.map { " · \($0)" } ?? "")"
        }
        return "Not installed\(size.map { " · \($0)" } ?? "")"
    }

    // MARK: - Language Section

    /// Language picker for streaming transcription
    /// LAYOUT: Horizontal row with icon + label on LEFT, dropdown on RIGHT
    private var languageSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "globe")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("streaming.language.title".localized)
                .font(.headline)

            Spacer()

            if selectedProvider == .parakeetLocal {
                parakeetLanguagePicker
                    .frame(width: 200, alignment: .trailing)
            } else if selectedProvider == .nemotronLocal {
                nemotronLanguagePicker
                    .frame(width: 200, alignment: .trailing)
            } else {
                // Use LanguageSelectionView with cloud provider settings
                // showLabel: false since this section already has its own label
                LanguageSelectionView(
                    language: $settingsManager.streamingLanguage,
                    provider: .cloud,
                    model: "cloud",
                    cloudProviderId: languageCloudProviderId,
                    cloudModelId: languageCloudModelId,
                    showLabel: false
                )
                .frame(width: 200, alignment: .trailing)
            }
        }
    }

    /// Language picker specific to the selected Parakeet version:
    /// V2 → English only (disabled picker).
    /// V3 → V3's 25-language list, sorted alphabetically.
    @ViewBuilder
    private var parakeetLanguagePicker: some View {
        let modelId = settingsManager.streamingLocalParakeetVersion
        let isV2 = modelId == ParakeetModelManager.Constants.v2ModelId
        let languages = isV2
            ? ParakeetModelManager.Constants.v2Languages
            : ParakeetModelManager.Constants.v3Languages
        let sorted = languages.sorted { $0.value < $1.value }

        Picker("", selection: $settingsManager.streamingLanguage) {
            ForEach(sorted, id: \.key) { code, name in
                Text(name).tag(code)
            }
        }
        .labelsHidden()
        .disabled(isV2)
    }

    // MARK: - Nemotron (On-Device) Sections

    /// Nemotron 3.5 Latin vs Multilingual variant picker. Latin: ~6 European
    /// languages, fast. Multilingual: ~40 languages incl. CJK + Arabic.
    private var nemotronVariantSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "cpu")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("Model")
                .font(.headline)

            Spacer()

            Picker("", selection: $settingsManager.streamingLocalNemotronVariant) {
                Text("Nemotron 3.5 (Multilingual)").tag(NemotronModelManager.Constants.multilingualModelId)
                Text("Nemotron 3.5 (Latin)").tag(NemotronModelManager.Constants.latinModelId)
            }
            .labelsHidden()
            .frame(width: 240, alignment: .trailing)
            .frame(maxWidth: .infinity, alignment: .trailing)
            .onChange(of: settingsManager.streamingLocalNemotronVariant) { _, _ in
                normalizeStreamingLanguageForCurrentProvider()
            }
        }
    }

    /// Variant install / download row. Reuses the global NemotronModelManager
    /// so the Library and Streaming surfaces share download state.
    private var nemotronModelStatusSection: some View {
        let modelId = settingsManager.streamingLocalNemotronVariant
        let model = nemotronManager.availableModels.first(where: { $0.id == modelId })
        let isDownloading = nemotronManager.isDownloading(modelId)
        let isInstalled = model?.isDownloaded ?? false

        return HStack(spacing: 12) {
            Image(systemName: isInstalled ? "checkmark.circle.fill" : "arrow.down.circle")
                .font(.title2)
                .foregroundColor(isInstalled ? .green : .secondary)
                .frame(width: 30)

            VStack(alignment: .leading, spacing: 2) {
                Text(model?.displayName ?? "Nemotron 3.5")
                    .font(.headline)
                Text(statusLine(isInstalled: isInstalled, isDownloading: isDownloading, size: model?.size))
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Spacer()

            if isDownloading {
                ProgressView()
                    .controlSize(.small)
            } else if !isInstalled {
                Button("Install") {
                    nemotronManager.startDownload(modelId)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
                .frame(minWidth: 78)
            } else {
                Button("Manage") {
                    appState.selectedNavigationItem = .modelLibrary
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
                .frame(minWidth: 78)
            }
        }
    }

    /// Language picker for the selected Nemotron variant. Multilingual shows
    /// "Automatic" first; Latin omits it because the model's prompt dictionary
    /// is restricted to the 6 Latin-script codes.
    @ViewBuilder
    private var nemotronLanguagePicker: some View {
        let modelId = settingsManager.streamingLocalNemotronVariant
        let supported = NemotronModelManager.supportedLanguages(forModelId: modelId) ?? [:]
        let sorted = supported.sorted { $0.value < $1.value }

        Picker("", selection: $settingsManager.streamingLanguage) {
            if modelId == NemotronModelManager.Constants.multilingualModelId {
                Text("Automatic").tag(LanguageData.automaticCode)
            }
            ForEach(sorted, id: \.key) { code, name in
                Text(name).tag(code)
            }
        }
        .labelsHidden()
    }

    // MARK: - Shortcut Section

    /// Keyboard shortcut recorder for streaming
    /// LAYOUT: Horizontal row with icon + label on LEFT, recorder on RIGHT
    /// Uses .startStreaming shortcut name from KeyboardShortcuts+Names.swift
    private var shortcutSection: some View {
        HStack(spacing: 12) {
            Image(systemName: "keyboard")
                .font(.title2)
                .foregroundColor(.secondary)
                .frame(width: 30)

            Text("streaming.shortcut.title".localized)
                .font(.headline)

            Spacer()

            KeyboardShortcuts.Recorder(for: .startStreaming) { _ in
                NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
            }
        }
    }
}

// MARK: - Preview

#Preview {
    StreamingView()
        .environmentObject(SettingsManager.shared)
        .environmentObject(AppState())
        .environmentObject(ParakeetModelManager())
        .environmentObject(NemotronModelManager())
        .frame(width: 500, height: 600)
}
