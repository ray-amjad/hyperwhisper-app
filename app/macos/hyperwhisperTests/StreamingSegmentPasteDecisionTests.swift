//
//  StreamingSegmentPasteDecisionTests.swift
//  hyperwhisperTests
//
//  Pins the streaming paste-vs-type decision to the shared core's no-space
//  rule (issue #901): an explicit language decides by `is_no_space_language`,
//  and auto-detect decides by `is_continuous_script`, which includes Thai.
//

import Testing
@testable import HyperWhisper

struct StreamingSegmentPasteDecisionTests {

    private let thaiText = "สวัสดีครับ วันนี้อากาศดี"
    private let japaneseText = "今日はいい天気です"
    private let englishText = "Hello world, this is a test"

    private func shouldPaste(_ text: String, _ language: String?) -> Bool {
        TextInputService.streamingSegmentShouldPaste(text, language: language)
    }

    // The 4 cases the private ["ja", "zh", "ko"] literal got wrong.

    @Test func thaiLanguagePastes() {
        #expect(shouldPaste(thaiText, "th"))
    }

    @Test func cantoneseLanguagePastes() {
        #expect(shouldPaste("今日天氣好好", "yue"))
    }

    @Test func traditionalChineseSpellingPastes() {
        #expect(shouldPaste("今天天氣很好", "zh-Hant"))
        #expect(shouldPaste("今天天气很好", "zh-CN"))
    }

    @Test func autoDetectOverThaiTextPastes() {
        #expect(shouldPaste(thaiText, LanguageData.automaticCode))
        #expect(shouldPaste(thaiText, "AUTO"))
        #expect(shouldPaste(thaiText, nil))
        #expect(shouldPaste(thaiText, ""))
    }

    // Behaviour the old literal already had, kept.

    @Test func japaneseChineseKoreanStillPaste() {
        #expect(shouldPaste(japaneseText, "ja"))
        #expect(shouldPaste("今天天气很好", "zh"))
        #expect(shouldPaste("안녕하세요", "ko"))
    }

    @Test func autoDetectOverJapaneseTextPastes() {
        #expect(shouldPaste(japaneseText, LanguageData.automaticCode))
    }

    @Test func spaceDelimitedLanguagesType() {
        #expect(!shouldPaste(englishText, "en"))
        #expect(!shouldPaste("Bonjour tout le monde", "fr"))
        #expect(!shouldPaste(englishText, LanguageData.automaticCode))
        #expect(!shouldPaste(englishText, nil))
    }

    @Test func explicitLanguageWinsOverTextScript() {
        // An explicit space-delimited language types even over Thai text.
        #expect(!shouldPaste(thaiText, "en"))
    }
}
