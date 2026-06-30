//
//  ModePostProcessingSettings.swift
//  HyperWhisper
//
//  Post-processing configuration panel for mode settings.
//

import SwiftUI
#if os(macOS)
import AppKit
#endif

// MARK: - Language Processing Settings View

/// Combined language processing and text output settings
struct LanguageProcessingSettingsView: View {
    private let activeCloudProvider: CloudProvider?
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var localModelManager: LocalModelManager
    @EnvironmentObject var customEndpointManager: CustomPostProcessingManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager
    @Environment(\.dismiss) private var dismiss
    @Binding var postProcessingMode: PostProcessingMode
    @Binding var postProcessingProvider: String
    @Binding var languageModel: String
    @Binding var profanityFilter: Bool
    @Binding var userSystemPrompt: String
    @Binding var language: String
    @Binding var englishSpelling: EnglishSpelling
    @Binding var enableScreenOCR: Bool
    @Binding var preset: String
    @Binding var customInstructions: String
    @Binding var cloudPostProcessingModel: String

    @State private var showLanguageModelInfo = false
    @State private var showPostProcessingInfo = false
    @State private var showEnglishSpellingInfo = false
    @State private var userPromptEnabled: Bool = false
    @State private var hasScreenRecordingPermission: Bool = false

    /// Last BYOK ("Your provider") / custom post-processing provider selected, so
    /// toggling the cloud Source to HyperWhisper Cloud and back restores it
    /// instead of seeding the first available provider. Persists for the edit
    /// session only.
    @State private var lastDirectPostProcessingProvider: String?

    init(
        activeCloudProvider: CloudProvider? = nil,
        preset: Binding<String>,
        customInstructions: Binding<String>,
        postProcessingMode: Binding<PostProcessingMode>,
        postProcessingProvider: Binding<String>,
        languageModel: Binding<String>,
        profanityFilter: Binding<Bool>,
        userSystemPrompt: Binding<String>,
        language: Binding<String>,
        englishSpelling: Binding<EnglishSpelling>,
        enableScreenOCR: Binding<Bool>,
        cloudPostProcessingModel: Binding<String>
    ) {
        self.activeCloudProvider = activeCloudProvider
        self._preset = preset
        self._customInstructions = customInstructions
        self._postProcessingMode = postProcessingMode
        self._postProcessingProvider = postProcessingProvider
        self._languageModel = languageModel
        self._profanityFilter = profanityFilter
        self._userSystemPrompt = userSystemPrompt
        let hasInitialPrompt = !userSystemPrompt.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        self._userPromptEnabled = State(initialValue: hasInitialPrompt)
        self._language = language
        self._englishSpelling = englishSpelling
        self._enableScreenOCR = enableScreenOCR
        self._cloudPostProcessingModel = cloudPostProcessingModel
    }

    private struct LanguageModelOption: Identifiable {
        let id: String
        let displayName: String
        let description: String?
    }

    // DERIVED STATE: Provider is determined by mode, not stored independently
    // This eliminates bugs where validation functions could conflict with each other.
    // - Local mode: Always uses localQwen (on-device processing)
    // - Cloud/Off mode: Uses the stored postProcessingProvider selection
    private var currentProvider: PostProcessingProvider {
        switch postProcessingMode {
        case .local:
            return .localLLM
        case .cloud, .off:
            return PostProcessingProvider(rawValue: postProcessingProvider) ?? .hyperwhisper
        }
    }

    private var isHyperwhisperTranscription: Bool {
        activeCloudProvider == .hyperwhisper
    }

    /// Whether a custom endpoint is currently selected for post-processing
    /// Custom endpoints have their model embedded in the endpoint config, not in languageModel
    private var isCustomEndpointSelected: Bool {
        CustomPostProcessingEndpoint.isCustomProviderString(postProcessingProvider)
    }

    private var hasPostProcessingAPIKey: Bool {
        // CUSTOM ENDPOINTS:
        // Custom endpoints may or may not require API keys - always allow them
        // The user can test the endpoint to verify it works
        if isCustomEndpointSelected {
            return true
        }
        return currentProvider.requiresAPIKey ? settingsManager.hasPostProcessingAPIKey(for: currentProvider) : true
    }

