//
//  CatalogLanguageParityTests.swift
//  hyperwhisperTests
//
//  Guards the Model Library language filter's single source of truth:
//  shared-models/models-catalog.json carries per-model CLOUD language sets that
//  Windows reads directly and macOS reads for cloud rows. Those values are a
//  mirror of macOS `STTCapabilities.swift` (the authoritative per-model cloud
//  registry). This test asserts they never drift — if someone edits one without
//  the other, it fails. Local-model language sets are NOT in the catalog (the
//  catalog's local rows are wildcards), so they're out of scope here.
//

import Foundation
import Testing
@testable import HyperWhisper

struct CatalogLanguageParityTests {

    /// Catalog provider string → STTCapabilities provider id, for the cloud
    /// providers whose language sets are mirrored from STTCapabilities.
    ///
    /// Intentionally excluded (asserted only for "has some data", not parity):
    /// - gemini / microsoftAzureSpeech / googleSpeech: absent from STTCapabilities;
    ///   their catalog values come from cloud-stt-catalog.json (Azure/Google) or
    ///   are `supportsAllLanguages` (Gemini auto-detects).
    /// - grok: present in STTCapabilities but its 25 codes are a formatting-only
    ///   allowlist; the catalog intentionally marks it `supportsAllLanguages`
    ///   (xAI transcription is not language-limited). It is also Windows-only.
    private static let providerIdMap: [String: String] = [
        "openai": "openai",
        "groq": "groq",
        "deepgram": "deepgram",
        "assemblyAI": "assemblyai",
        "elevenLabs": "elevenlabs",
        "mistral": "mistral",
        "soniox": "soniox",
    ]

    /// Reduce a list of raw provider locale codes to the same base-code +
    /// "covers everything" shape the catalog stores.
    private func reduce(_ codes: [String]) -> (codes: Set<String>, all: Bool) {
        let infos = codes.map { LanguageData.LanguageInfo(code: $0, displayName: $0) }
        return LibraryLanguageFilter.reduce(infos)
    }

    @Test("Every cloud voice model has language data")
    func everyCloudVoiceHasLanguageData() throws {
        let localProviders: Set<String> = ["appleSpeech", "localWhisper", "parakeet", "qwen3ASR", "nemotron"]
        let cloudVoice = SharedModelsCatalog.allEntries().filter {
            $0.kind == "voice" && !localProviders.contains($0.provider)
        }
        #expect(cloudVoice.count >= 25, "expected the full cloud voice catalog, got \(cloudVoice.count)")
        for entry in cloudVoice {
            let support = SharedModelsCatalog.languageSupport(provider: entry.provider, kind: .voice, id: entry.id)
            #expect(
                support.supportsAll || !support.codes.isEmpty,
                "\(entry.provider)/\(entry.id) has no language data (neither supportsAllLanguages nor supportedLanguages)"
            )
        }
    }

    @Test("Catalog cloud languages match STTCapabilities")
    func catalogMatchesSTTCapabilities() throws {
        var checked = 0
        for entry in SharedModelsCatalog.allEntries() where entry.kind == "voice" {
            guard let providerId = Self.providerIdMap[entry.provider] else { continue }
            // A mapped provider can still carry ids absent from STTCapabilities —
            // e.g. openai's dated `gpt-4o-mini-transcribe-2025-12-15` alias, which
            // is intentionally `supportsAllLanguages`. Those aren't parity
            // violations (the "has data" test covers them); only assert parity for
            // ids STTCapabilities actually defines.
            guard STTCapabilities.model(providerId: providerId, modelId: entry.id) != nil else { continue }
            let expected = reduce(STTCapabilities.locales(providerId: providerId, modelId: entry.id))
            let actual = SharedModelsCatalog.languageSupport(provider: entry.provider, kind: .voice, id: entry.id)

            #expect(
                actual.supportsAll == expected.all,
                "\(entry.provider)/\(entry.id): supportsAllLanguages mismatch — catalog \(actual.supportsAll), STTCapabilities \(expected.all)"
            )
            if !expected.all {
                #expect(
                    actual.codes == expected.codes,
                    "\(entry.provider)/\(entry.id): language set mismatch.\n  only in catalog: \(actual.codes.subtracting(expected.codes).sorted())\n  only in STTCapabilities: \(expected.codes.subtracting(actual.codes).sorted())"
                )
            }
            checked += 1
        }
        #expect(checked >= 18, "expected to verify the STTCapabilities-backed cloud models, only checked \(checked)")
    }

    @Test("English-only cloud models filter to English only")
    func englishOnlyModels() throws {
        for id in ["nova-3-medical", "nova-2-medical"] {
            let support = SharedModelsCatalog.languageSupport(provider: "deepgram", kind: .voice, id: id)
            #expect(!support.supportsAll, "\(id) should not be supportsAllLanguages")
            #expect(support.supports("en"), "\(id) should support English")
            #expect(!support.supports("es"), "\(id) should be English-only (no Spanish)")
        }
    }
}
