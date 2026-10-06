//
//  CloudWhisperPerVendorRoutingTests.swift
//  hyperwhisperTests
//
//  Issue #1338: OpenAI and Groq shared ONE `CloudWhisperProvider`. The router
//  wrote the vendor and key onto that instance, awaited the health check, and
//  only then did `transcribe` read them back. A second request that resolved
//  in that window re-pointed the first one: a Groq request went to OpenAI, on
//  the OpenAI key.
//
//  These tests drive the real `TranscriptionProviderRouter` through the Local
//  API entry point (`resolveProvider(engine:model:language:)`), with a real
//  `CloudProviderHealthManager`, and record each request at the transport. They
//  reproduce the race window by ordering the calls: request A resolves, request
//  B resolves (and transcribes), then A transcribes. That is the interleaving
//  the `ensureHealthy` await allows on the main actor. The health probe itself
//  cannot be held open here: its URLSession is private to the health manager,
//  so both vendors start with a cached healthy verdict instead.
//

import Foundation
import Testing
import os
@testable import HyperWhisper

// MARK: - Test doubles

/// Fixed, obviously fake keys. Two distinct values are the whole point: each
/// request must carry its OWN vendor's key.
private enum FakeKeys {
    static let openAI = "openai-fixture-not-a-secret"
    static let groq = "groq-fixture-not-a-secret"
}

/// A `SettingsManager` that answers fixed keys and never reads the Keychain
/// for them. The router takes the concrete type, so this subclasses it.
@MainActor
private final class FixedKeySettings: SettingsManager {
    override func apiKey(for provider: CloudProvider) -> String {
        switch provider {
        case .openai: return FakeKeys.openAI
        case .groq: return FakeKeys.groq
        default: return ""
        }
    }
}

/// One request as it reached the transport.
private struct SentRequest {
    let url: String
    let authorization: String?
}

/// Records every request the router's Whisper providers send, and answers each
/// with a 200 `{"text": …}` so the core parser succeeds.
private final class RequestRecorder {
    private let sent = OSAllocatedUnfairLock(initialState: [SentRequest]())

    var requests: [SentRequest] {
        sent.withLock { $0 }
    }

