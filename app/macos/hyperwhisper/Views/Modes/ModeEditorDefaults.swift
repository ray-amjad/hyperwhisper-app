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
    /// licence: the runtime Cloud gate, plus one launch-window exception.
    ///
    /// The runtime gate is `LicenseManager.getTranscriptionIdentifier().isLicensed`
    /// — `licenseStatus == .active` AND a non-empty stored key. Every Cloud
    /// path refuses without it (`HyperWhisperCloudEntitlement.requireLicense`).
    /// `storedKey` is the same non-retrying read that gate makes, and
    /// `.present` is exactly its "non-empty key". So `.active` with the key
    /// `.missing` (a failed Keychain write) or `.unavailable` (an unreadable
    /// Keychain) is NOT licensed, and neither is any status with an
    /// unreadable Keychain: Cloud would throw on the first dictation.
    ///
    /// The exception is `.trial` with a stored key. `licenseStatus` starts at
    /// `.trial` and a stored key leaves it only when `loadStoredLicense()` (or
    /// a backup import's validation) publishes a verdict. With a cached
    /// verdict inside the 7-day grace that happens before the first await;
    /// without one it takes one validation request, whose every failure maps
    /// to `.invalid` and never back to `.trial`. The window is seconds long,
    /// and a key holder who opens the sheet in it keeps the Cloud seeds
    /// instead of saving an On-device mode they never chose.
    static func treatsLicenseAsActive(
        status: LicenseStatus,
        storedKey: RustLicenseStore.StoredLicenseKeyRead
    ) -> Bool {
        guard case .present = storedKey else { return false }
        let passesRuntimeGate = status == .active
        let launchVerdictPending = status == .trial
        return passesRuntimeGate || launchVerdictPending
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
