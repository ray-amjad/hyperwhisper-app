//
//  SentryRedactionTests.swift
//  hyperwhisperTests
//
//  `SentryRedaction` is what `beforeSend`, `beforeSendSpan` and
//  `beforeBreadcrumb` run on a failed request's URL, query and headers, on a
//  network span's data and description, and on an http breadcrumb's data.
//  Those hooks take Sentry types, which this target does not link, so this
//  file pins the plain-Swift decisions underneath them.
//
//  Every fake credential is built at run time, never written as a literal.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Sentry credential redaction")
struct SentryRedactionTests {

    /// Matches a shape `scrubFreeText` knows (`sk-…`).
    let shapedKey = ["sk", "test", "FAKEKEY123"].joined(separator: "-")
    /// Matches NO shape: only its header or parameter name can hide it.
    let opaqueKey = String(repeating: "ab12", count: 10)
    /// The HyperWhisper Account Key shape, `HW-` and four groups of four.
    let accountKey = ["HW", "AB12", "CD34", "EF56", "GH78"].joined(separator: "-")

    var allKeys: [String] { [shapedKey, opaqueKey, accountKey] }

    func leaks(_ text: String?) -> Bool {
        guard let text else { return false }
        return allKeys.contains { text.contains($0) }
    }

    func leaks(_ data: [String: Any]) -> Bool {
        leaks(String(describing: data))
    }

    // MARK: - Request

    @Test func requestURLLosesQueryFragmentAndUserInfo() {
        let url = "https://user:\(opaqueKey)@generativelanguage.googleapis.com/v1/models?key=\(opaqueKey)#t=\(opaqueKey)"
        let scrubbed = SentryRedaction.scrubURL(url)
        #expect(scrubbed == "https://generativelanguage.googleapis.com/v1/models")
        #expect(!leaks(scrubbed))
    }

    @Test func accountKeyInAPathIsRedacted() {
        let scrubbed = SentryRedaction.scrubURL("https://api.example.com/license/\(accountKey)/status")
        #expect(scrubbed == "https://api.example.com/license/[redacted]/status")
    }

    @Test func queryStringRedactsSensitiveParametersAndKeepsTheRest() {
        let query = "model=nova-3&key=\(opaqueKey)&api_key=\(opaqueKey)&licenseKey=\(accountKey)&language=en&x=\(shapedKey)"
        let scrubbed = SentryRedaction.scrubQueryString(query)
        #expect(!leaks(scrubbed))
        #expect(scrubbed == "model=nova-3&key=[redacted]&api_key=[redacted]&licenseKey=[redacted]&language=en&x=[redacted]")
        #expect(SentryRedaction.scrubQueryString("?token=\(opaqueKey)") == "?token=[redacted]")
        #expect(SentryRedaction.scrubQueryString(nil) == nil)
        #expect(SentryRedaction.scrubQueryString("") == "")
    }

    @Test func sensitiveHeadersAreRedactedAndBenignOnesSurvive() {
        let headers = [
            "Authorization": "Bearer \(opaqueKey)",
            "Proxy-Authorization": "Basic \(opaqueKey)",
            "Cookie": "session=\(opaqueKey)",
            "Set-Cookie": "sid=\(opaqueKey)",
            "X-Api-Key": opaqueKey,
            "xi-api-key": opaqueKey,
            "api-key": opaqueKey,
            "x-goog-api-key": opaqueKey,
            "X-License-Token": accountKey,
            "X-Client-Secret": opaqueKey,
            "X-Signature": opaqueKey,
            "X-Debug": "forwarded \(shapedKey)",
            "Content-Type": "application/json",
            "User-Agent": "HyperWhisper/2.40",
            "anthropic-version": "2023-06-01"
        ]
        let scrubbed = SentryRedaction.scrubHeaders(headers)
        #expect(!leaks(String(describing: scrubbed)))
        #expect(scrubbed["Authorization"] == "[redacted]")
        #expect(scrubbed["xi-api-key"] == "[redacted]")
        #expect(scrubbed["X-Debug"] == "forwarded [redacted]")
        #expect(scrubbed["Content-Type"] == "application/json")
        #expect(scrubbed["User-Agent"] == "HyperWhisper/2.40")
        #expect(scrubbed["anthropic-version"] == "2023-06-01")
        #expect(scrubbed.count == headers.count)
    }

    // MARK: - Free text

    @Test func freeTextRedactsKeysAndKeyParametersInsideAURL() {
        let text = "HTTP 503 for https://api.example.com/v1?key=\(opaqueKey)&model=x with Bearer \(opaqueKey)1 and \(shapedKey) and \(accountKey)"
        let scrubbed = SentryRedaction.scrubFreeText(text)
        #expect(!leaks(scrubbed))
        #expect(scrubbed.contains("https://api.example.com/v1?key=[redacted]&model=x"))
        #expect(scrubbed.contains("Bearer [redacted]"))
    }