    func execute(_ request: HttpRequest) -> HttpResponse {
        let authorization = request.headers.first {
            $0.name.caseInsensitiveCompare("Authorization") == .orderedSame
        }?.value
        sent.withLock { $0.append(SentRequest(url: request.url, authorization: authorization)) }
        return HttpResponse(status: 200, headers: [], body: Data(#"{"text":"fixture transcript"}"#.utf8))
    }
}

// MARK: - Tests

@MainActor
@Suite("OpenAI and Groq each keep their own vendor and key (#1338)", .serialized)
struct CloudWhisperPerVendorRoutingTests {

    private static let openAIHost = "https://api.openai.com/"
    private static let groqHost = "https://api.groq.com/"

    /// A router wired the way the app wires it, minus the network: a real
    /// health manager with both vendors cached healthy, and a recording
    /// transport. The settings object is returned so the caller keeps it
    /// alive (the router holds it weakly).
    private static func makeRouter(
        recorder: RequestRecorder
    ) -> (TranscriptionProviderRouter, FixedKeySettings, CloudProviderHealthManager) {
        let router = TranscriptionProviderRouter(cloudWhisperExecute: { request, _ in
            recorder.execute(request)
        })
        let settings = FixedKeySettings()
        let health = CloudProviderHealthManager()
        health.configure(apiKeyProvider: settings)
        health.setCachedTranscriptionStatusForTests(.healthy, for: .openai)
        health.setCachedTranscriptionStatusForTests(.healthy, for: .groq)
        router.setManagers(
            healthManager: health,
            licenseManager: nil,
            creditManager: nil,
            settingsManager: settings
        )
        return (router, settings, health)
    }

    /// A tiny valid WAV. CloudWhisperProvider only checks that the file exists
    /// and fits the size cap; the recorded transport never reads it.
    private static func temporaryAudio() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("cloud-whisper-1338-\(UUID().uuidString).wav")
        let samples = Data(count: 3_200) // 0.1 s of 16 kHz mono 16-bit silence
        var wav = Data()
        func append<T: FixedWidthInteger>(_ value: T) {
            withUnsafeBytes(of: value.littleEndian) { wav.append(contentsOf: $0) }
        }
        wav.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36 + samples.count))
        wav.append(contentsOf: Array("WAVE".utf8))
        wav.append(contentsOf: Array("fmt ".utf8)); append(UInt32(16)); append(UInt16(1)); append(UInt16(1))
        append(UInt32(16_000)); append(UInt32(32_000)); append(UInt16(2)); append(UInt16(16))
        wav.append(contentsOf: Array("data".utf8)); append(UInt32(samples.count))
        wav.append(samples)
        try wav.write(to: url)
        return url
    }

    private static func expectSent(
        _ request: SentRequest?,
        toHost host: String,
        withKey key: String,
        _ label: String
    ) {
        guard let request else {
            Issue.record("\(label): no request was sent")
            return
        }
        #expect(request.url.hasPrefix(host), "\(label) went to \(request.url), expected \(host)")
        #expect(request.authorization == "Bearer \(key)", "\(label) carried the wrong key")
    }

    /// The issue's own sequence: a Groq request resolves (configure + health
    /// check), an OpenAI request resolves and transcribes, then the Groq
    /// request transcribes. With one shared instance the Groq audio went to
    /// OpenAI on the OpenAI key.
    @Test func aGroqRequestOverlappedByOpenAIStillGoesToGroqWithTheGroqKey() async throws {
        let recorder = RequestRecorder()
        let (router, settings, health) = Self.makeRouter(recorder: recorder)
        let audio = try Self.temporaryAudio()
        defer { try? FileManager.default.removeItem(at: audio) }

        // Request A (engine=groq) passes configure and the health check...
        let groq = try await router.resolveProvider(engine: "groq", model: nil, language: nil)
        #expect(groq.cloudProviderType == .groq)

        // ...and before it transcribes, request B (engine=openai) runs.
        let openAI = try await router.resolveProvider(engine: "openai", model: nil, language: nil)
        #expect(openAI.cloudProviderType == .openai)
        _ = try await openAI.provider.transcribe(audioURL: audio, language: nil, mode: nil, vocabulary: [])

        // A resumes.
        _ = try await groq.provider.transcribe(audioURL: audio, language: nil, mode: nil, vocabulary: [])

        let sent = recorder.requests
        #expect(sent.count == 2)
        Self.expectSent(sent.first, toHost: Self.openAIHost, withKey: FakeKeys.openAI, "OpenAI request (B)")
        Self.expectSent(sent.dropFirst().first, toHost: Self.groqHost, withKey: FakeKeys.groq, "Groq request (A)")
        #expect(groq.provider.name == CloudProvider.groq.displayName)
        withExtendedLifetime((settings, health)) {}
    }

    /// The mirror image: an OpenAI request overlapped by a Groq one must still
    /// reach OpenAI on the OpenAI key.
    @Test func anOpenAIRequestOverlappedByGroqStillGoesToOpenAIWithTheOpenAIKey() async throws {
        let recorder = RequestRecorder()
        let (router, settings, health) = Self.makeRouter(recorder: recorder)
        let audio = try Self.temporaryAudio()
        defer { try? FileManager.default.removeItem(at: audio) }

        let openAI = try await router.resolveProvider(engine: "openai", model: nil, language: nil)
        let groq = try await router.resolveProvider(engine: "groq", model: nil, language: nil)
        _ = try await groq.provider.transcribe(audioURL: audio, language: nil, mode: nil, vocabulary: [])
        _ = try await openAI.provider.transcribe(audioURL: audio, language: nil, mode: nil, vocabulary: [])

        let sent = recorder.requests
        #expect(sent.count == 2)
        Self.expectSent(sent.first, toHost: Self.groqHost, withKey: FakeKeys.groq, "Groq request (B)")
        Self.expectSent(sent.dropFirst().first, toHost: Self.openAIHost, withKey: FakeKeys.openAI, "OpenAI request (A)")
        withExtendedLifetime((settings, health)) {}
    }

    /// The settings-change path (`refreshConfiguration(openAIAPIKey:)`) writes
    /// the OpenAI key. It used to land on the shared instance too, defaulting
    /// its vendor back to OpenAI under a Groq request in flight.
    @Test func anOpenAIKeyRefreshCannotRepointAGroqRequestInFlight() async throws {
        let recorder = RequestRecorder()
        let (router, settings, health) = Self.makeRouter(recorder: recorder)
        let audio = try Self.temporaryAudio()
        defer { try? FileManager.default.removeItem(at: audio) }

        let groq = try await router.resolveProvider(engine: "groq", model: nil, language: nil)
        router.refreshConfiguration(openAIAPIKey: FakeKeys.openAI)
        _ = try await groq.provider.transcribe(audioURL: audio, language: nil, mode: nil, vocabulary: [])

        let sent = recorder.requests
        #expect(sent.count == 1)
        Self.expectSent(sent.first, toHost: Self.groqHost, withKey: FakeKeys.groq, "Groq request")
        withExtendedLifetime((settings, health)) {}
    }
}
