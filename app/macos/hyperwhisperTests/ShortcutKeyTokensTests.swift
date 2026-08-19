//
//  ShortcutKeyTokensTests.swift
//  hyperwhisperTests
//
//  Onboarding and the home badge render the same shortcut description very
//  differently, so the shared tokenizer is asserted on tokens, not on labels.
//

import Testing
@testable import HyperWhisper

struct ShortcutKeyTokensTests {

    @Test func modifierGlyphsKeepTheirInputOrderAheadOfThePrimaryKey() {
        #expect(ShortcutKeyTokens.tokenize("⌥Space") == [.option, .space])
        #expect(ShortcutKeyTokens.tokenize("⌃⌘X") == [.control, .command, .key("X")])
        #expect(ShortcutKeyTokens.tokenize("⇪A") == [.capsLock, .key("A")])
    }

    @Test func functionKeysSurviveWholeRatherThanCollapsingToTheirDigit() {
        #expect(ShortcutKeyTokens.tokenize("F5") == [.key("F5")])
        #expect(ShortcutKeyTokens.tokenize("⇧⌘F12") == [.shift, .command, .key("F12")])
    }

    @Test func namedPrimariesResolveFromGlyphsAndFromWords() {
        #expect(ShortcutKeyTokens.tokenize("⎋") == [.escape])
        #expect(ShortcutKeyTokens.tokenize("Escape") == [.escape])
        #expect(ShortcutKeyTokens.tokenize("↩") == [.return])
        #expect(ShortcutKeyTokens.tokenize("↩︎") == [.return])
        #expect(ShortcutKeyTokens.tokenize("Return") == [.return])
        #expect(ShortcutKeyTokens.tokenize("⌘ ") == [.command, .space])
    }

    @Test func emptyAndModifierOnlyInputProduceNoBlankToken() {
        #expect(ShortcutKeyTokens.tokenize("") == [])
        #expect(ShortcutKeyTokens.tokenize("⌘⇧") == [.command, .shift])
    }
}
