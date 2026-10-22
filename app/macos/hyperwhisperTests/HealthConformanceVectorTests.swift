//
//  HealthConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/health-vectors.json` through THIS head's mapping of
//  the core's health verdict, `CloudProviderHealthManager.mapRustHealth`
//  (issue #779). The other heads replay the same file:
//
//    shared-core-rs/crates/hw-net/tests/health_vectors.rs
//    app/windows/HyperWhisper.SmokeTests/Program.cs
//    app/shared-dotnet/HyperWhisper.SharedCore.Tests/Program.cs
//
//  macOS and Windows each used to keep their own "which vendors answer a bad
//  key with HTTP 400" list, and they disagreed on Gemini 3.5 Transcribe: the
//  same bad key read "Key invalid" here and "Provider unreachable" on Windows.
//  The core now decides `HwProviderHealth.reason`; these rows prove this head
//  reads it unchanged. Each row is asserted against the LITERAL in the file.
//
//  Regenerate the vectors from Rust after an intended policy change:
//    cd shared-core-rs && cargo test -p hw-net --test health_vectors -- --ignored regenerate
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct HealthConformanceVectorTests {

    struct Document: Decodable {
        let cases: [HealthVector]
    }

    struct HealthVector: Decodable {
        let provider: String
        let status: UInt16
        let healthy: Bool
        let reason: String
    }

    /// The vectors are repo data shared by four stacks, not a bundled app
    /// resource, so read them from the source tree the way the other
    /// conformance tests do.
    private static func vectors() throws -> Document {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/health-vectors.json")
        return try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    /// The vector `provider` is the core's variant name. Written out by hand:
    /// the Swift binding's `HwProvider` is not `CaseIterable`.
    private static func hwProvider(named name: String) -> HwProvider? {
        switch name {
        case "HyperWhisperCloud": return .hyperWhisperCloud
        case "Openai": return .openai
        case "Groq": return .groq
        case "Elevenlabs": return .elevenlabs
        case "Mistral": return .mistral
        case "Grok": return .grok
        case "Deepgram": return .deepgram
        case "Soniox": return .soniox
        case "Assemblyai": return .assemblyai
        case "Gemini": return .gemini
        case "AzureMai": return .azureMai
        case "GoogleChirp": return .googleChirp
        case "GeminiTranscribe": return .geminiTranscribe
        case "GeminiTranscribeLive": return .geminiTranscribeLive
        case "Meta": return .meta
        default: return nil
        }
    }

    private static func expected(_ reason: String) -> ProviderHealth? {
        switch reason {
        case "healthy": return .healthy
        case "unauthorized": return .unauthorized
        case "unreachable": return .unreachable
        default: return nil
        }
    }

    private static func verdict(_ provider: HwProvider, status: UInt16) -> HwProviderHealth {
        parseHealthResponse(provider: provider, resp: HttpResponse(status: status, headers: [], body: Data()))
    }

    @Test("every health vector maps to the same state on this head")
    func healthVerdictsMatchTheVectors() throws {
        let document = try Self.vectors()
        #expect(document.cases.count >= 15 * 6, "health-vectors.json looks truncated")

        for row in document.cases {
            let label = "\(row.provider) \(row.status)"
            guard let provider = Self.hwProvider(named: row.provider) else {
                Issue.record("\(label): no HwProvider named '\(row.provider)'")
                continue
            }
            guard let want = Self.expected(row.reason) else {
                Issue.record("\(label): unknown reason '\(row.reason)'")
                continue
            }
            let verdict = Self.verdict(provider, status: row.status)
            #expect(verdict.healthy == row.healthy, "\(label): core healthy flag")
            #expect(CloudProviderHealthManager.mapRustHealth(verdict) == want, "\(label): mapped state")
        }
    }

    /// The row issue #779 was filed for, through the provider mapping the
    /// shipping probe uses rather than through the vector's own name.
    @Test("a Gemini 3.5 Transcribe key rejected with HTTP 400 reads as unauthorized")
    func badGeminiTranscribeKeyIsUnauthorized() {
        let provider = RustCoreMapping.hwProvider(for: .geminiTranscribe)
        let mapped = CloudProviderHealthManager.mapRustHealth(Self.verdict(provider, status: 400))
        #expect(mapped == .unauthorized)
    }
}
