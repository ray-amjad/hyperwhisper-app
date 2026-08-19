//
//  PostProcessingModelResolutionTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Covers the data the mode editor's `ensureValidLanguageModelSelection()` relies on
/// (`ModePostProcessingSettings.swift`) when a stored `languageModel` id is one of the
/// 5 ids retired by #217 (commit 367443a). The editor now resolves a stored id through
/// `PostProcessingModels.resolvedModelId()` before falling back to `options.first`, so
/// this asserts each retired id resolves to its documented replacement, and that the
/// replacement is actually present in that provider's picker options — the same guard
/// `ensureValidLanguageModelSelection()` checks before accepting the resolved id.
struct PostProcessingModelResolutionTests {
    @Test func retiredIdsResolveToDocumentedReplacementPresentInPickerOptions() {
        let retiredIds: [(id: String, provider: PostProcessingProvider, replacement: String)] = [
            ("moonshotai/kimi-k2-instruct", .groq, "openai/gpt-oss-120b"),
            ("open-mistral-nemo", .mistral, "mistral-small-latest"),
            ("claude-sonnet-4-0", .anthropic, "claude-sonnet-4-5"),
            ("meta-llama/llama-4-maverick-17b-128e-instruct", .groq, "openai/gpt-oss-120b"),
            ("zai-glm-4.7", .cerebras, "gpt-oss-120b"),
        ]

        for entry in retiredIds {
            let resolved = PostProcessingModels.resolvedModelId(entry.id, provider: entry.provider)
            #expect(resolved == entry.replacement, "\(entry.id) should resolve to \(entry.replacement)")

            let options = PostProcessingModels.models(for: entry.provider)
            #expect(
                options.contains(where: { $0.id == resolved }),
                "resolved replacement \(resolved) must be a selectable option for \(entry.provider), otherwise the editor falls back to options.first instead of the intended model"
            )
        }
    }

    @Test func unknownIdIsLeftUnresolvedSoCallersFallBackToOptionsFirst() {
        // A truly unmapped id must come back unchanged — this is what tells
        // `ensureValidLanguageModelSelection()` to fall through to its
        // `options.first` last resort instead of accepting a bogus "replacement".
        let unknownId = "totally-unknown-model-id-\(UUID().uuidString)"
        let resolved = PostProcessingModels.resolvedModelId(unknownId, provider: .groq)
        #expect(resolved == unknownId)
    }
}