    private var hasLocalModel: Bool {
        !localModelManager.downloadedModels.isEmpty
    }

    /// Whether the current on-device (`.local`) selection can't actually run, so the
    /// editor should silently switch to Off (never Cloud — we never route the user's
    /// text to a cloud LLM without their say-so). Mirrors the policy in
    /// `PersistenceController.repairBrokenLocalModes` so editor + launch + restore
    /// behave identically. Rosetta (Apple-Silicon hardware) is NOT treated as broken —
    /// a native relaunch fixes it and the Model Library shows that nudge.
    private var localPostProcessingIsBroken: Bool {
        guard postProcessingMode == .local else { return false }
        if !SystemCapability.current.isAppleSiliconHardware { return true } // Intel
        let id = languageModel.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else { return true }
        if localModelManager.downloadingModels.contains(id) { return false } // mid-download — keep intent
        guard localModelManager.downloadedModels.contains(where: { $0.id == id }) else { return true }
        if case .invalid = localModelManager.checksumStates[id] { return true }
        return false
    }

    private var shouldShowPostProcessingPrivacyNotice: Bool {
        postProcessingMode == .cloud && currentProvider != .hyperwhisper
    }

    private var languageModelOptions: [LanguageModelOption] {
        if currentProvider == .localLLM {
            return localModelManager.downloadedModels.map { model in
                LanguageModelOption(id: model.id, displayName: model.displayName, description: model.notes.isEmpty ? nil : model.notes)
            }
        }

        return PostProcessingModels.models(for: currentProvider).map { model in
            LanguageModelOption(
                id: model.id,
                displayName: model.displayName,
                description: model.description
            )
        }
    }

    private var availablePostProcessingModes: [PostProcessingMode] {
        // All post-processing modes available for all presets.
        // When mode is Off, the formatting style picker is hidden so there's no
        // confusion about instructions being silently ignored.
        if LlamaServerController.isAppleSilicon {
            return PostProcessingMode.allCases
        } else {
            // Intel Macs can't run local LLM models
            return PostProcessingMode.allCases.filter { $0 != .local }
        }
    }

    /// Available cloud post-processing providers
    ///
    /// DECOUPLED ARCHITECTURE:
    /// HyperWhisper Cloud post-processing can now be used independently of transcription provider.
    /// Users can mix any transcription provider with any post-processing provider:
    /// - Local Whisper transcription + HyperWhisper Cloud post-processing
    /// - OpenAI transcription + HyperWhisper Cloud post-processing
    /// - HyperWhisper Cloud transcription + OpenAI post-processing
    /// - etc.
    ///
    /// The /post-process endpoint has separate billing from transcription.
    private var availableCloudProviders: [PostProcessingProvider] {
        // Drop providers whose health is anything but .healthy so the
        // picker can't surface dead options. Always preserve the
        // currently-selected provider so we don't yank a saved choice
        // out from under the user — the row beside the picker shows a
        // "Reconnect" link when the selection is unhealthy. Providers
        // that don't require a key (HyperWhisper Cloud) always pass.
        let current = PostProcessingProvider(rawValue: postProcessingProvider)
        return PostProcessingProvider.allCases.filter { provider in
            if provider.isLocal { return false }
            if !provider.requiresAPIKey { return true }
            if provider == current { return true }
            return cloudHealth.status(for: provider) == .healthy
        }
    }

    /// BYOK ("Your provider") post-processing providers — `availableCloudProviders`
    /// minus HyperWhisper Cloud, which is its own Source segment.
    private var availableDirectPostProcessingProviders: [PostProcessingProvider] {
        availableCloudProviders.filter { $0 != .hyperwhisper }
    }

    /// The engine (provider) that owns the currently-selected cloud
    /// post-processing model — first axis of the Engine + Model split.
    private var selectedPostProcessingEngine: CloudPostProcessingEngine {
        CloudPostProcessingEngine.engine(for: CloudPostProcessingModel.fromStorageValue(cloudPostProcessingModel))
    }

