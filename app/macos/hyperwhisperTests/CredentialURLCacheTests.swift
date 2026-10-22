//
//  CredentialURLCacheTests.swift
//  hyperwhisperTests
//
//  #1491: a session that sends the account key or an API key must not use the
//  on-disk URL cache. A `.default` configuration writes to `URLCache.shared`
//  (`~/Library/Caches/com.hyperwhisper.hyperwhisper/Cache.db`), and CFNetwork
//  archives the request (URL, headers, body) next to each cached response, so
//  the licence validate body and the credits `?identifier=` URL left the key in
//  plain text on disk.
//

import Foundation
import Testing
@testable import HyperWhisper

struct CredentialURLCacheTests {

    // MARK: - The configuration every credential-bearing session uses

    @Test func defaultConfigurationHasADiskCacheSoTheseTestsAreNotVacuous() {
        #expect(URLSessionConfiguration.default.urlCache != nil)
    }

    @Test func credentialBearingConfigurationHasNoURLCache() {
        let config = URLSessionConfiguration.credentialBearing
        #expect(config.urlCache == nil)
        #expect(config.requestCachePolicy == .reloadIgnoringLocalCacheData)
    }

    @Test func credentialBearingKeepsDefaultCookieBehaviour() {
        // Only the cache differs from `.default`; `.ephemeral` would also have
        // swapped the cookie and credential stores for in-memory ones.
        let config = URLSessionConfiguration.credentialBearing
        #expect(config.httpCookieStorage === URLSessionConfiguration.default.httpCookieStorage)
        #expect(config.urlCredentialStorage === URLSessionConfiguration.default.urlCredentialStorage)
    }

    @Test func sharedCredentialSessionHasNoURLCache() {
        #expect(CredentialNetworkCache.session.configuration.urlCache == nil)
    }

    // MARK: - The two sites #1491 names

    @Test func licenceValidationSessionHasNoURLCache() {
        let session = LicenseNetworkService.makeDefaultSession()
        defer { session.invalidateAndCancel() }
        #expect(session.configuration.urlCache == nil)
        #expect(session.configuration.timeoutIntervalForRequest == NetworkConfig.licenseValidationTimeout)
    }

    @Test func cloudCreditsSessionHasNoURLCache() {
        let config = HyperWhisperCloudManager.makeSessionConfiguration()
        #expect(config.urlCache == nil)
        #expect(config.timeoutIntervalForRequest == 10.0)
        #expect(config.timeoutIntervalForResource == 15.0)
    }

    // MARK: - Siblings

    /// Every file whose session sends the account key or a BYOK API key. The
    /// providers' sessions are `private lazy var`s, so this reads the wiring
    /// from source (see `ProductionSource` for why that is the last resort).
    static let credentialBearingSources = [
        "app/macos/hyperwhisper/Managers/LicenseNetworkService.swift",
        "app/macos/hyperwhisper/Managers/HyperWhisperCloudManager.swift",
        "app/macos/hyperwhisper/Managers/CustomPostProcessingManager.swift",
        "app/macos/hyperwhisper/Managers/AudioRecording/Streaming/StreamingTranscriptionClient.swift",
        "app/macos/hyperwhisper/Managers/Transcription/PostProcessing/AIPostProcessor.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/AssemblyAIProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/CloudWhisperProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/DeepgramProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/ElevenLabsProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/GeminiTranscribeProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/GeminiTranscriptionProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/GrokSTTProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/MetaMuseProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/MistralProvider.swift",
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/SonioxProvider.swift",
        "app/macos/hyperwhisper/Views/Settings/CustomEndpointSheet.swift",
    ]

    /// Spellings that reach `URLCache.shared`. `URLSession.shared.bytes` is
    /// not listed: `AIPostProcessor`'s one streaming call goes to the local
    /// llama-server with no key.
    static let cachingSpellings = [
        "URLSessionConfiguration.default",
        "configuration: .default",
        "URLSession.shared.data(",
    ]

    @Test(arguments: CredentialURLCacheTests.credentialBearingSources)
    func credentialBearingFileUsesNoCachingSession(_ path: String) throws {
        let code = try ProductionSource.code(of: path)
        for spelling in Self.cachingSpellings {
            #expect(!code.contains(spelling), "\(path) uses \(spelling), which writes to the on-disk URL cache")
        }
    }

    // MARK: - One-time purge of rows older builds wrote

    @Test func purgeClearsTheCacheOnceAndThenNeverAgain() throws {
        let suiteName = "CredentialURLCacheTests.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        var clears = 0
        let first = CredentialNetworkCache.purgeLegacyCachedCredentialsIfNeeded(
            defaults: defaults,
            clear: { clears += 1 }
        )
        let second = CredentialNetworkCache.purgeLegacyCachedCredentialsIfNeeded(
            defaults: defaults,
            clear: { clears += 1 }
        )

        #expect(first)
        #expect(!second)
        #expect(clears == 1)
        #expect(defaults.bool(forKey: CredentialNetworkCache.purgeDoneDefaultsKey))
    }
}
