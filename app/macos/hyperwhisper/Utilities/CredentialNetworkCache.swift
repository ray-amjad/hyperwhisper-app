//
//  CredentialNetworkCache.swift
//  hyperwhisper
//
//  Keeps credentials out of the on-disk HTTP cache (#1491).
//
//  `URLSessionConfiguration.default` and `URLSession.shared` both write to
//  `URLCache.shared`, which lives at
//  `~/Library/Caches/com.hyperwhisper.hyperwhisper/Cache.db`. CFNetwork archives
//  the whole request next to a cached response: the URL, the headers and the
//  body. So a session that sends the account key (in a JSON body, or as the
//  `?identifier=` query) or a BYOK API key (in an `Authorization`-style header)
//  can leave that key in plain text in a file any process of the user can read,
//  and that every backup of `~/Library/Caches` copies.
//
//  Every session that carries a credential is built from
//  `URLSessionConfiguration.credentialBearing`, and a one-shot call that would
//  otherwise use `URLSession.shared` uses `CredentialNetworkCache.session`.
//  Both follow the pattern `HyperWhisperRoutedTranscription.sharedSession`
//  already used: `.default` with `urlCache = nil`, NOT `.ephemeral`. The only
//  difference from `.default` is the cache, so cookie and credential storage
//  behave exactly as before.
//

import Foundation

extension URLSessionConfiguration {

    /// A `.default` configuration with no URL cache: nothing is read from or
    /// written to `URLCache.shared`. Use it for every session whose requests
    /// carry the account key or an API key.
    static var credentialBearing: URLSessionConfiguration {
        let config = URLSessionConfiguration.default
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        return config
    }
}

enum CredentialNetworkCache {

    /// Stand-in for `URLSession.shared` at a call site that sends a credential.
    static let session = URLSession(configuration: .credentialBearing)

    /// Set once the one-time purge below has run.
    static let purgeDoneDefaultsKey = "credentialURLCachePurgeDone.1491"

    /// Removes cached responses that builds before #1491 wrote to the shared
    /// on-disk cache, once per install.
    ///
    /// Those rows hold the account key (licence validate body, credits
    /// `?identifier=` URL) and BYOK API keys (provider request headers), across
    /// many hosts and URLs, so a per-URL removal cannot reach them all. The whole
    /// shared cache is cleared instead. It is only a cache: nothing in the app
    /// depends on a row in it, and every later request simply goes to the
    /// network again.
    ///
    /// - Returns: `true` when the purge ran on this call.
    @discardableResult
    static func purgeLegacyCachedCredentialsIfNeeded(
        defaults: UserDefaults = .standard,
        clear: () -> Void = { URLCache.shared.removeAllCachedResponses() }
    ) -> Bool {
        guard !defaults.bool(forKey: purgeDoneDefaultsKey) else { return false }
        clear()
        defaults.set(true, forKey: purgeDoneDefaultsKey)
        return true
    }
}
