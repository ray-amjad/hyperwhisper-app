//
//  PromptBuilder.swift
//  hyperwhisper
//
//  Centralized prompt templates and assembly for AI post-processing.
//  System prompt is static per mode/preset; dynamic context (time, app, vocab)
//  is returned separately via systemInfo() for prompt caching.
//
//  Prompt ASSEMBLY now lives in the shared Rust core (hw-text). This file is a
//  thin Swift shim: it maps the native `(Mode, ApplicationContext, [Vocabulary])`
//  inputs into the UniFFI-generated `PromptContext` struct and delegates to
//  `buildSystemPrompt(ctx:)` / `buildSystemInfo(ctx:)`. The public signatures
//  are unchanged so call sites are untouched.
//
//  Fields that depend on the host (clock, timezone, locale, computer name,
//  resolved language display name) are resolved NATIVELY here — the Rust core has
//  no clock or language catalog. The focused-element pre-join and focused-content
//  pre-truncation are also done natively to preserve byte-for-byte behaviour with
//  the previous `formatContextForPrompt` implementation.
//

import Foundation

// MARK: - Prompt Builder

enum PromptBuilder {

    /// Build the shared-core `PromptContext` from the native inputs.
    ///
    /// Host-dependent fields (time/timezone/locale/computer name and the resolved
    /// language display name) are filled in here; the Rust core fills the rest of
    /// the template. `focusedElement` and `focusedContent` are PRE-PROCESSED here
    /// to match the old native formatting exactly (the core truncates nothing).
    @MainActor
    private static func makeContext(
        mode: Mode,
        applicationContext: ApplicationContext?,
        vocabulary: [Vocabulary]
    ) -> PromptContext {
        // Use the passed context if available, otherwise gather fresh (parity
        // with the previous behaviour, which gathered when nil).
        let appContext = applicationContext ?? ApplicationContextGatherer.shared.gatherContext()

        let preset = presetFromRaw(raw: mode.preset ?? "")
        let customInstructions = (preset == .custom) ? (mode.customInstructions ?? "") : ""

        // Resolve the language display name natively (Rust has no language catalog).
        let resolvedLanguage: String = {
            guard let lang = mode.language,
                  !lang.isEmpty,
                  lang != LanguageData.automaticCode else {
                return ""
            }
            return LanguageData.displayName(for: lang)
        }()

        // Focused element: strip a leading "AX" from the role, then join
        // role + title as "<role> - <title>" (skipping nil parts).
        let focusedElement: String = {
            var parts: [String] = []
            if let role = appContext.focusedElement.role {
                parts.append(role.replacingOccurrences(of: "AX", with: ""))
            }
            if let title = appContext.focusedElement.title {
                parts.append(title)
            }
            return parts.isEmpty ? "" : parts.joined(separator: " - ")
        }()

        // Focused content: pre-truncate to 100 source characters (the core does
        // NOT truncate — keeping this native prevents full field content leaking).
        let focusedContent: String = {
            guard let value = appContext.focusedElement.value, !value.isEmpty else { return "" }
            return String(value.prefix(100)) + (value.count > 100 ? "..." : "")
        }()

        // RAW vocabulary words (replacement-bearing entries excluded). The core's
        // `build_system_info` sanitizes/joins — do NOT pre-sanitize here.
        let vocabularyWords = vocabulary
            .filter { $0.replacement == nil || $0.replacement!.isEmpty }
            .compactMap { $0.word }

        return PromptContext(
            preset: preset,
            customInstructions: customInstructions,
            englishSpelling: englishSpellingFromRaw(raw: mode.englishSpelling ?? ""),
            language: resolvedLanguage,
            userSystemPrompt: mode.userSystemPrompt ?? "",
            appType: hwAppType(from: appContext.appType),
            appName: appContext.appName,
            category: appContext.category,
            description: appContext.description,
            textFormat: appContext.textInputFormat,
            browserHost: appContext.browserHost ?? "",
            browserTabTitle: appContext.browserTabTitle ?? "",
            focusedElement: focusedElement,
            focusedContent: focusedContent,
            screenOcrText: appContext.screenOCRText ?? "",
            appTypeConfidence: appContext.appTypeConfidence,
            appTypeSource: appContext.appTypeSource,
            hasApplicationContext: appContext.appName.isEmpty == false,
            vocabularyWords: vocabularyWords,
            time: Date().formatted(date: .omitted, time: .shortened),
            timezone: TimeZone.current.abbreviation() ?? "Unknown",
            locale: Locale.current.identifier,
            computerName: Host.current().name ?? "Unknown",
            punctuation: mode.punctuation,
            capitalization: mode.capitalization,
            profanityFilter: mode.profanityFilter
        )
    }

    /// Map the native `AppType` to the shared-core `HwAppType`.
    private static func hwAppType(from appType: AppType) -> HwAppType {
        switch appType {
        case .email:             return .email
        case .ai:                return .ai
        case .workMessaging:     return .workMessaging
        case .personalMessaging: return .personalMessaging
        case .document:          return .document
        case .code:              return .code
        case .terminal:          return .terminal
        case .sensitive:         return .sensitive
        case .other:             return .other
        }
    }

    /// Builds the full static system prompt for the provided mode.
    /// Dynamic context (time, app context, vocabulary) is NOT included —
    /// use systemInfo() to get that separately for prompt caching.
    /// - Parameters:
    ///   - mode: The transcription mode containing preset and processing settings
    ///   - applicationContext: Optional pre-captured application context (if nil, will gather fresh)
    @MainActor
    static func systemPrompt(for mode: Mode, applicationContext: ApplicationContext? = nil) -> String {
        let ctx = makeContext(mode: mode, applicationContext: applicationContext, vocabulary: [])
        return buildSystemPrompt(ctx: ctx)
    }

    /// Builds the dynamic system info string (time, timezone, locale, app context, vocabulary, etc.).
    /// This content changes per-request and should be prepended to the user message
    /// so the static system prompt benefits from provider prompt caching.
    /// - Parameters:
    ///   - mode: The transcription mode (used for spelling/language settings)
    ///   - vocabulary: Array of custom vocabulary items for improved accuracy
    ///   - applicationContext: Optional pre-captured application context (if nil, will gather fresh)
    @MainActor
    static func systemInfo(for mode: Mode, vocabulary: [Vocabulary] = [], applicationContext: ApplicationContext? = nil) -> String {
        let ctx = makeContext(mode: mode, applicationContext: applicationContext, vocabulary: vocabulary)
        return buildSystemInfo(ctx: ctx)
    }

    /// Neutralizes a vocabulary word for safe interpolation into the prompt.
    /// Delegates to the shared Rust core so macOS/Windows sanitize identically,
    /// including the shared 80-character vocabulary term cap.
    static func sanitizeVocabularyWord(_ word: String) -> String {
        HyperWhisper.sanitizeVocabularyWord(word: word)
    }
}
