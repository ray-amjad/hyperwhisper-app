//
//  SmartSpacing.swift
//  hyperwhisper
//
//  Language-aware trailing space handling for consecutive transcriptions.
//  The logic now lives in the shared Rust core (`hw-text`); these are thin
//  delegating shims so macOS and Windows stay in lockstep. `appendTrailingSpace`
//  shares a base name with the global binding func, so it's module-qualified to
//  defeat member-shadowing.
//

import Foundation

struct SmartSpacing {

    /// Detect if text primarily contains CJK characters (no word spaces).
    /// Excludes Thai; streaming paste-vs-type uses `isContinuousScript` instead.
    static func containsCJKCharacters(_ text: String) -> Bool {
        HyperWhisper.containsCjk(text: text)
    }

    /// Whether an explicit language code joins without spaces (ja, zh*, ko, th, yue).
    /// Used by `TextInputService.typeSegment()` to pick paste over typing.
    static func isNoSpaceLanguage(_ code: String) -> Bool {
        HyperWhisper.isNoSpaceLanguage(languageCode: code)
    }

    /// Auto-detect half of the no-space rule: CJK or Thai text. Unlike
    /// `containsCJKCharacters`, this includes Thai.
    static func isContinuousScript(_ text: String) -> Bool {
        HyperWhisper.isContinuousScript(text: text)
    }

    /// Append a language-aware trailing space (no-op for CJK / already-spaced / empty).
    static func appendTrailingSpace(_ text: String, modeLanguage: String) -> String {
        HyperWhisper.appendTrailingSpace(text: text, modeLanguage: modeLanguage)
    }
}
