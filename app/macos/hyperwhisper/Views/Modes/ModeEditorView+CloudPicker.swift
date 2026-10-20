//
//  ModeEditorView+CloudPicker.swift
//  HyperWhisper
//
//  The HyperWhisper Cloud Provider → Model picker of the mode editor, with
//  its badges, credits caption and Medical toggle.
//

import SwiftUI

extension ModeEditorView {

    // MARK: - HyperWhisper Cloud Provider → Model picker

    @ViewBuilder
    var hyperwhisperCloudProviderModelPicker: some View {
        VStack(alignment: .leading, spacing: 10) {
            // Level 1 — Provider, i.e. the company. One row per vendor, so the
            // two Google tiers (Transcribe + Gemini) share a row. The selection is
            // the vendor key; the binding maps it back to a tier for storage.
            HStack {
                Text(localized: "modes.field.provider")
                    .frame(width: 80, alignment: .leading)
                Picker("", selection: hyperwhisperCloudVendorBinding) {
                    ForEach(CloudAccuracyTier.pickerVendorGroups) { group in
                        Text(hyperwhisperCloudVendorLabel(group)).tag(group.id)
                    }
                }
                .pickerStyle(.menu)
                .labelsHidden()
                Spacer()
            }

            // Level 2 — Model. Always a dropdown, including for a company with a
            // single model, so the row keeps its shape as you switch provider.
            let models = hyperwhisperCloudModels
            HStack(alignment: .top) {
                Text(localized: "modes.field.model")
                    .frame(width: 80, alignment: .leading)
                Picker("", selection: hyperwhisperCloudModelBinding) {
                    ForEach(models) { model in
                        Text(model.displayName).tag(model.id)
                    }
                }
                .pickerStyle(.menu)
                .labelsHidden()
                Spacer()
            }

            // Badges for the selected model (Preview / No custom vocabulary).
            // Only render the row when a badge actually applies — otherwise the
            // empty HStack adds a blank line and a double gap above the credits
            // caption (e.g. for models with neither a preview nor a vocab flag).
            if let selected = models.first(where: { $0.id == cloudTranscriptionModel }) ?? models.first,
               selected.previewStatus == true || selected.supportsCustomVocabulary == false {
                HStack(spacing: 6) {
                    if selected.previewStatus == true {
                        hyperwhisperCloudBadge(text: "mode.editor.cloudModel.previewHint".localized, color: .orange)
                    }
                    if selected.supportsCustomVocabulary == false {
                        hyperwhisperCloudBadge(text: "mode.editor.cloudModel.noVocabularyHint".localized, color: .secondary)
                    }
                    Spacer()
                }
                .padding(.leading, 88)
            }

            // Credits caption for the SELECTED model.
            let tier = selectedCloudTier
            let perMinute = tier.creditsPerMinute(forModelId: cloudTranscriptionModel)
            if perMinute > 0 {
                // Gate on > 0 so a catalog load failure hides the caption
                // instead of showing a misleading "~0.0 credits/min".
                HStack(spacing: 6) {
                    Circle()
                        .fill(Color.green)
                        .frame(width: 4, height: 4)
                    Text(tier.creditsPerMinuteLabel(forModelId: cloudTranscriptionModel))
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .monospacedDigit()
                    // When the Medical Mode add-on is enabled it bills separately
                    // on top of the per-minute model rate — note it in the caption.
                    if showsMedicalDomainToggle && cloudTranscriptionDomain == "medical" {
                        Text(localized: "mode.editor.cloudModel.medicalAddOnNote")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
                .padding(.leading, 88)
                .help(tier.description)
            }

            // Medical domain toggle — assemblyAI only (Deepgram medical is a
            // model selection, handled by the Model dropdown above).
            if showsMedicalDomainToggle {
                HStack {
                    Text("")
                        .frame(width: 80, alignment: .leading)
                    Toggle(isOn: medicalDomainBinding) {
                        Text(localized: "mode.editor.cloudDomain.medical")
                            .font(.caption)
                    }
                    .toggleStyle(.checkbox)
                    Spacer()
                }
            }
        }
    }

    /// Label for a Provider row — the plain company name, tagged
    /// "(Recommended)" for the company that owns the recommended engine
    /// (ElevenLabs Scribe v2). "(Recommended)" appears on this row only: on the
    /// Model row it used to mean "this provider's default", which read as a
    /// second, conflicting recommendation.
    private func hyperwhisperCloudVendorLabel(_ group: CloudSTTCatalog.VendorGroup) -> String {
        let recommended = group.entries.contains {
            CloudAccuracyTier(rawValue: $0.id)?.isRecommended == true
        }
        return recommended
            ? "\(group.displayName) (\("modes.badge.recommended".localized))"
            : group.displayName
    }

    /// Small pill badge used in the HyperWhisper Cloud model picker.
    private func hyperwhisperCloudBadge(text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2)
            .foregroundColor(color)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(
                RoundedRectangle(cornerRadius: 4)
                    .fill(color.opacity(0.12))
            )
    }
}
