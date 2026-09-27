//
//  ModeEditorDefaults.swift
//  HyperWhisper
//
//  Pure seeds for the CREATE branch of ModeEditorView (issue #873), and the
//  order the On-device picker lists installed models in.
//

import Foundation

enum ModeEditorDefaults {

    /// The macOS 26 system speech model. `ModesView` lists it whenever
    /// `SpeechTranscriber.isAvailable`, whether or not the user ever chose it.
    static let appleSpeechAnalyzerId = "apple-speech-analyzer"

    // MARK: - Licence

    /// Whether the CREATE seeds treat this Mac as holding a HyperWhisper Cloud
    /// licence.
    ///
    /// `LicenseManager.licenseStatus` starts at `.trial` and only moves once
    /// `loadStoredLicense()` has read the secure store and, when its cache is
    /// stale with no verdict inside the grace period, heard back from the
    /// server. So `.trial` alone cannot tell "no key" from "not resolved yet".
    /// A `.trial` status with a stored key — or with a Keychain that could not
    /// be read — keeps today's Cloud seeds; only `.trial` with no stored key,
    /// or a key the server called expired or invalid, counts as unlicensed.
    static func treatsLicenseAsActive(
        status: LicenseStatus,
        storedKey: RustLicenseStore.StoredLicenseKeyRead
    ) -> Bool {
        switch status {
        case .active:
            return true
        case .expired, .invalid:
            return false
        case .trial:
            switch storedKey {
            case .present, .unavailable:
                return true
            case .missing:
                return false
            }
        }
    }

    // MARK: - Transcription

    /// Transcription provider a brand-new mode opens on. HyperWhisper Cloud
    /// needs an account key on every request, so an unlicensed user opens on
    /// On-device whenever the list holds a local model; a licensed user, or an
    /// unlicensed one with nothing on the list, keeps the Cloud default.
    ///
    /// On macOS 26 with `SpeechTranscriber` available the list always holds
    /// `apple-speech-analyzer`, so there an unlicensed user always opens on
    /// On-device, even with no model downloaded. Apple Speech needs no key.
    static func initialProvider(licenseActive: Bool, availableModelIds: [String]) -> ProviderType {
        (licenseActive || availableModelIds.isEmpty) ? .cloud : .local
    }

    /// On-device picker order: the non-Whisper models first, in this order,
    /// then Whisper in `WhisperModel` order, then anything unknown by id.
    private static let nonWhisperOrder: [String: Int] = [
        appleSpeechAnalyzerId: 0,
        "parakeet-tdt-0.6b-v3": 1,
        "qwen3-asr-0.6b": 2,
        NemotronModelManager.Constants.latinModelId: 3,
        NemotronModelManager.Constants.multilingualModelId: 4
    ]
    private static let whisperOrder: [String] = WhisperModel.allCases.map { $0.rawValue }

    /// Installed model ids in the order the On-device picker lists them.
    static func sortedLocalModelIds(_ ids: [String]) -> [String] {
        let whisperOffset = nonWhisperOrder.count
        func rank(_ id: String) -> Int {
            nonWhisperOrder[id]
                ?? whisperOrder.firstIndex(of: id).map { $0 + whisperOffset }
                ?? Int.max
        }
        return ids.sorted { first, second in
            let firstIndex = rank(first)
            let secondIndex = rank(second)
            if firstIndex != secondIndex { return firstIndex < secondIndex }
            return first < second
        }
    }

    /// Local model a brand-new mode opens on: the first installed model in
    /// picker order, passing over `apple-speech-analyzer` when anything else
    /// is installed. Apple Speech heads the picker but is listed on every
    /// macOS 26 Mac without being chosen, and its first use can download
    /// locale assets; the model the user downloaded is the one they set up.
    /// `"base"` when nothing is installed, as before.
    static func initialLocalModel(availableModelIds: [String]) -> String {
        let sorted = sortedLocalModelIds(availableModelIds)
        return sorted.first(where: { $0 != appleSpeechAnalyzerId }) ?? sorted.first ?? "base"
    }

    /// Language a brand-new mode opens on. Automatic, except on an English-only
    /// local model, where it is `"en"` — what the save path persists for that
    /// model and what the editor itself sets when such a model is picked.
    static func initialLanguage(provider: ProviderType, model: String) -> String {
        isEnglishOnlyModel(provider: provider, model: model) ? "en" : LanguageData.automaticCode
    }

    // MARK: - Post-processing

    /// Post-processing a brand-new mode opens on. HyperWhisper Cloud
    /// post-processing needs the same account key as Cloud transcription, so a
    /// mode seeded On-device for an unlicensed user opens with post-processing
    /// off — the same as onboarding's On-device path — and its first dictation
    /// needs no key and no extra download. Every other seed keeps Cloud.
    static func initialPostProcessingMode(licenseActive: Bool, availableModelIds: [String]) -> PostProcessingMode {
        initialProvider(licenseActive: licenseActive, availableModelIds: availableModelIds) == .local ? .off : .cloud
    }
}
