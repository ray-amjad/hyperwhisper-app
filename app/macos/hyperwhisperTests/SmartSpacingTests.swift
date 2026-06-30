//
//  SmartSpacingTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct SmartSpacingTests {

    @Test func autoDetectTreatsExtendedCJKAndFullwidthFormsAsNoSpaceText() {
        let noSpaceTexts = [
            "𠀋",      // CJK Unified Ideographs Extension B
            "𫠠",      // CJK Unified Ideographs Extension D
            "豈",       // CJK Compatibility Ideograph
            "ＡＢＣ",   // Fullwidth Latin letters
            "１２３"    // Fullwidth digits
        ]

        for text in noSpaceTexts {
            #expect(SmartSpacing.containsCJKCharacters(text), "\(text) should be detected as CJK/no-space text")
            #expect(SmartSpacing.appendTrailingSpace(text, modeLanguage: LanguageData.automaticCode) == text)
        }
    }

    @Test func autoDetectKeepsSpaceDelimitedTextBehavior() {
        #expect(!SmartSpacing.containsCJKCharacters("Hello world"))
        #expect(SmartSpacing.appendTrailingSpace("Hello world", modeLanguage: LanguageData.automaticCode) == "Hello world ")
    }

    @Test func fullwidthPunctuationAloneDoesNotForceCJKDetection() {
        #expect(!SmartSpacing.containsCJKCharacters("！？，"))
    }
}
