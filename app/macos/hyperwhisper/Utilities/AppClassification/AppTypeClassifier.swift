//
//  AppTypeClassifier.swift
//  hyperwhisper
//
//  Shared catalog-backed application classification for app-aware formatting.
//
//  The ALGORITHM is not here. Issue #279 moved it into `hw-catalog` and this is
//  now a facade over `appClassify` / `appIsWebmail`: the 8-element priority
//  array, the keyword-prep rule, the word-boundary rule, the host-suffix rule
//  and the email regex existed in Swift, C# and Rust, and had already drifted.
//  `shared-conformance/app-type-vectors.json`, run by
//  `AppTypeConformanceVectorTests`, pins the answer this file returns.
//

import Foundation

public enum AppType: String, Codable {
    case email
    case ai
    case workMessaging
    case personalMessaging
    case document
    case code
    case terminal
    case sensitive
    case other

    var promptValue: String {
        switch self {
        case .workMessaging:
            return "work_messaging"
        case .personalMessaging:
            return "personal_messaging"
        default:
            return rawValue
        }
    }

    var category: String {
        switch self {
        case .email:
            return "Email Client"
        case .ai:
            return "AI"
        case .workMessaging, .personalMessaging:
            return "Communication"
        case .document:
            return "Document"
        case .code:
            return "Code Editor"
        case .terminal:
            return "Terminal"
        case .sensitive:
            return "Sensitive"
        case .other:
            return "Application"
        }
    }

    var textInputFormat: String {
        switch self {
        case .email:
            return "email"
        case .code:
            return "code"
        case .terminal:
            return "command"
        case .document:
            return "markdown"
        default:
            return "text"
        }
    }

    fileprivate init(_ classified: ClassifiedAppType) {
        switch classified {
        case .email: self = .email
        case .ai: self = .ai
        case .workMessaging: self = .workMessaging
        case .personalMessaging: self = .personalMessaging
        case .document: self = .document
        case .code: self = .code
        case .terminal: self = .terminal
        case .sensitive: self = .sensitive
        case .other: self = .other
        }
    }
}

public struct AppClassificationResult {
    let appType: AppType
    let confidence: String
    let source: String
    let matched: String?
}

public final class AppTypeClassifier {
    public static let shared = AppTypeClassifier()

    private init() {}

    /// Classify the frontmost app. Signals are tried in order — host, bundle
    /// id, title, app name, focused element — and the first hit wins.
    ///
    /// `browserTitle` is the browser TAB title, not the window title. Windows
    /// joins both; the shared core takes an already-composed string, so
    /// widening this is a change here and nowhere else.
    public func classify(
        bundleId: String,
        appName: String,
        browserHost: String?,
        browserTitle: String?,
        focusedElement: FocusedElementInfo?
    ) -> AppClassificationResult {
        let result = appClassify(request: AppClassifyRequest(
            bundleId: bundleId.trimmingCharacters(in: .whitespacesAndNewlines),
            processName: "",
            appName: appName,
            host: browserHost,
            // macOS has always reported a host hit as `strong`. Windows reports
            // `medium` and the Local API `manual`, and that string reaches the
            // LLM prompt, so the caller owns it rather than the core.
            hostConfidence: "strong",
            title: browserTitle ?? "",
            focusedPieces: Self.focusedPieces(focusedElement)
        ))

        return AppClassificationResult(
            appType: AppType(result.appType),
            confidence: result.confidence,
            source: result.source,
            matched: result.matched
        )
    }

    /// Whether a browser-tab title looks like webmail. Call this ONLY when the
    /// frontmost app is already known to be a browser and nothing else
    /// classified the window — a title is not evidence of webmail on its own.
    public static func isWebmail(_ tabTitle: String) -> Bool {
        appIsWebmail(title: tabTitle)
    }

    /// The five accessibility fields macOS reads. Windows supplies two; the
    /// core takes whatever the platform has, joins the non-blank pieces and
    /// scans the result.
    ///
    /// These values are NOT truncated on the way in. `PromptBuilder` bounds
    /// `focusedContent` to 100 characters before it reaches the prompt, but the
    /// email scan has always run against the full value, and truncating first
    /// would drop an address that sits past the cut.
    private static func focusedPieces(_ focusedElement: FocusedElementInfo?) -> [String] {
        guard let focusedElement else { return [] }
        return [
            focusedElement.role,
            focusedElement.title,
            focusedElement.description,
            focusedElement.placeholder,
            focusedElement.value
        ].compactMap { $0 }
    }
}
