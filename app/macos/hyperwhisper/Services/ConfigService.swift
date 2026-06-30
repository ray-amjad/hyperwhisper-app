//
//  ConfigService.swift
//  hyperwhisper
//
//  Fetches remote trial configuration from the server.
//  Caches values in UserDefaults with server-driven expiry (Cache-Control max-age).
//  Falls back to hardcoded defaults when offline or on error.
//

import Foundation
import os

struct RemoteConfig {
    let trialDailyLimitSeconds: Int
    let trialModelDownloadLimit: Int
    /// Server-driven cache TTL (Cache-Control max-age, seconds). Threaded into the
    /// core's remote-override store so `licenseRemoteOverrideIfFresh` honors it (B4).
    let maxAgeSeconds: Int
}

final class ConfigService {

    static let shared = ConfigService()

    private let logger = Logger(subsystem: "com.hyperwhisper.hyperwhisper", category: "ConfigService")

    // UserDefaults keys
    private let kDailyLimit = "config.trialDailyLimitSeconds"
    private let kModelLimit = "config.trialModelDownloadLimit"
    private let kLastFetch = "config.lastFetchTimestamp"
    private let kMaxAge = "config.maxAge"

    private init() {}

    /// Returns cached config if fresh (within server's max-age), otherwise nil.
    func getCachedConfig() -> RemoteConfig? {
        let defaults = UserDefaults.standard

        guard defaults.object(forKey: kDailyLimit) != nil,
              defaults.object(forKey: kLastFetch) != nil else {
            return nil
        }

        let lastFetch = defaults.double(forKey: kLastFetch)
        let maxAge = defaults.double(forKey: kMaxAge)

        // Check if cache is still fresh
        if Date().timeIntervalSince1970 - lastFetch > maxAge {
            return nil
        }

        return RemoteConfig(
            trialDailyLimitSeconds: defaults.integer(forKey: kDailyLimit),
            trialModelDownloadLimit: defaults.integer(forKey: kModelLimit),
            maxAgeSeconds: Int(maxAge)
        )
    }

    /// Fetches config from the server, caches it, and returns the values.
    /// Returns nil on any error (network, parse, non-200 status).
    func fetchConfig() async -> RemoteConfig? {
        let urlString = "\(NetworkConfig.baseURL)/api/config"
        guard let url = URL(string: urlString) else { return nil }

        do {
            var request = URLRequest(url: url)
            request.httpMethod = "GET"
            request.timeoutInterval = 10
            request.setValue("HyperWhisper/1.0", forHTTPHeaderField: "User-Agent")

            let (data, response) = try await URLSession.shared.data(for: request)

            guard let httpResponse = response as? HTTPURLResponse,
                  httpResponse.statusCode == 200 else {
                logger.warning("Config fetch failed with non-200 status")
                return nil
            }

            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let dailyLimit = json["trial_daily_limit_seconds"] as? Int,
                  let modelLimit = json["trial_model_download_limit"] as? Int else {
                logger.warning("Config fetch: failed to parse response")
                return nil
            }

            // Parse max-age from Cache-Control header
            var maxAge: Double = 21600 // default 6 hours
            if let cacheControl = httpResponse.value(forHTTPHeaderField: "Cache-Control") {
                if let range = cacheControl.range(of: "max-age=") {
                    let afterMaxAge = cacheControl[range.upperBound...]
                    if let seconds = Double(afterMaxAge.prefix(while: { $0.isNumber })) {
                        maxAge = seconds
                    }
                }
            }

            // Cache to UserDefaults
            let defaults = UserDefaults.standard
            defaults.set(dailyLimit, forKey: kDailyLimit)
            defaults.set(modelLimit, forKey: kModelLimit)
            defaults.set(Date().timeIntervalSince1970, forKey: kLastFetch)
            defaults.set(maxAge, forKey: kMaxAge)

            logger.info("Config fetched: dailyLimit=\(dailyLimit, privacy: .public)s, modelLimit=\(modelLimit, privacy: .public), maxAge=\(maxAge, privacy: .public)s")

            return RemoteConfig(
                trialDailyLimitSeconds: dailyLimit,
                trialModelDownloadLimit: modelLimit,
                maxAgeSeconds: Int(maxAge)
            )
        } catch {
            logger.warning("Config fetch error: \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }
}
