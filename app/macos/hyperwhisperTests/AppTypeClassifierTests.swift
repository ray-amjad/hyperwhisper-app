//
//  AppTypeClassifierTests.swift
//  hyperwhisperTests
//
//  The rules themselves are proved by AppTypeConformanceVectorTests against
//  shared-conformance/app-type-vectors.json, which Rust and C# run too. What is
//  left here is what only macOS can get wrong: the shape of the call this app
//  makes into the shared classifier.
//

import Foundation
import Testing
@testable import HyperWhisper

struct AppTypeClassifierTests {
    private let classifier = AppTypeClassifier.shared

    @Test func hostMatchingDetectsBrowserEmail() {
        let result = classifier.classify(
            bundleId: "com.google.Chrome",
            appName: "Google Chrome",
            browserHost: "mail.google.com",
            browserTitle: "Ray",
            focusedElement: nil
        )

        #expect(result.appType == .email)
        #expect(result.source == "browserHost")
        // macOS has always reported a host hit as `strong`; Windows says
        // `medium` and the Local API `manual`. The core takes whatever the
        // caller passes, so this app's choice is asserted here.
        #expect(result.confidence == "strong")
    }

    @Test func priorityClassifiesCursorAsCodeNotAi() {
        let result = classifier.classify(
            bundleId: "com.todesktop.230313mzl4w4u92",
            appName: "Cursor",
            browserHost: nil,
            browserTitle: nil,
            focusedElement: nil
        )

        #expect(result.appType == .code)
    }

    @Test func priorityClassifiesWarpAsTerminalNotAi() {
        let result = classifier.classify(
            bundleId: "dev.warp.Warp-Stable",
            appName: "Warp",
            browserHost: nil,
            browserTitle: nil,
            focusedElement: nil
        )

        #expect(result.appType == .terminal)
    }

    @Test func sensitiveAppsWinPriority() {
        let result = classifier.classify(
            bundleId: "com.1password.1password",
            appName: "1Password",
            browserHost: nil,
            browserTitle: nil,
            focusedElement: nil
        )

        #expect(result.appType == .sensitive)
    }

    @Test func titleKeywordRequiresWordBoundary() {
        let result = classifier.classify(
            bundleId: "com.google.Chrome",
            appName: "Google Chrome",
            browserHost: nil,
            browserTitle: "They have arrived - Google Chrome",
            focusedElement: nil
        )

        #expect(result.appType == .other)
    }

    /// All five accessibility fields reach the core. Windows sends two; a
    /// mapping that dropped `placeholder` or `description` would still pass
    /// every other test in this file.
    @Test func focusedElementFieldsAllReachTheCore() {
        let elements: [(String, FocusedElementInfo)] = [
            ("role", FocusedElementInfo(
                role: "Subject", title: nil, description: nil, value: nil, placeholder: nil)),
            ("title", FocusedElementInfo(
                role: nil, title: "Subject", description: nil, value: nil, placeholder: nil)),
            ("description", FocusedElementInfo(
                role: nil, title: nil, description: "Subject line", value: nil, placeholder: nil)),
            ("value", FocusedElementInfo(
                role: nil, title: nil, description: nil, value: "Subject", placeholder: nil)),
            ("placeholder", FocusedElementInfo(
                role: nil, title: nil, description: nil, value: nil, placeholder: "Subject"))
        ]

        for (field, element) in elements {
            let result = classifier.classify(
                bundleId: "",
                appName: "",
                browserHost: nil,
                browserTitle: nil,
                focusedElement: element
            )
            #expect(result.appType == .email, "the \(field) field did not reach the core")
            #expect(result.source == "focusedElement", "the \(field) field did not reach the core")
        }
    }

    /// The focused value is NOT truncated before the email scan. `PromptBuilder`
    /// bounds `focusedContent` to 100 characters on its way to the prompt, and
    /// an address past that cut must still classify.
    @Test func focusedValueIsScannedUntruncated() {
        let padding = String(repeating: "x", count: 200)
        let result = classifier.classify(
            bundleId: "",
            appName: "",
            browserHost: nil,
            browserTitle: nil,
            focusedElement: FocusedElementInfo(
                role: "AXTextArea",
                title: nil,
                description: nil,
                value: "\(padding) ray@example.com",
                placeholder: nil
            )
        )

        #expect(result.appType == .email)
        #expect(result.source == "focusedElementText")
        #expect(result.confidence == "weak")
    }

    /// The webmail safety net is the shared one, including the "[Name] Mail"
    /// address fallback that Windows used to lack.
    @Test func webmailDetectionIsShared() {
        #expect(AppTypeClassifier.isWebmail("Inbox (12) - Gmail"))
        #expect(AppTypeClassifier.isWebmail("ray@acme.co Mail"))
        #expect(!AppTypeClassifier.isWebmail("Acme Team - Slack"))
    }
}
