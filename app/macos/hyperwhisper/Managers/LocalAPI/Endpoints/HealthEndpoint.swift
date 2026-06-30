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

    @MainActor
    static func handle(
        port: UInt16,
        cloudHealth: CloudProviderHealthManager?,
        whisperModelManager: WhisperModelManager?,
        parakeetModelManager: ParakeetModelManager?,
        qwen3AsrModelManager: Qwen3AsrModelManager?,
        localModelManager: LocalModelManager?,
        settingsManager: SettingsManager?
    ) async -> HTTPResponse {
        let snapshot = cloudHealth?.healthSnapshot()

        // Cloud transcription providers
        var providerEntries: [HealthProviderStatus] = []
        for provider in CloudProvider.allCases {
            let status = snapshot?.cloud[provider.rawValue] ?? "unknown"
            let keyPresent: Bool
            if !provider.requiresAPIKey {
                // HW-Cloud-routed providers (hyperwhisper, microsoftAzureSpeech,
                // googleSpeech) authenticate via license/device — always "configured".
                keyPresent = true
            } else if let settingsManager {
                keyPresent = !settingsManager.apiKey(for: provider).isEmpty
            } else {
                keyPresent = false
            }
            providerEntries.append(HealthProviderStatus(
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
            case .hyperwhisper, .localLLM:
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

        let local = collectLocalModels(
            whisperModelManager: whisperModelManager,
            parakeetModelManager: parakeetModelManager,
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
            providers: providerEntries,
            post_processing_providers: postEntries,
            local_models: local
        )

        return LocalAPIResponder.ok(response)
    }

    @MainActor
    private static func collectLocalModels(
        whisperModelManager: WhisperModelManager?,
        parakeetModelManager: ParakeetModelManager?,
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
            qwen3_asr: qwen3,
            apple_speech: apple,
            local_llm: localLLM
        )
    }
}
