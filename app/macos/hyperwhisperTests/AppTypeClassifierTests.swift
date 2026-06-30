//
//  AppTypeClassifierTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

struct AppTypeClassifierTests {
    private let classifier: AppTypeClassifier

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-app-classification/app-type-catalog.json")
        classifier = try AppTypeClassifier(catalogData: Data(contentsOf: url))
    }

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
}
