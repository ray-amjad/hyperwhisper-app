//
//  ModeEditorView+Content.swift
//  HyperWhisper
//
//  The mode editor's scrolling form: the basic settings, transcription,
//  language/post-processing and punctuation cards in order.
//

import SwiftUI

extension ModeEditorView {

    // MARK: - Content

    var editorContent: some View {
        ScrollView {
            VStack(spacing: 24) {
                editorBasicSettings

                editorTranscriptionModel

                LanguageProcessingSettingsView(
                    activeCloudProvider: provider == .cloud ? currentCloudProvider : nil,
                    preset: $preset,
                    customInstructions: $customInstructions,
                    postProcessingMode: $postProcessingMode,
                    postProcessingProvider: $postProcessingProvider,
                    languageModel: $languageModel,
                    profanityFilter: $profanityFilter,
                    userSystemPrompt: $userSystemPrompt,
                    language: $language,
                    englishSpelling: $englishSpelling,
                    enableScreenOCR: $enableScreenOCR,
                    cloudPostProcessingModel: $cloudPostProcessingModel
                )

                // Punctuation card
                GroupBox {
                    VStack(alignment: .leading, spacing: 16) {
                        Label(LocalizedStringKey("modes.section.punctuation"), systemImage: "textformat")
                            .font(.headline)
                            .foregroundColor(.secondary)

                        VStack(spacing: 12) {
                            // Punctuation & capitalization are LLM instructions — only show when post-processing is enabled
                            if postProcessingMode != .off {
                                SettingsToggleRow(
                                    title: LocalizedStringKey("modes.toggle.punctuation.title"),
                                    subtitle: LocalizedStringKey("modes.toggle.punctuation.subtitle"),
                                    isOn: $punctuation,
                                    standalone: false
                                )

                                if punctuation {
                                    Divider()

                                    SettingsToggleRow(
                                        title: LocalizedStringKey("modes.toggle.removeTrailingPeriod.title"),
                                        subtitle: LocalizedStringKey("modes.toggle.removeTrailingPeriod.subtitle"),
                                        isOn: $removeTrailingPeriod,
                                        standalone: false
                                    )
                                }

                                Divider()

                                SettingsToggleRow(
                                    title: LocalizedStringKey("modes.toggle.capitalization.title"),
                                    subtitle: LocalizedStringKey("modes.toggle.capitalization.subtitle"),
                                    isOn: $capitalization,
                                    standalone: false
                                )
                            } else {
                                // When post-processing is off, only show remove trailing period (string operation, works independently)
                                SettingsToggleRow(
                                    title: LocalizedStringKey("modes.toggle.removeTrailingPeriod.title"),
                                    subtitle: LocalizedStringKey("modes.toggle.removeTrailingPeriod.subtitle"),
                                    isOn: $removeTrailingPeriod,
                                    standalone: false
                                )
                            }
                        }
                    }
                    .padding(12)
                }
            }
            .padding(20)
        }
    }
}
