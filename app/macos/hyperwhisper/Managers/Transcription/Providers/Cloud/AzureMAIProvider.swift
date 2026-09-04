//
//  AzureMAIProvider.swift
//  hyperwhisper
//
//  Microsoft MAI-Transcribe (2 and 1.5) via HyperWhisper Cloud.
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

    /// Catalog entry the two MAI models live under. NOT the `X-STT-Provider`
    /// value above (`azure-mai`, the backend dispatch key) and NOT the catalog
    /// provider key (`microsoftAzureSpeech`).
    static let catalogTier: CloudAccuracyTier = .azureMaiTranscribe

    /// The `X-STT-Model` value for this request.
    ///
    /// Azure MAI serves TWO models on one route — `mai-transcribe-2` (default,
    /// 1.67 credits/min) and `mai-transcribe-1.5` (6.0) — and `X-STT-Model` is
    /// the only place the choice can travel. Sending nothing lets the backend
    /// apply its own default, so a mode pinned to 1.5 transcribed and billed as
    /// 2, with a different `transcribeStyle`.
    ///
    /// Reuses `HyperWhisperCloudProvider.resolvedSTTModelId` — the same
    /// validation the HyperWhisper Cloud tier path runs — so a stale id, a BYOK
    /// id left in the shared `cloudTranscriptionModel` field, or a live-only id
    /// all degrade to the tier default instead of earning a backend 400.
    ///
    /// Static and pure so the resolution is testable without a license manager.
    static func routedModelId(storedModelId: String?) -> String? {
        let resolved = HyperWhisperCloudProvider.resolvedSTTModelId(
            tier: catalogTier,
            storedModelId: storedModelId
        )
        return resolved.isEmpty ? nil : resolved
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        try await HyperWhisperRoutedTranscription.run(
            session: HyperWhisperRoutedTranscription.sharedSession,
            providerHeader: Self.sttProviderHeader,
            providerDisplayName: name,
            audioURL: audioURL,
            language: language,
            mode: mode,
            vocabulary: vocabulary,
            routedModel: Self.routedModelId(storedModelId: mode?.cloudTranscriptionModel),
            licenseManager: licenseManager,
            creditManager: creditManager
        )
    }
}