    @Test func freeTextLeavesOrdinaryWordsAlone() {
        let benign = [
            "HTTP Client Error with status code: 503",
            "Basic settings failed to load",
            "Token refreshing",
            "task-force risk-assessment",
            "https://api.example.com/v1/listen?model=nova-3&language=en",
            "utm_campaign=hw-launch"
        ]
        for text in benign {
            #expect(SentryRedaction.scrubFreeText(text) == text)
        }
    }

    // MARK: - Span data and description

    @Test func networkSpanDataLosesKeys() {
        let data: [String: Any] = [
            "url": "https://api.groq.com/openai/v1/audio?key=\(opaqueKey)",
            "http.url": "https://api.example.com/?token=\(opaqueKey)",
            "http.query": "key=\(opaqueKey)&model=whisper-large-v3",
            "http.fragment": "access_token=\(opaqueKey)",
            "http.request.method": "POST",
            "http.response.status_code": "503",
            "type": "fetch",
            "thread.id": 7,
            "blocked_main_thread": false
        ]
        let scrubbed = SentryRedaction.scrubData(data)
        #expect(!leaks(scrubbed))
        #expect(scrubbed["url"] as? String == "https://api.groq.com/openai/v1/audio")
        #expect(scrubbed["http.query"] as? String == "key=[redacted]&model=whisper-large-v3")
        #expect(scrubbed["http.fragment"] as? String == "[redacted]")
        #expect(scrubbed["http.request.method"] as? String == "POST")
        #expect(scrubbed["http.response.status_code"] as? String == "503")
        #expect(scrubbed["type"] as? String == "fetch")
        #expect(scrubbed["thread.id"] as? Int == 7)
        #expect(scrubbed["blocked_main_thread"] as? Bool == false)
    }

    @Test func spanDescriptionLosesKeys() {
        let description = "GET https://generativelanguage.googleapis.com/v1/models?key=\(opaqueKey)"
        let scrubbed = SentryRedaction.scrubFreeText(description)
        #expect(!leaks(scrubbed))
        #expect(scrubbed == "GET https://generativelanguage.googleapis.com/v1/models?key=[redacted]")
        #expect(SentryRedaction.scrubFreeText("transcribe.cloud") == "transcribe.cloud")
    }

    // MARK: - Breadcrumb data and event context

    @Test func httpBreadcrumbDataLosesKeys() {
        let data: [String: Any] = [
            "url": "https://api.elevenlabs.io/v1/speech-to-text?xi_api_key=\(opaqueKey)",
            "http.query": "xi_api_key=\(opaqueKey)",
            "http.fragment": "x",
            "method": "POST",
            "status_code": 502,
            "request_body_size": 1024,
            "reason": "Bad Gateway",
            "nested": ["apiKey": opaqueKey, "detail": "via \(shapedKey)", "items": [accountKey, "ok"]] as [String: Any]
        ]
        let scrubbed = SentryRedaction.scrubData(data)
        #expect(!leaks(scrubbed))
        #expect(scrubbed["url"] as? String == "https://api.elevenlabs.io/v1/speech-to-text")
        #expect(scrubbed["method"] as? String == "POST")
        #expect(scrubbed["status_code"] as? Int == 502)
        #expect(scrubbed["request_body_size"] as? Int == 1024)
        #expect(scrubbed["reason"] as? String == "Bad Gateway")
        let nested = scrubbed["nested"] as? [String: Any]
        #expect((nested?["items"] as? [Any])?.last as? String == "ok")
    }

    @Test func responseContextHeadersAreScrubbed() {
        let response: [String: Any] = [
            "status_code": 503,
            "body_size": 12,
            "headers": ["Set-Cookie": "sid=\(opaqueKey)", "Content-Type": "text/plain", "X-Api-Key": opaqueKey]
        ]
        let scrubbed = SentryRedaction.scrubData(response)
        #expect(!leaks(scrubbed))
        let headers = scrubbed["headers"] as? [String: Any]
        #expect(headers?["Content-Type"] as? String == "text/plain")
        #expect(headers?["Set-Cookie"] as? String == "[redacted]")
        #expect(scrubbed["status_code"] as? Int == 503)
    }

    // MARK: - Names

    @Test func sensitiveNamesMatchAnyCaseAndSeparator() {
        for name in ["Authorization", "PROXY-AUTHORIZATION", "cookie", "Set-Cookie", "x-api-key",
                     "XI-API-KEY", "apiKey", "api_key", "access_token", "client_secret",
                     "password", "X-Amz-Signature", "sig", "code"] {
            #expect(SentryRedaction.isSensitiveName(name), "\(name)")
        }
        for name in ["Content-Type", "User-Agent", "Accept", "model", "language", "url",
                     "anthropic-version", "status_code"] {
            #expect(!SentryRedaction.isSensitiveName(name), "\(name)")
        }
    }
}
