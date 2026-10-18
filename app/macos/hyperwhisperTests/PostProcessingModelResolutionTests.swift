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
            ("claude-sonnet-4-0", .anthropic, "claude-sonnet-5-5"),
            ("meta-llama/llama-4-maverick-17b-128e-instruct", .groq, "openai/gpt-oss-120b"),
            ("zai-glm-4.7", .cerebras, "gpt-oss-120b"),
            ("gpt-4.1-nano", .openai, "gpt-5.6-luna"),
            // OpenAI removes the gpt-5 / -mini / -nano snapshots 2026-12-11 (#1018).
            ("gpt-5-nano", .openai, "gpt-5.6-luna"),
            ("gpt-5-mini", .openai, "gpt-5.6-luna"),
            ("gpt-5", .openai, "gpt-5.6-luna"),
            ("gemini-3-pro-preview", .gemini, "gemini-3.1-pro-preview"),
            ("gemini-3.1-flash-lite-preview", .gemini, "gemini-3.5-flash-lite"),
            ("gemini-2.0-flash", .gemini, "gemini-3.6-flash"),
            ("gemini-2.0-flash-lite", .gemini, "gemini-3.5-flash-lite"),
            // #798: Google shuts gemini-3.1-flash-lite down 2027-05-07.
            ("gemini-3.1-flash-lite", .gemini, "gemini-3.5-flash-lite"),
            // #1209: OpenAI removes gpt-5.1 and gpt-5.4-nano 2027-04-01.
            ("gpt-5.1", .openai, "gpt-5.6-luna"),
            ("gpt-5.4-nano", .openai, "gpt-6-luna"),
            // OpenAI removes gpt-4.1 2027-01-14; Google deprecated 3.5 / 3.7 Flash 2026-10-08.
            ("gpt-4.1", .openai, "gpt-6-luna"),
            ("gemini-3.5-flash", .gemini, "gemini-3.6-flash"),
            ("gemini-3.7-flash", .gemini, "gemini-3.8-flash"),
            // #1907: OpenAI removes gpt-4.1-mini 2027-01-14 too, and names gpt-6-luna.
            ("gpt-4.1-mini", .openai, "gpt-6-luna"),
            // Gemma hosted models left the Gemini API 2026-03-08 (#1019, matches Windows).
            ("gemma-3-12b-it", .gemini, "gemini-3.8-flash"),
            ("gemma-3-27b-it", .gemini, "gemini-3.8-flash"),
            ("llama3.1-8b", .cerebras, "qwen-3.8-27b"),
            ("llama-3.1-8b", .cerebras, "qwen-3.8-27b"),
            // Cerebras removed gemma-4-31b from the public endpoints 2026-09-03.
            ("gemma-4-31b", .cerebras, "qwen-3.8-27b"),
            ("qwen-3-235b-a22b-instruct-2507", .cerebras, "gpt-oss-120b"),
            // 2026-10 catalog refresh: each old id goes straight to its final row.
            ("claude-haiku-4-5", .anthropic, "claude-haiku-5-5"),
            ("claude-haiku-4.5", .anthropic, "claude-haiku-5-5"),
            ("claude-3-5-haiku-latest", .anthropic, "claude-haiku-5-5"),
            ("claude-haiku-4-5-20251001", .anthropic, "claude-haiku-5-5"),
            ("claude-sonnet-4-5", .anthropic, "claude-sonnet-5-5"),
            ("claude-sonnet-4-5-20250929", .anthropic, "claude-sonnet-5-5"),
            ("gemini-3-flash-preview", .gemini, "gemini-3.8-flash"),
            ("qwen/qwen3.6-27b", .groq, "qwen/qwen3.8-27b"),
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

    @Test func currentCatalogOmitsRetiredIdsAndUsesLunaAsOpenAIDefault() {
        let retired = Set([
            "gpt-4.1-nano", "gemini-3-pro-preview",
            "gemini-3.1-flash-lite-preview", "llama3.1-8b",
            "qwen-3-235b-a22b-instruct-2507",
            "gemma-4-31b",
            "claude-haiku-4-5", "claude-sonnet-4-5",
            "gemini-3-flash-preview", "qwen/qwen3.6-27b",
            "gemini-3.1-flash-lite", "gpt-5.1", "gpt-5.4-nano",
            "gpt-4.1", "gemini-3.5-flash", "gemini-3.7-flash",
            "gpt-4.1-mini",
        ])
        #expect(PostProcessingModels.availableModels.allSatisfy { !retired.contains($0.id) })
        #expect(PostProcessingModels.defaultModel(for: .openai)?.id == "gpt-5.6-luna")
        #expect(PostProcessingModels.model(withId: "gpt-6-luna", provider: .openai)?.displayName == "GPT-6 Luna")
        #expect(PostProcessingModels.defaultModel(for: .anthropic)?.id == "claude-haiku-5-5")
        #expect(PostProcessingModels.displayName(for: "claude-haiku-4-5", provider: .anthropic) == "Claude Haiku 5.5")
        // #1018: the gpt-5 family rows are deleted, not hidden. A stored id still
        // shows the name of the model that runs, through the redirect to luna.
        for retiredId in ["gpt-5-nano", "gpt-5-mini", "gpt-5"] {
            #expect(PostProcessingModels.model(withId: retiredId, provider: .openai) == nil)
            #expect(PostProcessingModels.displayName(for: retiredId, provider: .openai) == "GPT-5.6 Luna")
        }
        #expect(PostProcessingModels.model(withId: "qwen-3.8-27b", provider: .cerebras) != nil)
        #expect(PostProcessingModels.model(withId: "qwen/qwen3.8-27b", provider: .groq) != nil)
        // Removing the Cerebras row must not move the provider default, which
        // pickers resolve as `models(for:).first`.
        #expect(PostProcessingModels.defaultModel(for: .cerebras)?.id == "gpt-oss-120b")
        // #1019: Google gates the 2.5 models to past users, so a new BYOK Gemini key
        // defaults to 3.8 Flash. The 2.5 rows stay selectable for existing users.
        #expect(PostProcessingProvider.gemini.defaultModel == "gemini-3.8-flash")
        #expect(PostProcessingModels.defaultModel(for: .gemini)?.id == "gemini-3.8-flash")
        #expect(PostProcessingModels.models(for: .gemini).contains(where: { $0.id == "gemini-2.5-flash" }))
    }

    struct RedirectDocument: Decodable {
        let vectors: [RedirectVector]
    }

    struct RedirectVector: Decodable {
        let provider: String
        let from: String
        let to: String
        let platforms: [String]?
    }

    /// Replays `shared-conformance/pp-model-redirect-vectors.json` through THIS
    /// head's `deprecatedModelMappings` (#1083). Windows (`HyperWhisper.SmokeTests`)
    /// and shared-dotnet (`HyperWhisper.CloudPostProcessing.Tests`) replay the same
    /// file through their own tables; before it, the 3 copies had drifted and one
    /// saved Mode ran a different model per platform. The file is repo data, not a
    /// bundled resource, so it is read from the source tree the way
    /// `DefaultModelConformanceVectorTests` reads its own.
    @Test func retiredIdsMatchTheSharedRedirectVectors() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/pp-model-redirect-vectors.json")
        let document = try JSONDecoder().decode(RedirectDocument.self, from: Data(contentsOf: url))
        #expect(document.vectors.count >= 40, "pp-model-redirect-vectors.json looks truncated")

        var checkedRows = 0
        for vector in document.vectors {
            if let platforms = vector.platforms, !platforms.contains("macos") { continue }
            guard let provider = PostProcessingProvider(rawValue: vector.provider) else {
                Issue.record("\(vector.from): macOS has no provider '\(vector.provider)'")
                continue
            }
            // resolvedModelId drops a mapping whose target is not a live row, so this
            // also proves `to` is a live picker row of that provider.
            #expect(
                PostProcessingModels.resolvedModelId(vector.from, provider: provider) == vector.to,
                "\(vector.from) should resolve to \(vector.to) for \(vector.provider)"
            )
            #expect(
                PostProcessingModels.model(withId: vector.from, provider: provider) == nil,
                "retired \(vector.from) is still a \(vector.provider) picker row"
            )
            checkedRows += 1
        }
        #expect(checkedRows >= 40, "only \(checkedRows) macOS redirect rows were checked")
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
