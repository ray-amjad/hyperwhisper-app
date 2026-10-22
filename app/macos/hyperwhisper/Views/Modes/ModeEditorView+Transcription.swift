//
//  ModeEditorView+Transcription.swift
//  HyperWhisper
//
//  The Transcription card of the mode editor: the 3-way Source toggle and
//  its On-device, HyperWhisper Cloud and Your provider (BYOK) sections. The
//  On-device model picker itself stays in ModeEditorView.swift.
//

import SwiftUI

extension ModeEditorView {

    // MARK: - Transcription Model

    var editorTranscriptionModel: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 16) {
                Label(LocalizedStringKey("modes.section.transcription"), systemImage: "cpu")
                    .font(.headline)
                    .foregroundColor(.secondary)

                // 3-way Source segmented control:
                // On-device / HyperWhisper Cloud / Your provider.
                Picker("", selection: transcriptionSource) {
                    ForEach(TranscriptionSource.allCases) { source in
                        Text(source.label).tag(source)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                switch transcriptionSource.wrappedValue {
                case .onDevice:
                    editorLocalTranscription
                case .hyperwhisperCloud:
                    hyperwhisperCloudTranscriptionSection
                case .yourProvider:
                    yourProviderTranscriptionSection
                }
            }
            .padding(12)
        }
    }

    // MARK: - On-device notices

    /// Shown in place of the On-device picker when no local model is installed.
    var noLocalModelsNotice: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundColor(.orange)
            VStack(alignment: .leading, spacing: 2) {
                Text(localized: "modes.notice.noLocalModels.title")
                    .font(.caption)
                    .fontWeight(.medium)
                Text(localized: "modes.notice.noLocalModels.subtitle")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.orange.opacity(0.1))
        )
    }

    /// The mode's stored model is not installed (issue #1434).
    var missingLocalModelNotice: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundColor(.orange)
            Text(localized: "modes.notice.modelNotInstalled")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.orange.opacity(0.1))
        )
    }

    // MARK: - HyperWhisper Cloud transcription

    @ViewBuilder
    private var hyperwhisperCloudTranscriptionSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Two-level Engine → Model picker. Engine (L1) is the accuracy tier
            // (X-STT-Provider); Model (L2) is the selected model within that
            // engine (X-STT-Model). Credits caption reflects the selected model.
            hyperwhisperCloudProviderModelPicker

            Divider()

            cloudTranscriptionLanguageAndNotices
        }
    }

    // MARK: - Your provider (BYOK) transcription

    @ViewBuilder
    private var yourProviderTranscriptionSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Provider picker — BYOK providers only (HyperWhisper Cloud is its
            // own Source segment).
            yourProviderPickerRow

            // Cloud transcription model row. Always a dropdown — a provider
            // with one model (Grok) lists that one entry rather than
            // degrading to a read-only label.
            yourProviderModelRow

            yourProviderModelDetails

            Divider()

            cloudTranscriptionLanguageAndNotices

            // Gemini custom transcription instructions
            if currentCloudProvider == .gemini {
                geminiCustomPromptEditor
            }

            // A missing or unhealthy key. The notice is in ModeEditorView.swift.
            if currentProviderNeedsAttention {
                yourProviderNeedsKeyNotice
            }
        }
    }

    /// The BYOK Provider row. Changing provider reseeds its default model and
    /// moves a Cloud post-processing provider off HyperWhisper Cloud.
    private var yourProviderPickerRow: some View {
        HStack {
            Text(localized: "modes.field.provider")
                .frame(width: 80, alignment: .leading)
            Picker("", selection: $cloudProvider) {
                ForEach(availableDirectCloudProviders, id: \.id) { provider in
                    Text(provider.displayName).tag(provider.rawValue)
                }
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .onChange(of: cloudProvider) { _, newProvider in
                guard let provider = CloudProvider.parse(newProvider) else { return }
                showAllCloudTranscriptionModels = false
                cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: provider)
                cloudTranscriptionDomain = nil

                guard postProcessingMode == .cloud else { return }

                // Compare through the enum: a mode seeded or restored from
                // another head stores "hyperwhispercloud" for this provider,
                // and a raw-string match would leave post-processing pointed
                // at HyperWhisper Cloud after the user moved transcription
                // to a BYOK provider.
                let currentPostProvider = PostProcessingProvider(rawValue: postProcessingProvider)
                if currentPostProvider == .hyperwhisper {
                    postProcessingProvider = PostProcessingProvider.openai.rawValue
                }
            }
            Spacer()
        }
    }

    /// The BYOK Model row, with the model info popover.
    private var yourProviderModelRow: some View {
        HStack {
            Text(localized: "modes.field.model")
                .frame(width: 80, alignment: .leading)
            Picker("", selection: $cloudTranscriptionModel) {
                ForEach(cloudTranscriptionModelsForPicker, id: \.id) { model in
                    Text(model.displayName)
                        .tag(model.id)
                }
            }
            .pickerStyle(.menu)
            .labelsHidden()

            Button { showCloudModelInfo.toggle() } label: {
                Image(systemName: "info.circle")
                    .foregroundColor(.secondary)
            }
            .buttonStyle(.plain)
            .popover(isPresented: $showCloudModelInfo, arrowEdge: .trailing) {
                if let modelInfo = CloudTranscriptionModels.model(
                    withId: cloudTranscriptionModel,
                    provider: currentCloudProvider
                ) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(modelInfo.displayName)
                            .font(.headline)
                        Text(modelInfo.description)
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                        if let perSecond = modelInfo.pricePerSecond {
                            let perMinute = perSecond * 60.0
                            HStack(spacing: 6) {
                                Image(systemName: "dollarsign.circle")
                                    .foregroundColor(.green)
                                Text(String(format: "modes.model.pricing".localized, perMinute))
                                    .font(.caption)
                            }
                        }
                    }
                    .padding()
                    .frame(width: 350)
                }
            }
            .help("modes.help.model".localized)

            Spacer()
        }
    }

    /// The selected model's summary line and the "Show all models" box.
    @ViewBuilder
    private var yourProviderModelDetails: some View {
        if let modelSummary = selectedCloudTranscriptionModelSummary {
            HStack(alignment: .top) {
                Text("")
                    .frame(width: 80, alignment: .leading)
                Text(modelSummary)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .lineLimit(3)
                Spacer()
            }
        }

        if canShowAllCloudTranscriptionModels {
            HStack {
                Text("")
                    .frame(width: 80, alignment: .leading)
                Toggle(isOn: $showAllCloudTranscriptionModels) {
                    Text("Show all models")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                .toggleStyle(.checkbox)

                Spacer()
            }
        }
    }

    /// Gemini's custom transcription instructions, capped at
    /// `geminiCustomPromptCharacterLimit`.
    private var geminiCustomPromptEditor: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(localized: "modes.gemini.customPrompt.title")
                .font(.caption)
                .fontWeight(.medium)
                .foregroundColor(.secondary)

            TextEditor(text: $geminiCustomPrompt)
                .font(.body)
                .frame(height: 80)
                .padding(6)
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color(NSColor.textBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 6)
                        .stroke(Color(NSColor.separatorColor), lineWidth: 1)
                )
                .onChange(of: geminiCustomPrompt) { _, newValue in
                    if newValue.count > geminiCustomPromptCharacterLimit {
                        geminiCustomPrompt = String(newValue.prefix(geminiCustomPromptCharacterLimit))
                    }
                }

            Text(localized: "modes.gemini.customPrompt.helper")
                .font(.caption2)
                .foregroundColor(.secondary)
        }
    }

    // MARK: - Shared cloud language + notices

    /// Language picker + vocabulary / Deepgram notices shared by both cloud
    /// Source branches. For HyperWhisper Cloud the language context is derived
    /// from the SELECTED tier's routed upstream provider id (X-STT-Provider) and
    /// model id rather than the literal "hyperwhisper" wrapper, which
    /// STTCapabilities does not register.
    @ViewBuilder
    private var cloudTranscriptionLanguageAndNotices: some View {
        LanguageSelectionView(
            language: $language,
            provider: .cloud,
            model: cloudTranscriptionModel,
            cloudProviderId: languageFilterCloudProviderId,
            cloudModelId: languageFilterCloudModelId,
            cloudTierId: languageFilterCloudTierId
        )

        // Show vocabulary warning for providers that don't support it
        if showVocabularyUnsupportedNotice {
            VocabularyUnsupportedNotice()
        }

        // Show warning when Deepgram Nova-3 is used with auto-detect language
        if showDeepgramNova3AutoDetectNotice {
            DeepgramNova3AutoDetectNotice()
        }
    }
}
