//
//  PostProcessEndpointLabelTests.swift
//  hyperwhisperTests
//
//  Pins the `/post-process` rule from issue #314: the response names the
//  provider and model that ACTUALLY RAN, not the ones stored on the Mode.
//  `AIPostProcessor` resolves both after the Mode is read (deprecated-model
//  remap, provider-default substitution, installed-local-model substitution,
//  the GGUF llama-server really loaded), and hands them back on the
//  request-scoped `MutationSignal`.
//

import Foundation
import Testing
@testable import HyperWhisper

struct PostProcessEndpointLabelTests {

    // MARK: - The bug

    @Test func resolvedModelWinsOverTheStoredModel() {
        // The reported case: the Mode stores a model that belongs to another
        // provider, AIPostProcessor falls back to the provider default, and the
        // response used to name the dead id.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "anthropic",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: "anthropic",
            resolvedModel: "claude-haiku-4-5"
        )
        #expect(labels.model == "claude-haiku-4-5")
        #expect(labels.provider == "anthropic")
    }

    @Test func resolvedProviderWinsWhenTheProviderItselfChanged() {
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "openai",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: PostProcessingProvider.localLLM.rawValue,
            resolvedModel: "gemma-3-12b"
        )
        #expect(labels.provider == "local_llm")
        #expect(labels.model == "gemma-3-12b")
    }

    @Test func cloudRunReportsTheCloudModelNotTheModesLanguageModel() {
        // The default provider never reads `mode.languageModel` at all — it
        // routes on `mode.cloudPostProcessingModel` — so the old response
        // reported an unrelated field, not merely a stale one.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "hyperwhisper",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: PostProcessingProvider.hyperwhisper.rawValue,
            resolvedModel: "claude-haiku-4-5"
        )
        #expect(labels.provider == "hyperwhisper")
        #expect(labels.model == "claude-haiku-4-5")
    }

    @Test func customEndpointReportsItsProviderStringAndRepairedModel() {
        let providerString = "custom:0F5F6B1E-4B4A-4E0B-9C2E-6F8C3F2A1D77"
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: providerString,
            storedModel: "ignored-by-custom-endpoints",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: providerString,
            resolvedModel: "llama3.1:8b"
        )
        #expect(labels.provider == providerString)
        #expect(labels.model == "llama3.1:8b")
    }

    // MARK: - Nothing ran

    @Test func nothingRanFallsBackToTheStoredLabels() {
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "openai",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: nil,
            resolvedModel: nil
        )
        #expect(labels.provider == "openai")
        #expect(labels.model == "gpt-4.1-nano")
    }

    @Test func nothingRanAndNothingStoredKeepsTheHistoricalDefaults() {
        // No `mode_id` and no run: `model` stays `""` rather than inventing a
        // value. `post_processed: false` is what tells the caller.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: nil,
            storedModel: nil,
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: nil,
            resolvedModel: nil
        )
        #expect(labels.provider == PostProcessingProvider.hyperwhisper.rawValue)
        #expect(labels.model == "")
    }

    @Test func emptyResolvedValuesAreTreatedAsAbsent() {
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "openai",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: "   ",
            resolvedModel: ""
        )
        #expect(labels.provider == "openai")
        #expect(labels.model == "gpt-4.1-nano")
    }

    // MARK: - Nothing ran: the fallback follows the router, not just the string

    @Test func nothingRanOnALocalModeReportsLocalLlmWithNoStoredProvider() {
        // `postProcessingMode: 2` with an explicit JSON null `postProcessingProvider`
        // — a local run that returned raw text without throwing. Reading the
        // stored string alone answered `hyperwhisper`, a provider this mode would
        // never route to; `TranscriptionProviderRouter` labels it `local_llm`.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: nil,
            storedModel: nil,
            storedProcessingMode: PostProcessingMode.local.rawValue,
            resolvedProvider: nil,
            resolvedModel: nil
        )
        #expect(labels.provider == PostProcessingProvider.localLLM.rawValue)
    }

    @Test func nothingRanOnALocalModeIgnoresAStaleCloudProviderString() {
        // A mode switched to local keeps whatever `postProcessingProvider` it had.
        // The router ignores that string for a `.local` mode, so this must too.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "openai",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.local.rawValue,
            resolvedProvider: nil,
            resolvedModel: nil
        )
        #expect(labels.provider == PostProcessingProvider.localLLM.rawValue)
    }

    @Test func nothingRanUsesTheProcessingModesOwnDefaultProvider() {
        // `.cloud` defaults to `hyperwhisper` and `.off` has no default at all,
        // which is the only case that falls through to the historical constant.
        #expect(PostProcessEndpoint.responseLabels(
            storedProvider: nil, storedModel: nil,
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: nil, resolvedModel: nil
        ).provider == PostProcessingMode.cloud.defaultProvider?.rawValue)
        #expect(PostProcessEndpoint.responseLabels(
            storedProvider: nil, storedModel: nil,
            storedProcessingMode: PostProcessingMode.off.rawValue,
            resolvedProvider: nil, resolvedModel: nil
        ).provider == PostProcessingProvider.hyperwhisper.rawValue)
    }

    @Test func aRunThatHappenedStillWinsOverTheProcessingModeFallback() {
        // The `.local` rule is a NOTHING-RAN rule only. A mode marked local whose
        // request actually routed elsewhere reports what ran.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: nil,
            storedModel: nil,
            storedProcessingMode: PostProcessingMode.local.rawValue,
            resolvedProvider: "anthropic",
            resolvedModel: "claude-haiku-4-5"
        )
        #expect(labels.provider == "anthropic")
        #expect(labels.model == "claude-haiku-4-5")
    }

    // MARK: - Provider-spelling preservation

    @Test func callersProviderSpellingIsPreservedWhenTheProviderIsUnchanged() {
        // Same provider, different spelling → echo the caller's, so the field
        // changes ONLY when a real substitution happened.
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "OpenAI",
            storedModel: "gpt-4.1-nano",
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: "openai",
            resolvedModel: "gpt-4.1-nano"
        )
        #expect(labels.provider == "OpenAI")
    }

    @Test func resolvedProviderIsUsedWhenTheStoredOneIsBlank() {
        let labels = PostProcessEndpoint.responseLabels(
            storedProvider: "  ",
            storedModel: nil,
            storedProcessingMode: PostProcessingMode.cloud.rawValue,
            resolvedProvider: "anthropic",
            resolvedModel: "claude-haiku-4-5"
        )
        #expect(labels.provider == "anthropic")
        #expect(labels.model == "claude-haiku-4-5")
    }

    // MARK: - The signal contract the endpoint depends on

    @Test func mutationSignalStartsWithNoResolvedLabels() {
        // `resolvedModel != nil` must mean "an LLM ran" — a fresh signal, and a
        // run that failed before any success site, must carry nothing.
        let signal = MutationSignal()
        #expect(signal.didMutate == false)
        #expect(signal.resolvedProvider == nil)
        #expect(signal.resolvedModel == nil)
    }
}
