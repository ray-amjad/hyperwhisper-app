//
//  SharedLlmRequestBuilderTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Covers the macOS side of issue #282: the post-processing endpoint table, the
/// auth headers, the request bodies, the custom-endpoint rule and the duplicate
/// copy-name rule moved into the shared Rust core.
///
/// These assertions pin the values the app sent BEFORE the move, so a drift in
/// the core is a red test here rather than a broken provider in the field.
struct SharedLlmRequestBuilderTests {

    private func request(
        _ provider: PostProcessingProvider,
        model: String = "model",
        apiKey: String = "test-key"
    ) throws -> HttpRequest {
        try llmBuildRequest(params: HwLlmParams(
            provider: provider.coreProvider,
            model: model,
            apiKey: apiKey,
            systemPrompt: "system",
            systemInfo: "info",
            transcript: "raw words"
        ))
    }

    private func body(_ request: HttpRequest) throws -> [String: Any] {
        guard case let .bytes(_, payload) = request.body,
              let json = try JSONSerialization.jsonObject(with: payload) as? [String: Any] else {
            Issue.record("request body was not inline JSON")
            return [:]
        }
        return json
    }

    private func header(_ request: HttpRequest, _ name: String) -> String? {
        request.headers.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    @Test func everyProviderKeepsItsEndpoint() throws {
        let expected: [(PostProcessingProvider, String)] = [
            (.openai, "https://api.openai.com/v1/chat/completions"),
            (.anthropic, "https://api.anthropic.com/v1/messages"),
            (.gemini, "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"),
            (.groq, "https://api.groq.com/openai/v1/chat/completions"),
            (.grok, "https://api.x.ai/v1/chat/completions"),
            (.cerebras, "https://api.cerebras.ai/v1/chat/completions"),
            (.mistral, "https://api.mistral.ai/v1/chat/completions"),
        ]
        for (provider, url) in expected {
            let built = try request(provider)
            #expect(built.url == url, "endpoint for \(provider.displayName)")
        }
    }

    @Test func localLlamaUsesTheConfiguredPort() throws {
        let built = try llmBuildRequest(params: HwLlmParams(
            provider: PostProcessingProvider.localLLM.coreProvider,
            model: "gemma.gguf",
            apiKey: "",
            systemPrompt: "system",
            systemInfo: "info",
            transcript: "raw words",
            localLlamaPort: UInt16(LlamaServerController.Configuration.default.port)
        ))
        #expect(built.url == "http://127.0.0.1:\(LlamaServerController.Configuration.default.port)/v1/chat/completions")
        // The Gemma 4 sampling parameters travel with the request.
        let json = try body(built)
        #expect(json["top_p"] as? Double == 0.95)
        #expect(json["max_tokens"] as? Int == 8192)
    }

    @Test func authHeadersMatchEachWireProtocol() throws {
        let openai = try request(.openai)
        let anthropic = try request(.anthropic)
        #expect(header(openai, "Authorization") == "Bearer test-key")
        #expect(header(anthropic, "x-api-key") == "test-key")
        #expect(header(anthropic, "anthropic-version") == "2023-06-01")
        // Anthropic must NOT get a Bearer header — it 401s on one.
        #expect(header(anthropic, "Authorization") == nil)
    }

    @Test func onlyGroqCarriesACompletionCap() throws {
        let groq = try body(request(.groq))
        #expect(groq["max_completion_tokens"] as? Int == Int(llmGroqMaxCompletionTokens()))
        #expect(groq["max_tokens"] == nil)

        let openai = try body(request(.openai))
        #expect(openai["max_completion_tokens"] == nil)
        #expect(openai["max_tokens"] == nil)
    }

    @Test func anthropicKeepsItsRequiredOutputLimit() throws {
        let json = try body(request(.anthropic))
        #expect(json["max_tokens"] as? Int == 8192)
        #expect(json["max_tokens"] as? Int == Int(llmMaxOutputTokens()))
    }

    @Test func transcriptIsWrappedForTheModel() throws {
        let wrapped = llmWrapTranscript(systemInfo: "info", transcript: "raw words")
        #expect(wrapped.contains("--TRANSCRIPT--"))
        #expect(wrapped.contains("--ENDTRANSCRIPT--"))
        #expect(wrapped.hasPrefix("info"))
    }

    @Test func hyperWhisperCloudSendsTheTranscriptOnce() throws {
        let built = try llmBuildRequest(params: HwLlmParams(
            provider: .hyperWhisperCloud,
            model: "",
            apiKey: "",
            systemPrompt: "system",
            systemInfo: "info",
            transcript: "raw words",
            baseUrl: NetworkConfig.hyperwhisperCloudURL,
            deviceId: "device-1"
        ))
        #expect(built.url == NetworkConfig.hyperwhisperCloudURL + "/post-process")

        let json = try body(built)
        #expect(json["text"] as? String == "raw words")
        #expect(json["device_id"] as? String == "device-1")
        // The prompt carries the context only. Windows used to send the wrapped
        // transcript here AND in `text`, so the model saw it twice.
        let prompt = json["prompt"] as? String ?? ""
        #expect(!prompt.contains("--TRANSCRIPT--"))
        #expect(!prompt.contains("raw words"))
    }

    @Test func aCustomEndpointAimedAtGroqGetsGroqsCap() throws {
        let groqRequest = try llmBuildRequest(params: HwLlmParams(
            provider: .custom, model: "openai/gpt-oss-20b", apiKey: "",
            systemPrompt: "system", systemInfo: "info", transcript: "raw words",
            customEndpoint: "https://API.GROQ.COM/openai/v1/chat/completions"))
        let groq = try body(groqRequest)
        #expect(groq["max_completion_tokens"] as? Int == Int(llmGroqMaxCompletionTokens()))

        let localRequest = try llmBuildRequest(params: HwLlmParams(
            provider: .custom, model: "llama3.2", apiKey: "",
            systemPrompt: "system", systemInfo: "info", transcript: "raw words",
            customEndpoint: "http://localhost:1234/v1/chat/completions"))
        let local = try body(localRequest)
        #expect(local["max_completion_tokens"] == nil)
    }

    @Test func savingAnEndpointIsStrictButLoadingOneIsLenient() {
        // macOS used to accept this: `URL(string:)` parses a schemeless string.
        let strict = llmNormalizeCustomEndpoint(
            raw: "localhost:11434/v1/chat/completions", model: "llama3.2", mode: .strict)
        #expect(strict.status == .invalid)
        #expect(strict.url.isEmpty)
        #expect(strict.suggestion != nil)

        // The same endpoint already on disk is repaired, not dropped.
        let lenient = llmValidateExistingCustomEndpoint(
            raw: "localhost:11434/v1/chat/completions", model: "llama3.2")
        #expect(lenient.status == .needsRepair)
        #expect(!lenient.url.isEmpty)
    }

    @Test func endpointCopyNamesIncrement() {
        #expect(llmNextCopyName(originalName: "Name") == "Name (copy)")
        #expect(llmNextCopyName(originalName: "Name (copy)") == "Name (copy 2)")
        #expect(llmNextCopyName(originalName: "Name (copy 2)") == "Name (copy 3)")
        #expect(llmNextCopyName(originalName: "Name (copy 99)") == "Name (copy 100)")
    }

    @Test func customProviderStringsRoundTrip() {
        let id = UUID()
        let providerString = "custom:\(id.uuidString)"
        #expect(CustomPostProcessingEndpoint.isCustomProviderString(providerString))
        #expect(CustomPostProcessingEndpoint.parseCustomProviderString(providerString) == id)
        #expect(!CustomPostProcessingEndpoint.isCustomProviderString("openai"))
        #expect(CustomPostProcessingEndpoint.parseCustomProviderString("custom:not-a-uuid") == nil)
    }
}
