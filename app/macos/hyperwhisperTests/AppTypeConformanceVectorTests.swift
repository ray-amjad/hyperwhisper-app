//
//  AppTypeConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/app-type-vectors.json` against the Swift UniFFI
//  binding. Issue #279 deleted the native app-type classifiers, so there is
//  exactly one implementation of the 8-element priority order, the title
//  word-boundary rule, the host-suffix rule and the email regex; these vectors
//  prove macOS reads that one implementation's answer unchanged. Rust and C#
//  run the same file:
//
//    shared-core-rs/crates/hw-core/tests/app_type_vectors.rs
//    app/shared-dotnet/HyperWhisper.AppTypeConformance.Tests/Program.cs
//
//  Before #279 the three stacks had already drifted: macOS reported a host hit
//  as `strong` and Windows as `medium`, macOS had an `appName` signal and
//  Windows did not, macOS read five focused-element fields and Windows two, and
//  the webmail address fallback existed only here.
//
//  Regenerate the vectors from Rust after an intended catalog change:
//    cd shared-core-rs && cargo test -p hw-core --test app_type_vectors -- --ignored regenerate
//

import Foundation
import Testing
@testable import HyperWhisper

struct AppTypeConformanceVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let classifications: [CaseVector]
        let webmailTitles: [WebmailVector]
    }

    struct CaseVector: Decodable {
        let name: String
        let rule: String
        let request: RequestVector
        let expected: ExpectedVector
    }

    struct RequestVector: Decodable {
        let bundleId: String
        let processName: String
        let appName: String
        let host: String?
        let hostConfidence: String
        let title: String
        let focusedPieces: [String]
    }

    struct ExpectedVector: Decodable {
        let appType: String
        let promptValue: String
        let category: String
        let textInputFormat: String
        let confidence: String
        let source: String
        let matched: String?
    }

    struct WebmailVector: Decodable {
        let title: String
        let expected: Bool
        let branch: String
    }

    // MARK: - Loading

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/app-type-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    // MARK: - The vectors

    /// The binding is called directly rather than through
    /// `AppTypeClassifier.classify`, because that facade fixes `processName` to
    /// empty and `hostConfidence` to `strong` — the vectors have to be able to
    /// drive every field.
    private func classify(_ request: RequestVector) -> AppClassification {
        appClassify(request: AppClassifyRequest(
            bundleId: request.bundleId,
            processName: request.processName,
            appName: request.appName,
            host: request.host,
            hostConfidence: request.hostConfidence,
            title: request.title,
            focusedPieces: request.focusedPieces
        ))
    }

    private func name(_ appType: ClassifiedAppType) -> String {
        switch appType {
        case .email: return "Email"
        case .ai: return "Ai"
        case .workMessaging: return "WorkMessaging"
        case .personalMessaging: return "PersonalMessaging"
        case .document: return "Document"
        case .code: return "Code"
        case .terminal: return "Terminal"
        case .sensitive: return "Sensitive"
        case .other: return "Other"
        }
    }

    @Test("classification matches the shared vectors")
    func classificationMatchesVectors() {
        for vector in document.classifications {
            let actual = classify(vector.request)
            #expect(name(actual.appType) == vector.expected.appType, "\(vector.name): appType")
            #expect(actual.confidence == vector.expected.confidence, "\(vector.name): confidence")
            #expect(actual.source == vector.expected.source, "\(vector.name): source")
            #expect(actual.matched == vector.expected.matched, "\(vector.name): matched")
        }
    }

    /// `AppType`'s three derived properties are the one piece of app-type
    /// behaviour macOS still owns. They are only safe to keep while they agree
    /// with the shared core, which resolves the same three strings on every
    /// classification.
    @Test("the derived AppType strings match the shared vectors")
    func derivedStringsMatchVectors() {
        for vector in document.classifications {
            guard let native = AppType(rawValue: nativeRawValue(vector.expected.appType)) else {
                Issue.record("unknown app type \(vector.expected.appType) in \(vector.name)")
                continue
            }
            #expect(native.promptValue == vector.expected.promptValue, "\(vector.name): promptValue")
            #expect(native.category == vector.expected.category, "\(vector.name): category")
            #expect(
                native.textInputFormat == vector.expected.textInputFormat,
                "\(vector.name): textInputFormat")
        }
    }

    /// The vectors name the FFI variant (`WorkMessaging`); `AppType` is keyed on
    /// its own rawValue (`workMessaging`).
    private func nativeRawValue(_ variant: String) -> String {
        guard let first = variant.first else { return variant }
        return first.lowercased() + variant.dropFirst()
    }

    @Test("webmail detection matches the shared vectors")
    func webmailMatchesVectors() {
        for vector in document.webmailTitles {
            #expect(
                AppTypeClassifier.isWebmail(vector.title) == vector.expected,
                "isWebmail(\(vector.title))")
        }
    }

    /// A vector file that stopped exercising a rule would pass every check
    /// above while proving nothing. This mirrors the Rust runner's own coverage
    /// test.
    @Test("the vectors cover every rule and signal")
    func vectorsCoverEveryRule() {
        #expect(document.classifications.count >= 40)

        for rule in ["priorityOrder", "wordBoundary", "hostSuffix", "emailRegex"] {
            #expect(
                document.classifications.filter { $0.rule == rule }.count >= 4,
                "rule \(rule) has too few vectors")
        }

        for source in [
            "browserHost", "bundleId", "processName", "title",
            "appName", "focusedElement", "focusedElementText", "default"
        ] {
            #expect(
                document.classifications.contains { $0.expected.source == source },
                "no vector reaches the \(source) signal")
        }

        for appType in [
            "Email", "Ai", "WorkMessaging", "PersonalMessaging",
            "Document", "Code", "Terminal", "Sensitive", "Other"
        ] {
            #expect(
                document.classifications.contains { $0.expected.appType == appType },
                "no vector classifies as \(appType)")
        }

        for branch in ["keyword", "address", "none"] {
            #expect(
                document.webmailTitles.contains { $0.branch == branch },
                "no webmail vector is a \(branch) case")
        }
        #expect(document.webmailTitles.contains { $0.expected })
        #expect(document.webmailTitles.contains { !$0.expected })
    }
}
