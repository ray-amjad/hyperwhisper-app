//
//  HyperWhisperClientInfo.swift
//  hyperwhisper
//
//  WHICH APP AND WHICH BUILD IS CALLING THE CLOUD
//
//  Every HyperWhisper Cloud request carries the platform and the app version:
//
//    X-HyperWhisper-Platform: macos
//    X-HyperWhisper-Version:  2.41.0
//
//  The backend reads both into its structured log lines
//  (`hyperwhisper-cloud/src/lib/client-info.ts`), so a regression can be scoped
//  to one platform and one build without asking the user for their version.
//
//  The Windows twin is `app/windows/HyperWhisper/Services/ClientInfoHeaders.cs`.
//  Keep the header names and the platform token in step with it.
//
//  These headers are additive: the existing `User-Agent` stays as it is, because
//  the backend still parses it as the fallback for builds shipped before this.
//

import Foundation

enum HyperWhisperClientInfo {
    /// Header names agreed with the backend.
    static let platformHeaderName = "X-HyperWhisper-Platform"
    static let versionHeaderName = "X-HyperWhisper-Version"

    /// Platform token. Lowercase — the backend buckets on the exact string.
    static let platform = "macos"

    /// Marketing version (`CFBundleShortVersionString`), e.g. `2.41.0`.
    ///
    /// The backend drops any value with characters outside `[A-Za-z0-9._-]`, so
    /// the fallback stays inside that alphabet.
    static var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "unknown"
    }

    /// Appends the headers to a core-built request.
    static func apply(to request: inout HttpRequest) {
        request.headers.append(Header(name: platformHeaderName, value: platform))
        request.headers.append(Header(name: versionHeaderName, value: version))
    }

    /// Sets the headers on a natively built request.
    static func apply(to request: inout URLRequest) {
        request.setValue(platform, forHTTPHeaderField: platformHeaderName)
        request.setValue(version, forHTTPHeaderField: versionHeaderName)
    }
}
