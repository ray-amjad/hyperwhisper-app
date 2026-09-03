//
//  PostProcessingProviderAliasTests.swift
//  hyperwhisperTests
//
//  The three heads spell HyperWhisper Cloud's post-processing provider three
//  different ways in storage: macOS wrote "hyperwhisper", Windows and the
//  Linux/portable head write "hyperwhispercloud", and older Windows builds wrote
//  "hyperwhisper_cloud". The universal backup carries whichever one it was given
//  — `ApplicationBackupExport.cs` does not fold it — so a restore lands a
//  foreign spelling on every platform.
//
//  macOS used to reject two of the three, and rejected them SILENTLY: the
//  parse failure makes `AIPostProcessor` return the raw transcript while
//  `ModeCard` and `ModePostProcessingSettings` still render "HyperWhisper
//  Cloud", because their `?? .hyperwhisper` fallbacks fire on nil and hide the
//  mismatch. These tests are the macOS half of the alias pin that
//  `HyperWhisper.Linux.Composition.Tests` (`CloudProviderStorageRoutes`) already
//  holds for Linux, so the two sides cannot drift apart.
//

import Foundation
import Testing

@testable import HyperWhisper

struct PostProcessingProviderAliasTests {

    /// The exact alias set `HyperWhisper.Linux.Composition.Tests` pins, so the
    /// two suites fail together if either side adds or drops a spelling.
    private static let hyperwhisperCloudSpellings = [
        "hyperwhispercloud",
        "hyperwhisper",
        "hyperwhisper_cloud",
    ]

    // MARK: - The alias set

    @Test func everyHyperWhisperCloudSpellingResolvesToTheSameCase() {
        for spelling in Self.hyperwhisperCloudSpellings {
            #expect(PostProcessingProvider(rawValue: spelling) == .hyperwhisper)
        }
    }

    @Test func theOtherProvidersInTheLinuxPinStillResolve() {
        // The Linux suite asserts these route alongside the three aliases; a
        // widened initialiser that broke one of them would pass the test above.
        let expected: [(String, PostProcessingProvider)] = [
            ("openai", .openai),
            ("anthropic", .anthropic),
            ("groq", .groq),
            ("grok", .grok),
            ("gemini", .gemini),
            ("cerebras", .cerebras),
            ("mistral", .mistral),
            ("local_llm", .localLLM),
        ]
        for (stored, provider) in expected {
            #expect(PostProcessingProvider(rawValue: stored) == provider)
        }
    }

    @Test func everyCaseStillParsesFromItsOwnRawValue() {
        // The custom initialiser replaces the synthesised one, so this is the
        // guard that it did not lose a case on the way.
        for provider in PostProcessingProvider.allCases {
            #expect(PostProcessingProvider(rawValue: provider.rawValue) == provider)
        }
    }

    @Test func caseAndSurroundingWhitespaceDoNotChangeTheResult() {
        // Windows folds with `ToLowerInvariant()` before matching; match it, so
        // a hand-edited backup or a Local API caller's "OpenAI" lands the same
        // way on both heads.
        #expect(PostProcessingProvider(rawValue: "HyperWhisperCloud") == .hyperwhisper)
        #expect(PostProcessingProvider(rawValue: " hyperwhisper ") == .hyperwhisper)
        #expect(PostProcessingProvider(rawValue: "\nHYPERWHISPER_CLOUD\n") == .hyperwhisper)
        #expect(PostProcessingProvider(rawValue: "OpenAI") == .openai)
        #expect(PostProcessingProvider(rawValue: "LOCAL_LLM") == .localLLM)
    }

    // MARK: - What must still be rejected

    @Test func unknownAndEmptyValuesStillReturnNil() {
        // Read sites lean on nil: `ModeData(from:)` passes `?? ""`, and
        // `AIPostProcessor` treats nil as "leave the transcript alone".
        #expect(PostProcessingProvider(rawValue: "") == nil)
        #expect(PostProcessingProvider(rawValue: "   ") == nil)
        #expect(PostProcessingProvider(rawValue: "unknown") == nil)
        #expect(PostProcessingProvider(rawValue: "none") == nil)
        // Not an alias on this head: Windows maps "local" to LocalLlm, macOS
        // never wrote it. Widening to it would be a separate decision.
        #expect(PostProcessingProvider(rawValue: "local") == nil)
    }

    @Test func customEndpointStringsAreNotProviders() {
        // `AIPostProcessor` checks `isCustomProviderString` BEFORE the enum. If
        // a widened parser ever swallowed "custom:<uuid>", every custom
        // OpenAI-compatible endpoint would silently route to a built-in
        // provider instead.
        let endpointString = "custom:\(UUID().uuidString)"
        #expect(CustomPostProcessingEndpoint.isCustomProviderString(endpointString))
        #expect(PostProcessingProvider(rawValue: endpointString) == nil)
        #expect(PostProcessingProvider(rawValue: endpointString.lowercased()) == nil)
    }

    // MARK: - The storage value

    @Test func hyperWhisperCloudStoresTheCanonicalCrossPlatformToken() {
        #expect(PostProcessingProvider.hyperwhisper.storageValue == "hyperwhispercloud")
    }

    @Test func theRawValueIsDeliberatelyNotRecanonicalised() {
        // `rawValue` is wired into API-key setting names, ProviderHealth keys
        // and SwiftUI picker tags. Only `storageValue` moved; changing this
        // line would change all of those at once.
        #expect(PostProcessingProvider.hyperwhisper.rawValue == "hyperwhisper")
    }

    @Test func everyOtherProviderStoresItsRawValueUnchanged() {
        for provider in PostProcessingProvider.allCases where provider != .hyperwhisper {
            #expect(provider.storageValue == provider.rawValue)
        }
    }

    @Test func storageValuesRoundTripAndAreIdempotent() {
        for provider in PostProcessingProvider.allCases {
            #expect(PostProcessingProvider(rawValue: provider.storageValue) == provider)
            let once = provider.storageValue
            #expect(PostProcessingProvider(rawValue: once)?.storageValue == once)
        }
    }

    // MARK: - The bug this phase fixes

    @Test func aLinuxBackupsProviderTokenSurvivesARestoreOnMacOS() {
        // `PortableModeDefaults` seeds "hyperwhispercloud"; the exporter writes
        // it verbatim into the `.hwbackup`; `BackupManager` hands it to
        // `createOrUpdateMode`, which stores caller-supplied strings verbatim.
        // Before this change the value reached every reader as nil and
        // post-processing quietly stopped running.
        let restored = "hyperwhispercloud"
        let provider = PostProcessingProvider(rawValue: restored)
        #expect(provider == .hyperwhisper)
        #expect(provider?.displayName == "HyperWhisper Cloud")
        #expect(provider?.coreProvider == .hyperWhisperCloud)
        #expect(provider?.requiresAPIKey == false)
        #expect(provider?.isLocal == false)
    }
}