    /// Cloud Source toggle: true = HyperWhisper Cloud, false = Your provider (BYOK).
    /// Backed by `postProcessingProvider` — no new storage.
    private var postProcessingCloudSource: Binding<Bool> {
        Binding(
            get: {
                // A custom OpenAI-compatible endpoint is "Your provider", not
                // HyperWhisper Cloud — its provider string isn't a valid
                // PostProcessingProvider rawValue, so guard it before the
                // `?? .hyperwhisper` fallback would misclassify it.
                if isCustomEndpointSelected { return false }
                return (PostProcessingProvider(rawValue: postProcessingProvider) ?? .hyperwhisper) == .hyperwhisper
            },
            set: { useHyperwhisper in
                if useHyperwhisper {
                    // Remember the BYOK/custom provider we're leaving so toggling
                    // back restores it rather than reseeding the first available one.
                    if (PostProcessingProvider(rawValue: postProcessingProvider) ?? .hyperwhisper) != .hyperwhisper
                        || isCustomEndpointSelected {
                        lastDirectPostProcessingProvider = postProcessingProvider
                    }
                    postProcessingProvider = PostProcessingProvider.hyperwhisper.rawValue
                } else {
                    // Restore a previously-chosen direct provider if it's still
                    // usable; otherwise seed the first available direct provider.
                    if let saved = lastDirectPostProcessingProvider,
                       let savedProvider = PostProcessingProvider(rawValue: saved),
                       availableDirectPostProcessingProviders.contains(savedProvider) {
                        postProcessingProvider = saved
                    } else if let saved = lastDirectPostProcessingProvider,
                              customEndpointManager.isValidCustomProvider(saved) {
                        // A saved custom OpenAI-compatible endpoint string isn't a
                        // PostProcessingProvider rawValue; restore it directly.
                        postProcessingProvider = saved
                    } else {
                        let firstDirect = availableDirectPostProcessingProviders.first ?? .openai
                        postProcessingProvider = firstDirect.rawValue
                    }
                }
            }
        )
    }

    /// Engine axis for HyperWhisper Cloud post-processing. Selecting an engine
    /// seeds `cloudPostProcessingModel` to that engine's default model.
    private var postProcessingEngine: Binding<CloudPostProcessingEngine> {
        Binding(
            get: { selectedPostProcessingEngine },
            set: { engine in cloudPostProcessingModel = engine.defaultModel.rawValue }
        )
    }

    private func postProcessingEngineLabel(_ engine: CloudPostProcessingEngine) -> String {
        engine.isRecommended
            ? "\(engine.displayName) (\("modes.badge.recommended".localized))"
            : engine.displayName
    }

    private func postProcessingModelLabel(_ model: CloudPostProcessingModel) -> String {
        selectedPostProcessingEngine.isRecommendedModel(model)
            ? "\(model.displayName) (\("modes.badge.recommended".localized))"
            : model.displayName
    }

    private var languageModelSelectionEnabled: Bool {
        postProcessingMode.allowsModelSelection && !languageModelOptions.isEmpty
    }

    private var languageModelLabel: String {
        currentProvider.isLocal ? "modes.postProcessing.localModel".localized : "modes.postProcessing.languageModel".localized
    }

    private func ensureValidLanguageModelSelection() {
        // CUSTOM ENDPOINTS: Skip validation - model is embedded in endpoint config
        // Custom endpoints don't use the languageModel field; their model comes from
        // CustomPostProcessingEndpoint.modelName, so there's nothing to validate here.
        guard !isCustomEndpointSelected else { return }

        let options = languageModelOptions
        let current = languageModel

        // NOTE: Changes are made synchronously to ensure UI renders with correct selection
        if options.isEmpty {
            guard !current.isEmpty else { return }
            languageModel = ""
            return
        }

        guard !options.contains(where: { $0.id == current }) else { return }

        // CASE-INSENSITIVE RESOLUTION:
        // Handles stored ids that differ only in casing from the current catalog (e.g.
        // legacy Gemma 4 12B uppercase → current lowercase). Rewrites the stored id to
        // the canonical casing instead of dropping the user's selection. This avoids the
        // SwiftUI Picker fault `selection "..." is invalid and does not have an associated tag`
        // when the first body evaluation in .onAppear races against the assignment.
        if let canonical = options.first(where: { $0.id.caseInsensitiveCompare(current) == .orderedSame })?.id {
            languageModel = canonical
            return
        }

        let fallback = options.first?.id ?? ""
        languageModel = fallback
    }

