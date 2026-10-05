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

    // Cases main got WRONG. The old private ["ja", "zh", "ko"] literal was
    // matched against `language?.prefix(2).lowercased()`, so it missed
    // explicit "th", explicit "yue", and auto-detect (nil) over Thai text
    // (`containsCJKCharacters` excludes Thai).

    @Test func thaiLanguagePastes() {
        #expect(shouldPaste(thaiText, "th"))
    }

    @Test func cantoneseLanguagePastes() {
        #expect(shouldPaste("今日天氣好好", "yue"))
    }

    // Production passes nil for auto-detect (RecordingTranscriptionFlow+Streaming
    // maps "auto"/"" to nil before calling typeSegment). nil is the primary
    // assertion. "auto", "AUTO" and "" are accepted defensively by
    // `streamingSegmentShouldPaste` but are not what the caller sends.
    @Test func autoDetectOverThaiTextPastes() {
        #expect(shouldPaste(thaiText, nil))
        #expect(shouldPaste(thaiText, LanguageData.automaticCode))
        #expect(shouldPaste(thaiText, "AUTO"))
        #expect(shouldPaste(thaiText, ""))
    }

    // Behaviour UNCHANGED from main, pinned so it stays that way. The old
    // prefix(2) match already pasted ja, zh, ko, and zh-Hant / zh-CN, and
    // auto-detect (nil) over Japanese already pasted via containsCJKCharacters.

    @Test func autoDetectOverJapaneseTextStillPastes() {
        #expect(shouldPaste(japaneseText, nil))
        #expect(shouldPaste(japaneseText, LanguageData.automaticCode))
        #expect(shouldPaste(japaneseText, ""))
    }

    @Test func japaneseChineseKoreanStillPaste() {
        #expect(shouldPaste(japaneseText, "ja"))
        #expect(shouldPaste("今天天气很好", "zh"))
        #expect(shouldPaste("안녕하세요", "ko"))
    }

    @Test func traditionalChineseSpellingPastes() {
        #expect(shouldPaste("今天天氣很好", "zh-Hant"))
        #expect(shouldPaste("今天天气很好", "zh-CN"))
    }

    // Space-delimited text still types (unchanged from main).

    @Test func spaceDelimitedLanguagesType() {
        #expect(!shouldPaste(englishText, "en"))
        #expect(!shouldPaste("Bonjour tout le monde", "fr"))
        #expect(!shouldPaste(englishText, nil))
        #expect(!shouldPaste(englishText, LanguageData.automaticCode))
    }

    @Test func explicitLanguageWinsOverTextScript() {
        // An explicit space-delimited language types even over Thai text.
        #expect(!shouldPaste(thaiText, "en"))
    }
}
