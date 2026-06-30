//
//  GoogleChirpProvider.swift
//  hyperwhisper
//
//  Google Cloud Speech-to-Text V2 (Chirp 3) via HyperWhisper Cloud.
//
//  Same routing strategy as AzureMAIProvider — pins `X-STT-Provider: google-chirp`
//  so the Fly backend dispatches to Google Speech V2. No BYOK in v1; auth is
//  license_key or device_id like HyperWhisper Cloud.
//

import Foundation

class GoogleChirpProvider: TranscriptionProvider {
    private let licenseManager: LicenseManager
    private let creditManager: HyperWhisperCloudManager

    init(licenseManager: LicenseManager, creditManager: HyperWhisperCloudManager) {
        self.licenseManager = licenseManager
        self.creditManager = creditManager
    }

    var isAvailable: Bool { true }
    var name: String { "Google Chirp 3" }

    /// X-STT-Provider header value that the Fly backend uses to dispatch
    /// requests to Google Speech V2. Distinct from the catalog provider key
    /// (`googleSpeech`) — do not conflate the two.
    private static let sttProviderHeader = "google-chirp"

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        try await HyperWhisperRoutedTranscription.run(
            session: HyperWhisperRoutedTranscription.sharedSession,
            providerHeader: Self.sttProviderHeader,
            providerDisplayName: name,
            audioURL: audioURL,
            language: language,
            mode: mode,
            vocabulary: vocabulary,
            licenseManager: licenseManager,
            creditManager: creditManager
        )
    }
}