    /// Coerce `cloudPostProcessingModel` to a selectable value when the stored
    /// engine/model isn't in the catalog-driven picker lists (e.g. an engine
    /// gated `enabled: false` on the backend, or a removed model). Without this
    /// the SwiftUI Picker faults with "selection is invalid and does not have an
    /// associated tag". Only runs while HyperWhisper Cloud post-processing is the
    /// active provider — BYOK uses the separate languageModel picker.
    private func ensureValidCloudPostProcessingModel() {
        guard postProcessingMode == .cloud,
              currentProvider == .hyperwhisper,
              !isCustomEndpointSelected else { return }

        let selectedModel = CloudPostProcessingModel.fromStorageValue(cloudPostProcessingModel)
        let engine = CloudPostProcessingEngine.engine(for: selectedModel)
        let engines = CloudPostProcessingEngine.allCases

        // Engine no longer offered → reset to the recommended (or first) engine's default model.
        guard engines.contains(where: { $0.id == engine.id }) else {
            let fallbackEngine = engines.first(where: { $0.isRecommended }) ?? engines.first
            let replacement = fallbackEngine?.defaultModel.rawValue ?? CloudPostProcessingModel.fallback.rawValue
            if cloudPostProcessingModel != replacement { cloudPostProcessingModel = replacement }
            return
        }

        // Engine valid but the model id isn't in its list → reset to engine default.
        if !engine.models.contains(where: { $0.id == selectedModel.id }) {
            let replacement = engine.defaultModel.rawValue
            if cloudPostProcessingModel != replacement { cloudPostProcessingModel = replacement }
        } else if cloudPostProcessingModel != selectedModel.rawValue {
            // Normalize a legacy raw value to its provider-qualified key so the
            // Picker tag matches exactly.
            cloudPostProcessingModel = selectedModel.rawValue
        }
    }

    /// CLOUD MODE PICKER VALIDATION: Ensures postProcessingProvider is valid for cloud mode.
    /// Only applies to cloud mode - local mode uses derived provider (see currentProvider).
    private func ensureValidCloudProvider() {
        guard postProcessingMode == .cloud else { return }

        // CUSTOM ENDPOINTS: Skip validation - custom endpoints are always valid
        // They're not in availableCloudProviders but are still valid choices
        guard !isCustomEndpointSelected else { return }

        let available = availableCloudProviders
        guard !available.contains(where: { $0.rawValue == postProcessingProvider }) else { return }

        // Auto-correct to first available cloud provider
        if let fallback = available.first {
            postProcessingProvider = fallback.rawValue
        }
    }

