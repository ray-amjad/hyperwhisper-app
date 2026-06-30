//
//  AzureMAIProvider.swift
//  hyperwhisper
//
//  Microsoft MAI-Transcribe 1.5 via HyperWhisper Cloud.
//
//  This provider routes through the Fly transcribe service (same path as
//  HyperWhisperCloudProvider) but pins `X-STT-Provider: azure-mai` so the
//  backend dispatches to Azure Speech. There is no BYOK path in v1 — auth is
//  always license_key or device_id, identical to HyperWhisper Cloud.
//

import Foundation

class AzureMAIProvider: TranscriptionProvider {
    private let licenseManager: LicenseManager
    private let creditManager: HyperWhisperCloudManager

    init(licenseManager: LicenseManager, creditManager: HyperWhisperCloudManager) {
        self.licenseManager = licenseManager
        self.creditManager = creditManager
    }

    var isAvailable: Bool { true }
    var name: String { "Microsoft MAI-Transcribe" }

    /// X-STT-Provider header value that the Fly backend uses to dispatch
    /// requests to Azure Speech. Distinct from the catalog provider key
    /// (`microsoftAzureSpeech`) — do not conflate the two.
    private static let sttProviderHeader = "azure-mai"

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
