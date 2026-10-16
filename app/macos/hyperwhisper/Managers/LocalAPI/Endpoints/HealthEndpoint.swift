//
//  HealthEndpoint.swift
//  hyperwhisper
//
//  Implements `GET /health`. Reuses CloudProviderHealthManager's existing
//  per-provider probes (via `healthSnapshot()`) plus the local model managers'
//  download state. Read-only: never triggers a probe.
//

import Foundation
import FlyingFox
#if canImport(Speech)
import Speech
#endif

enum HealthEndpoint {

    static var transcriptionProviders: [CloudProvider] { CloudProvider.allCases }

    @MainActor
    static func handle(
        port: UInt16,
        cloudHealth: CloudProviderHealthManager?,
        whisperModelManager: WhisperModelManager?,
        parakeetModelManager: ParakeetModelManager?,
        nemotronModelManager: NemotronModelManager?,
        qwen3AsrModelManager: Qwen3AsrModelManager?,
        localModelManager: LocalModelManager?,
        settingsManager: SettingsManager?,
        licenseManager: LicenseManager?
    ) async -> HTTPResponse {
        let entries = providerEntries(
            snapshot: cloudHealth?.healthSnapshot(),
            settingsManager: settingsManager,
            licenseManager: licenseManager
        )

        let local = collectLocalModels(
            whisperModelManager: whisperModelManager,
            parakeetModelManager: parakeetModelManager,
            nemotronModelManager: nemotronModelManager,
            qwen3AsrModelManager: qwen3AsrModelManager,
            localModelManager: localModelManager
        )

        let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        let response = HealthResponse(
            ok: true,
            app_version: appVersion,
            api_version: LocalAPIVersion.current,
            port: port,
            pid: ProcessInfo.processInfo.processIdentifier,
            providers: entries.providers,
            post_processing_providers: entries.postProcessing,
            local_models: local
        )

        return LocalAPIResponder.ok(response)
    }

    /// The `providers` and `post_processing_providers` rows of `GET /health`.
    ///
    /// Split out of `handle` because it names no FlyingFox type, so the test
    /// target (which links no FlyingFox) can drive the real rows.
    ///
    /// `key_present` for the HyperWhisper Cloud-routed providers (`hyperwhisper`,
    /// `microsoftAzureSpeech`, `googleSpeech`, and post-processing
    /// `hyperwhisper`) reads the SAME predicate `/transcribe` and Cloud
    /// post-processing use to refuse a job with `MISSING_API_KEY`:
    /// `LicenseManager.getTranscriptionIdentifier().isLicensed`, fed to
    /// `HyperWhisperCloudEntitlement.requireLicense` (transcription) and checked
    /// by `AIPostProcessor.performHyperWhisperCloudPostProcessing`. They used to
    /// be hard-coded `true`, so a profile with no account key read
    /// `key_present: true` while every Cloud job was refused (#650, #1492).
    /// Presence only: the identifier itself is discarded, never logged.
    @MainActor
    static func providerEntries(
        snapshot: HealthSnapshot?,
        settingsManager: SettingsManager?,
        licenseManager: LicenseManager?
    ) -> (providers: [HealthProviderStatus], postProcessing: [HealthProviderStatus]) {
        let cloudAccountKeyPresent = licenseManager?.getTranscriptionIdentifier().isLicensed ?? false

        // Cloud transcription providers
        var transcriptionEntries: [HealthProviderStatus] = []
        for provider in transcriptionProviders {
            var status = snapshot?.cloud[provider.rawValue] ?? "unknown"
            let keyPresent: Bool
            if !provider.requiresAPIKey {
                // HW-Cloud-routed providers (hyperwhisper, microsoftAzureSpeech,
                // googleSpeech) need the HyperWhisper Cloud account key.
                keyPresent = cloudAccountKeyPresent
            } else if let settingsManager {
                keyPresent = !settingsManager.apiKey(for: provider).isEmpty
            } else {
                keyPresent = false
            }
            if provider == .meta, keyPresent {
                // Meta has no documented content-free validation endpoint.
                // Presence is configuration, not proof of reachability.
                status = "configured"
            }
            transcriptionEntries.append(HealthProviderStatus(
                id: provider.rawValue,
                key_present: keyPresent,
                reachable: status == "healthy",
                status: status
            ))
        }

        // Post-processing providers
        var postEntries: [HealthProviderStatus] = []
        for provider in PostProcessingProvider.allCases {
            let status = snapshot?.postProcessing[provider.rawValue] ?? "unknown"
            let keyPresent: Bool
            switch provider {
            case .hyperwhisper:
                keyPresent = cloudAccountKeyPresent
            case .localLLM:
                // Runs on this Mac; there is no credential to hold.
                keyPresent = true
            default:
                if let settingsManager {
                    keyPresent = !settingsManager.postProcessingAPIKey(for: provider).isEmpty
                } else {
                    keyPresent = false
                }
            }
            postEntries.append(HealthProviderStatus(
                id: provider.rawValue,
                key_present: keyPresent,
                reachable: status == "healthy",
                status: status
            ))
        }

        return (transcriptionEntries, postEntries)
    }

    @MainActor
    private static func collectLocalModels(
        whisperModelManager: WhisperModelManager?,
        parakeetModelManager: ParakeetModelManager?,
        nemotronModelManager: NemotronModelManager?,
        qwen3AsrModelManager: Qwen3AsrModelManager?,
        localModelManager: LocalModelManager?
    ) -> HealthLocalModels {
        var whisper: [HealthLocalModelEntry] = []
        if let wm = whisperModelManager {
            let downloaded = Set(wm.downloadedModels.map { $0.name })
            whisper = wm.availableModels.map { item in
                HealthLocalModelEntry(
                    id: item.name,
                    displayName: item.displayName,
                    installed: downloaded.contains(item.name)
                )
            }
        }

        var parakeet: [HealthLocalModelEntry] = []
        if let pm = parakeetModelManager {
            parakeet = pm.availableModels.map { item in
                HealthLocalModelEntry(
                    id: item.id,
                    displayName: item.displayName,
                    installed: item.isDownloaded
                )
            }
        }

        var nemotron: [HealthLocalModelEntry] = []
        if let nm = nemotronModelManager {
            nemotron = nm.availableModels.map { item in
                HealthLocalModelEntry(
                    id: item.id,
                    displayName: item.displayName,
                    installed: item.isDownloaded
                )
            }
        }

        var qwen3: [HealthLocalModelEntry] = []
        if let qm = qwen3AsrModelManager, #available(macOS 15.0, *) {
            qwen3.append(HealthLocalModelEntry(
                id: Qwen3AsrModelManager.Constants.modelId,
                displayName: Qwen3AsrModelManager.Constants.displayName,
                installed: qm.isDownloaded
            ))
        }

        var apple: [HealthLocalModelEntry] = []
        #if canImport(Speech)
        if #available(macOS 26.0, *) {
            apple.append(HealthLocalModelEntry(
                id: "apple-speech-analyzer",
                displayName: "Apple Speech",
                installed: SpeechTranscriber.isAvailable
            ))
        }
        #endif

        var localLLM: [HealthLocalModelEntry] = []
        if let lm = localModelManager {
            localLLM = lm.availableModels.map { item in
                HealthLocalModelEntry(
                    id: item.id,
                    displayName: item.displayName,
                    installed: item.isDownloaded
                )
            }
        }

        return HealthLocalModels(
            whisper: whisper,
            parakeet: parakeet,
            nemotron: nemotron,
            qwen3_asr: qwen3,
            apple_speech: apple,
            local_llm: localLLM
        )
    }
}
