//
//  DefaultModelConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/default-model-vectors.json` against THIS head's own
//  default-model resolver (issue #580). The other three heads replay the same
//  file through theirs:
//
//    shared-core-rs/crates/hw-net/tests/default_model_vectors.rs
//    app/shared-dotnet/HyperWhisper.TranscriptionRouting.Tests/Program.cs
//    app/windows/HyperWhisper.SmokeTests/Program.cs
//
//  Before this file, three of the four heads carried a hand-written table and
//  nothing compared them: OpenAI resolved to `gpt-4o-transcribe` on the portable
//  head and `whisper-1` here, on Windows and in `hw-net`, so byte-identical
//  audio and a byte-identical request body transcribed on a different model
//  depending on which head served the request.
//
//  The row is asserted against the LITERAL in the vector file, never against a
//  second call to `CloudSTTCatalog`. Reading the catalog on both sides restores
//  exactly the tautology issue #580 calls out in #566's test.
//
//  Regenerate the vectors from Rust after an intended catalog change:
//    cd shared-core-rs && cargo test -p hw-net --test default_model_vectors -- --ignored regenerate
//

import Foundation
import Testing
@testable import HyperWhisper

struct DefaultModelConformanceVectorTests {

    struct Document: Decodable {
        let providers: [ProviderVector]
    }

    struct ProviderVector: Decodable {
        let catalogEntryId: String
        let providerIdentifier: String
        let defaultModelId: String
        let creditsPerMinute: Double
    }

    /// The vectors are repo data shared by four stacks, not a bundled app
    /// resource, so read them from the source tree the way
    /// `CatalogConformanceVectorTests` reads its own.
    private static func vectors() throws -> Document {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/default-model-vectors.json")
        return try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    @Test func defaultModelMatchesTheVectors() throws {
        let document = try Self.vectors()
        #expect(document.providers.count >= 12, "default-model vectors look truncated")

        for want in document.providers {
            guard let provider = CloudProvider.parse(want.providerIdentifier) else {
                Issue.record("\(want.providerIdentifier): macOS does not map this provider identifier")
                continue
            }
            #expect(
                CloudTranscriptionModels.defaultModel(for: provider) == want.defaultModelId,
                "\(want.catalogEntryId): macOS' default model is not '\(want.defaultModelId)'"
            )
            #expect(
                CloudTranscriptionModels.catalogEntryId(for: provider) == want.catalogEntryId,
                "\(want.providerIdentifier) maps to the wrong catalog entry"
            )
        }
    }

    /// The default has to be a model the Mode editor can actually show, or the
    /// picker renders blank for the model that is about to run.
    @Test func everyDefaultIsASelectableModel() throws {
        for want in try Self.vectors().providers {
            guard let provider = CloudProvider.parse(want.providerIdentifier) else { continue }
            #expect(
                CloudTranscriptionModels.model(withId: want.defaultModelId, provider: provider) != nil,
                "\(want.catalogEntryId): the default model '\(want.defaultModelId)' is not in the macOS picker"
            )
        }
    }

    /// A default that moves to a differently-priced model is a billing change,
    /// not only a capability one, so the credits row travels with it.
    @Test func creditsPerMinuteMatchesTheVectors() throws {
        for want in try Self.vectors().providers {
            #expect(
                CloudSTTCatalog.shared.model(
                    forEntryId: want.catalogEntryId,
                    modelId: want.defaultModelId
                )?.creditsPerMinute == want.creditsPerMinute,
                "\(want.catalogEntryId): credits/min drifted from the vectors"
            )
        }
    }
}