    @ViewBuilder
    private var screenTextPermissionStatus: some View {
        HStack(spacing: 8) {
            if hasScreenRecordingPermission {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundColor(.green)
                    .font(.caption)
                Text(LocalizedStringKey("modes.screenText.permissionGranted"))
                    .font(.caption)
                    .foregroundColor(.secondary)
            } else {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundColor(.orange)
                    .font(.caption)
                Text(LocalizedStringKey("modes.screenText.permissionRequired"))
                    .font(.caption)
                    .foregroundColor(.orange)
                Spacer()
                Button {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
                } label: {
                    Text(LocalizedStringKey("modes.screenText.openSettings"))
                        .font(.caption)
                }
                .buttonStyle(.link)
            }
            if hasScreenRecordingPermission {
                Spacer()
            }
        }
        .padding(8)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(hasScreenRecordingPermission ? Color.green.opacity(0.1) : Color.orange.opacity(0.1))
        )
        .task {
            hasScreenRecordingPermission = await ScreenOCRCapture.shared.hasScreenRecordingPermission()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task {
                hasScreenRecordingPermission = await ScreenOCRCapture.shared.hasScreenRecordingPermission()
            }
        }
    }

    var body: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 16) {
                Label(LocalizedStringKey("modes.postProcessing.sectionTitle"), systemImage: "brain")
                    .font(.headline)
                    .foregroundColor(.secondary)

                VStack(spacing: 12) {
                    // Post-Processing Mode Picker
                    HStack {
                        Text(localized: "modes.postProcessing.modeLabel")
                            .frame(width: 120, alignment: .leading)

                        Picker("", selection: $postProcessingMode) {
                            ForEach(availablePostProcessingModes, id: \.self) { mode in
                                Text(mode.displayName).tag(mode)
                            }
                        }
                        .pickerStyle(.menu)
                        .labelsHidden()

                        Button { showPostProcessingInfo.toggle() } label: {
                            Image(systemName: "info.circle")
                                .foregroundColor(.secondary)
                        }
                        .buttonStyle(.plain)
                        .popover(isPresented: $showPostProcessingInfo, arrowEdge: .trailing) {
                            VStack(alignment: .leading, spacing: 8) {
                                Text(localized: "modes.postProcessing.modeTitle")
                                    .font(.headline)
                                Text(localized: "modes.postProcessing.modeDescription")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                            .padding()
                            .frame(width: 300)
                        }

                        Spacer()
                    }

                    if postProcessingMode != .off {
                        Divider()

                        PresetPickerView(preset: $preset, customInstructions: $customInstructions)

                        Divider()

                        switch postProcessingMode {
                        case .cloud:
                            // Source: HyperWhisper Cloud vs Your provider (BYOK).
                            // Backed by postProcessingProvider == "hyperwhisper".
                            HStack {
                                Text(localized: "modes.source.label")
                                    .frame(width: 120, alignment: .leading)
                                Picker("", selection: postProcessingCloudSource) {
                                    Text(localized: "modes.source.hyperwhisperCloud").tag(true)
                                    Text(localized: "modes.source.yourProvider").tag(false)
                                }
                                .pickerStyle(.segmented)
                                .labelsHidden()
                            }

                            if currentProvider == .hyperwhisper && !isCustomEndpointSelected {
                                // Engine + Model — pure UI over CloudPostProcessingModel.
                                Divider()
                                HStack {
                                    Text(localized: "modes.field.provider")
                                        .frame(width: 120, alignment: .leading)
                                    Picker("", selection: postProcessingEngine) {
                                        ForEach(CloudPostProcessingEngine.allCases) { engine in
                                            Text(postProcessingEngineLabel(engine)).tag(engine)
                                        }
                                    }
                                    .pickerStyle(.menu)
                                    .labelsHidden()
                                    Spacer()
                                }
                                HStack {
                                    Text(localized: "modes.field.model")
                                        .frame(width: 120, alignment: .leading)
                                    Picker("", selection: $cloudPostProcessingModel) {
                                        ForEach(selectedPostProcessingEngine.models) { model in
                                            Text(postProcessingModelLabel(model)).tag(model.rawValue)
                                        }
                                    }
                                    .pickerStyle(.menu)
                                    .labelsHidden()
                                    Spacer()
                                }
                            } else {
                                // Your provider (BYOK) — provider dropdown (no ⓘ).
                                Divider()
                                HStack {
                                    Text(localized: "modes.postProcessing.providerLabel")
                                        .frame(width: 120, alignment: .leading)

                                    Picker("", selection: $postProcessingProvider) {
                                        ForEach(availableDirectPostProcessingProviders, id: \.id) { provider in
                                            Text(provider.displayName).tag(provider.rawValue)
                                        }
                                        // CUSTOM ENDPOINTS SECTION:
                                        // Shows user-configured OpenAI-compatible endpoints after built-in providers
                                        if !customEndpointManager.endpoints.isEmpty {
                                            Divider()
                                            ForEach(customEndpointManager.endpoints) { endpoint in
                                                Text(endpoint.name).tag(endpoint.providerString)
                                            }
                                        }
                                    }
                                    .pickerStyle(.menu)
                                    .labelsHidden()
                                    .onChange(of: postProcessingProvider) { _, newValue in
                                        // CUSTOM ENDPOINT: Skip model auto-correction for custom endpoints
                                        // Custom endpoints have their model embedded in the endpoint config
                                        if CustomPostProcessingEndpoint.isCustomProviderString(newValue) {
                                            return
                                        }
                                        if let newProvider = PostProcessingProvider(rawValue: newValue) {
                                            if PostProcessingModels.model(withId: languageModel, provider: newProvider) == nil,
                                               let defaultModel = PostProcessingModels.defaultModel(for: newProvider) {
                                                languageModel = defaultModel.id
                                            }
                                        }
                                    }

                                    Spacer()
                                }
                            }

                            if currentProvider.requiresAPIKey && cloudHealth.status(for: currentProvider) != .healthy {
                                HStack(spacing: 8) {
                                    Image(systemName: "exclamationmark.triangle.fill")
                                        .font(.caption)
                                        .foregroundColor(.orange)
                                    Text("modes.provider.needsKey".localized(arguments: currentProvider.displayName))
                                        .font(.caption)
                                        .foregroundColor(.primary)
                                    Spacer()
                                    Button {
                                        dismiss()
                                        DispatchQueue.main.async {
                                            appState.navigateToModelLibraryAPIKeys()
                                        }
                                    } label: {
                                        Text(localized: "modes.provider.manageInLibrary")
                                    }
                                    .buttonStyle(.link)
                                    .font(.caption)
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(10)
                                .background(
                                    RoundedRectangle(cornerRadius: 8)
                                        .fill(Color.orange.opacity(0.15))
                                )
                                .padding(.top, 6)
                            } else if currentProvider == .openai && hasPostProcessingAPIKey {
                                HStack(alignment: .top, spacing: 8) {
                                    Image(systemName: "lightbulb.fill")
                                        .font(.caption)
                                        .foregroundColor(.yellow)
                                    Text(localized: "modes.postProcessing.modelRecommendation")
                                        .font(.caption)
                                        .foregroundColor(.secondary)
                                        .fixedSize(horizontal: false, vertical: true)
                                    Spacer()
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(10)
                                .background(
                                    RoundedRectangle(cornerRadius: 8)
                                        .fill(Color.blue.opacity(0.12))
                                )
                                .padding(.top, 6)
                            } else if currentProvider == .anthropic && hasPostProcessingAPIKey {
                                HStack(alignment: .top, spacing: 8) {
                                    Image(systemName: "lightbulb.fill")
                                        .font(.caption)
                                        .foregroundColor(.yellow)
                                    Text(localized: "modes.postProcessing.anthropicRecommendation")
                                        .font(.caption)
                                        .foregroundColor(.secondary)
                                        .fixedSize(horizontal: false, vertical: true)
                                    Spacer()
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(10)
                                .background(
                                    RoundedRectangle(cornerRadius: 8)
                                        .fill(Color.blue.opacity(0.12))
                                )
                                .padding(.top, 6)
                            }

                        // Show divider before Language Model section (hidden for HyperWhisper Cloud and custom endpoints)
                        if currentProvider != .hyperwhisper && !isCustomEndpointSelected {
                            Divider()
                        }

                    case .local:
                        if SystemCapability.current == .needsNativeRelaunch {
                            HStack(alignment: .top, spacing: 8) {
                                Image(systemName: "exclamationmark.triangle.fill")
                                    .foregroundColor(.orange)
                                    .font(.caption)
                                Text("transcription.guidance.needsNativeRelaunch".localized)
                                    .font(.caption)
                                    .foregroundColor(.orange)
                                    .fixedSize(horizontal: false, vertical: true)
                                Spacer()
                            }
                            .padding(8)
                            .background(
                                RoundedRectangle(cornerRadius: 6)
                                    .fill(Color.orange.opacity(0.1))
                            )
                            Divider()
                        }
                        if !hasLocalModel {
                                HStack(alignment: .top, spacing: 8) {
                                    Image(systemName: "exclamationmark.triangle.fill")
                                        .foregroundColor(.orange)
                                        .font(.caption)
                                    VStack(alignment: .leading, spacing: 4) {
                                        Text(localized: "modes.postProcessing.localRequirement")
                                            .font(.caption)
                                            .foregroundColor(.orange)
                                        Button {
                                            dismiss()
                                            DispatchQueue.main.async {
                                                appState.selectedNavigationItem = .modelLibrary
                                            }
                                        } label: {
                                            Text(localized: "modes.postProcessing.download")
                                        }
                                        .buttonStyle(.link)
                                        .font(.caption)
                                    }
                                    Spacer()
                                }
                                .padding(8)
                                .background(
                                    RoundedRectangle(cornerRadius: 6)
                                        .fill(Color.orange.opacity(0.1))
                                )
                                Divider()
                            }

                        case .off:
                            EmptyView()
                        }

                        // Language Model selection
                        // Hidden for:
                        // - HyperWhisper Cloud: Uses built-in post-processing, no model selection needed
                        // - Custom endpoints: Model is embedded in the endpoint config (CustomPostProcessingEndpoint.modelName)
                        if currentProvider != .hyperwhisper && !isCustomEndpointSelected {
                            HStack {
                                Text(languageModelLabel)
                                    .frame(width: 120, alignment: .leading)
                                    .opacity(languageModelSelectionEnabled ? 1.0 : 0.5)

                                Picker("", selection: $languageModel) {
                                    ForEach(languageModelOptions) { option in
                                        Text(option.displayName).tag(option.id)
                                    }
                                }
                                .pickerStyle(.menu)
                                .labelsHidden()
                                .disabled(!languageModelSelectionEnabled)
                                .opacity(languageModelSelectionEnabled ? 1.0 : 0.5)

                                Button { showLanguageModelInfo.toggle() } label: {
                                    Image(systemName: "info.circle")
                                        .foregroundColor(.secondary)
                                }
                                .buttonStyle(.plain)
                                .disabled(!languageModelSelectionEnabled)
                                .opacity(languageModelSelectionEnabled ? 1.0 : 0.5)
                                .popover(isPresented: $showLanguageModelInfo, arrowEdge: .trailing) {
                                    if let option = languageModelOptions.first(where: { $0.id == languageModel }) {
                                        VStack(alignment: .leading, spacing: 8) {
                                            Text(option.displayName)
                                                .font(.headline)
                                            if let description = option.description, !description.isEmpty {
                                                Text(description)
                                                    .font(.caption)
                                                    .foregroundColor(.secondary)
                                                    .fixedSize(horizontal: false, vertical: true)
                                            }
                                        }
                                        .padding()
                                        .frame(width: 300)
                                    }
                                }

                                Spacer()
                            }
                        }

                        if currentProvider.isLocal {
                            HStack(spacing: 8) {
                                Image(systemName: "desktopcomputer")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                Text(localized: "modes.postProcessing.localInfo")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                Spacer()
                            }
                            .padding(8)
                            .background(
                                RoundedRectangle(cornerRadius: 6)
                                    .fill(Color.gray.opacity(0.1))
                            )
                        }

                        // English spelling variant picker (only shown for English language + post-processing enabled)
                        if postProcessingMode != .off && (language == "en" || language == LanguageData.automaticCode) {
                            Divider()

                            HStack {
                                Text(localized: "modes.englishSpelling.title")
                                    .frame(width: 120, alignment: .leading)

                                Picker("", selection: $englishSpelling) {
                                    ForEach(EnglishSpelling.allCases) { spelling in
                                        Text(spelling.displayName).tag(spelling)
                                    }
                                }
                                .pickerStyle(.menu)
                                .labelsHidden()

                                Button { showEnglishSpellingInfo.toggle() } label: {
                                    Image(systemName: "info.circle")
                                        .foregroundColor(.secondary)
                                }
                                .buttonStyle(.plain)
                                .popover(isPresented: $showEnglishSpellingInfo, arrowEdge: .trailing) {
                                    VStack(alignment: .leading, spacing: 8) {
                                        Text(englishSpelling.displayName)
                                            .font(.headline)
                                        Text(englishSpelling.description)
                                            .font(.caption)
                                            .foregroundColor(.secondary)
                                            .fixedSize(horizontal: false, vertical: true)
                                    }
                                    .padding()
                                    .frame(width: 300)
                                }

                                Spacer()
                            }
                        }

                        Divider()

                        // Profanity filter
                        SettingsToggleRow(
                            title: LocalizedStringKey("modes.toggle.profanity.title"),
                            subtitle: LocalizedStringKey("modes.toggle.profanity.subtitle"),
                            isOn: $profanityFilter,
                            standalone: false
                        )
                        .opacity(1.0)
                        .padding(.leading, -8)

                        Divider()

                        SettingsToggleRow(
                            title: LocalizedStringKey("modes.toggle.screenText.title"),
                            subtitle: LocalizedStringKey("modes.toggle.screenText.subtitle"),
                            isOn: $enableScreenOCR,
                            standalone: false
                        )
                        .padding(.leading, -8)

                        if enableScreenOCR {
                            screenTextPermissionStatus
                        }

                        Divider()

                        SettingsToggleRow(
                            title: LocalizedStringKey("modes.postProcessing.userSystemPrompt.toggleTitle"),
                            subtitle: LocalizedStringKey("modes.postProcessing.userSystemPrompt.toggleSubtitle"),
                            isOn: $userPromptEnabled,
                            standalone: false
                        )
                        .padding(.leading, -8)
                        .onChange(of: userPromptEnabled) { _, enabled in
                            if !enabled {
                                userSystemPrompt = ""
                            }
                        }

                        if userPromptEnabled {
                            VStack(alignment: .leading, spacing: 6) {
                                HStack {
                                    Text(localized: "modes.postProcessing.userSystemPrompt.title")
                                        .font(.subheadline)
                                        .fontWeight(.semibold)
                                    Spacer()
                                    if !userSystemPrompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                                        Button {
                                            userSystemPrompt = ""
                                        } label: {
                                            Text(localized: "modes.postProcessing.userSystemPrompt.clear")
                                                .font(.caption)
                                        }
                                        .buttonStyle(.borderless)
                                        .foregroundColor(.secondary)
                                    }
                                }

                                ZStack(alignment: .topLeading) {
                                    if userSystemPrompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                                        Text(localized: "modes.postProcessing.userSystemPrompt.placeholder")
                                            .foregroundColor(.secondary.opacity(0.7))
                                            .padding(.vertical, 10)
                                            .padding(.horizontal, 12)
                                    }

                                    TextEditor(text: $userSystemPrompt)
                                        .frame(minHeight: 110)
                                        .background(Color(NSColor.textBackgroundColor))
                                        .font(.body.monospaced())
                                        .padding(.vertical, 6)
                                        .padding(.horizontal, 8)
                                }
                                .background(
                                    RoundedRectangle(cornerRadius: 8)
                                        .stroke(Color.secondary.opacity(0.25))
                                )
                            }
                        }
                    }

                    // Info text based on mode
                    if shouldShowPostProcessingPrivacyNotice {
                        HStack {
                            Image(systemName: "lock.shield.fill")
                                .font(.system(size: 11))
                                .foregroundColor(.green)
                            Text("modes.postProcessing.privacyNotice".localized(arguments: currentProvider.displayName))
                                .font(.system(size: 11))
                                .foregroundColor(.green)
                        }
                        .padding(.top, 4)
                    }
                }
            }
            .padding(12)
            .onAppear {
                // Silently turn OFF a broken on-device post-processing selection
                // (Intel hardware, or a missing/invalid local model). We switch to
                // Off — never Cloud — so the user's text is never silently routed to
                // a cloud LLM. Mirrors PersistenceController.repairBrokenLocalModes.
                if localPostProcessingIsBroken {
                    postProcessingMode = .off
                }
                // Refresh Qwen catalog to ensure downloaded models are detected
                localModelManager.refreshCatalog()
                ensureValidCloudProvider()
                ensureValidLanguageModelSelection()
                ensureValidCloudPostProcessingModel()
            }
            .onChange(of: postProcessingProvider) { _, _ in
                ensureValidLanguageModelSelection()
                ensureValidCloudPostProcessingModel()
            }
            .onChange(of: postProcessingMode) { _, newValue in
                if newValue == .off {
                    enableScreenOCR = false
                }
                ensureValidCloudProvider()
                ensureValidLanguageModelSelection()
                ensureValidCloudPostProcessingModel()
            }
            .onChange(of: activeCloudProvider) { _, _ in
                ensureValidCloudProvider()
                ensureValidLanguageModelSelection()
            }
            .onChange(of: userSystemPrompt) { _, newValue in
                let limited = String(newValue.prefix(userSystemPromptCharacterLimit))
                if limited != newValue {
                    userSystemPrompt = limited
                    return
                }
                if !limited.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !userPromptEnabled {
                    userPromptEnabled = true
                }
            }
            .onReceive(localModelManager.$downloadedModels) { _ in
                if currentProvider.isLocal {
                    ensureValidLanguageModelSelection()
                }
            }
        }
        .onChange(of: isHyperwhisperTranscription) { _, _ in
            ensureValidCloudProvider()
            ensureValidLanguageModelSelection()
        }
    }
}
