//
//  ModeLanguageSettings.swift
//  HyperWhisper
//
//  Language selection and vocabulary notice components.
//

import SwiftUI

// MARK: - Vocabulary Unsupported Notice

/// Informational note shown when a model does not support custom vocabulary.
struct VocabularyUnsupportedNotice: View {
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle")
                .foregroundColor(.orange)
            Text(localized: "modes.notice.vocabularyUnsupported")
                .font(.caption)
                .foregroundColor(.primary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 10)
        .padding(.horizontal, 12)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.orange.opacity(0.18))
        )
    }
}

// MARK: - Deepgram Nova-3 Auto-Detect Notice

/// Informational note shown when Deepgram Nova-3 is used with auto-detect language.
/// Nova-3 doesn't support the 'keywords' parameter, and 'keyterm' is ignored when using auto-detect.
/// Users should set a specific language to enable vocabulary boosting.
struct DeepgramNova3AutoDetectNotice: View {
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle")
                .foregroundColor(.orange)
            Text(localized: "modes.notice.deepgramNova3AutoDetect")
                .font(.caption)
                .foregroundColor(.primary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 10)
        .padding(.horizontal, 12)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.orange.opacity(0.18))
        )
    }
}

// MARK: - Language Selection View

/// Language selection component with English-only model support
struct LanguageSelectionView: View {
    @Binding var language: String
    let provider: ProviderType
    let model: String
    // Optional cloud context for dynamic filtering
    var cloudProviderId: String? = nil
    var cloudModelId: String? = nil
    // When false, only shows the picker without the label row (for embedding in other layouts)
    var showLabel: Bool = true

    private static let parakeetModelIdentifier = ParakeetModelManager.Constants.v3ModelId
    private static let parakeetLanguageCodes: [String] = {
        // Derive from the V3 model registry (the source of truth for what the
        // downloadable model actually supports) so the picker — and the Model
        // Library language filter that reuses this list — can never drift from
        // the model metadata. English first, then the rest alphabetically.
        let registryCodes = ParakeetModelManager.Constants.v3Languages.keys
        let rest = registryCodes.filter { $0 != "en" }.sorted()
        return [LanguageData.automaticCode, "en"] + rest
    }()

    private static let qwen3AsrModelIdentifier = Qwen3AsrModelManager.Constants.modelId
    private static let qwen3AsrLanguageCodes: [String] = [
        LanguageData.automaticCode,
        "zh", "en", "yue", "ar", "de", "fr", "es", "pt", "id", "it",
        "ko", "ru", "th", "vi", "ja", "tr", "hi", "ms", "nl", "sv",
        "da", "fi", "pl", "cs", "tl", "fa", "el", "hu", "mk", "ro"
    ]

    private static let speechAnalyzerLanguageCodes: [String] = [
        LanguageData.automaticCode,
        "en", "es", "fr", "de", "it", "pt", "nl", "sv", "da", "no",
        "fi", "ar", "he", "ja", "ko", "ms", "ru", "th", "tr", "vi",
        "yue", "zh", "zh-TW"
    ]

    private var isEnglishOnly: Bool {
        isEnglishOnlyModel(provider: provider, model: model)
    }

    // Compute allowed languages based on provider/model; fall back to full list
    private var allowedLanguageInfos: [LanguageData.LanguageInfo] {
        Self.allowedLanguageInfos(
            provider: provider,
            model: model,
            cloudProviderId: cloudProviderId,
            cloudModelId: cloudModelId
        )
    }

    /// Per-model allowed languages, factored out of the instance so the Model
    /// Library language filter resolves *local* models through the exact same
    /// logic the Mode Editor picker uses (no drift between the two surfaces).
    /// Cloud models are resolved by the library off the shared catalog instead,
    /// so this is primarily the local source of truth. English-only models
    /// (whisper `.en`, Parakeet v2) are handled by `isEnglishOnlyModel` at the
    /// call site, not here.
    static func allowedLanguageInfos(
        provider: ProviderType,
        model: String,
        cloudProviderId: String?,
        cloudModelId: String?
    ) -> [LanguageData.LanguageInfo] {
        if provider == .cloud, let pid = cloudProviderId, let mid = cloudModelId {
            let languageSpecs = STTCapabilities.languages(providerId: pid, modelId: mid)
            if !languageSpecs.isEmpty {
                let infos = languageSpecs.map { spec in
                    LanguageData.info(for: spec.code) ?? LanguageData.LanguageInfo(code: spec.code, displayName: spec.displayName)
                }
                return LanguageData.prioritizeAutomatic(infos)
            }
        }
        if provider == .local, model == Self.parakeetModelIdentifier {
            let infos = LanguageData.languages(for: Self.parakeetLanguageCodes, context: "Parakeet language filter")
            return LanguageData.prioritizeAutomatic(infos)
        }
        if provider == .local, model == Self.qwen3AsrModelIdentifier {
            let infos = LanguageData.languages(for: Self.qwen3AsrLanguageCodes, context: "Qwen3 ASR language filter")
            return LanguageData.prioritizeAutomatic(infos)
        }
        // Nemotron 3.5 (latin / multilingual): the on-disk variant determines the
        // language set. Latin is en/es/fr/it/pt/de only; multilingual is ~40 langs
        // incl. CJK + Arabic. Source of truth lives in NemotronModelManager so the
        // model file and the picker can never drift apart.
        if #available(macOS 14.0, *),
           provider == .local,
           let nemoLanguages = NemotronModelManager.supportedLanguages(forModelId: model) {
            let codes = [LanguageData.automaticCode] + Array(nemoLanguages.keys).sorted()
            let infos = LanguageData.languages(for: codes, context: "Nemotron language filter")
            return LanguageData.prioritizeAutomatic(infos)
        }
        if provider == .local, model == "apple-speech-analyzer" {
            let infos = LanguageData.languages(for: Self.speechAnalyzerLanguageCodes, context: "SpeechAnalyzer language filter")
            return LanguageData.prioritizeAutomatic(infos)
        }
        return LanguageData.prioritizeAutomatic(LanguageData.allLanguages)
    }

    private var allowedLanguages: [(code: String, name: String)] {
        LanguageData.pickerTuples(from: allowedLanguageInfos)
    }

    var body: some View {
        // LANGUAGE SELECTION
        // Hidden for English-only models (.en suffix)
        // These models are optimized for English and don't support other languages
        if !isEnglishOnly {
            if showLabel {
                HStack {
                    Text(localized: "modes.language.title")
                        .frame(width: 80, alignment: .leading)
                    Picker("", selection: $language) {
                        ForEach(allowedLanguages, id: \.code) { lang in
                            Text(lang.name).tag(lang.code)
                        }
                    }
                    .labelsHidden()
                    .onAppear { enforceAllowedLanguage() }
                    .onChange(of: cloudModelId) { _, _ in enforceAllowedLanguage() }
                    .onChange(of: cloudProviderId) { _, _ in enforceAllowedLanguage() }
                    .onChange(of: model) { _, _ in enforceAllowedLanguage() }
                    Spacer()
                }
            } else {
                // Compact mode: just the picker for embedding in other layouts
                Picker("", selection: $language) {
                    ForEach(allowedLanguages, id: \.code) { lang in
                        Text(lang.name).tag(lang.code)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .onAppear { enforceAllowedLanguage() }
                .onChange(of: cloudModelId) { _, _ in enforceAllowedLanguage() }
                .onChange(of: cloudProviderId) { _, _ in enforceAllowedLanguage() }
                .onChange(of: model) { _, _ in enforceAllowedLanguage() }
            }
        } else {
            // Display English-only notice for .en models
            if showLabel {
                HStack {
                    Text(localized: "modes.language.title")
                        .frame(width: 80, alignment: .leading)
                    HStack(spacing: 6) {
                        Image(systemName: "info.circle.fill")
                            .font(.caption)
                            .foregroundColor(.orange)
                        Text(localized: "modes.language.englishOnly")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                    Spacer()
                }
            } else {
                // Compact mode: just the notice for embedding in other layouts
                HStack(spacing: 6) {
                    Image(systemName: "info.circle.fill")
                        .font(.caption)
                        .foregroundColor(.orange)
                    Text(localized: "modes.language.englishOnly")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
        }
    }

    private func enforceAllowedLanguage() {
        let infos = allowedLanguageInfos
        let allowed = Set(infos.map { $0.code })
        if !allowed.contains(language), let first = infos.first?.code {
            language = first
        }
    }
}
