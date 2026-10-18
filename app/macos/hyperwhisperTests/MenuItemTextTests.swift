//
//  MenuItemTextTests.swift
//  hyperwhisperTests
//
//  Pins the bound on a mode name shown as a menu item (#1436). An NSMenu grows
//  to its widest item, so a 200-character mode name opened the Select Mode and
//  Transcribe File submenus about 1,150 pt wide.
//

import AppKit
import Testing
@testable import HyperWhisper

struct MenuItemTextTests {

    private static let font = NSFont.menuFont(ofSize: 0)

    private static func width(_ s: String) -> CGFloat {
        (s as NSString).size(withAttributes: [.font: font]).width
    }

    @Test func shortNameIsUnchanged() {
        #expect(MenuItemText.bounded("Email") == "Email")
        #expect(MenuItemText.bounded("") == "")
    }

    @Test func longLatinNameIsCutToTheMenuWidth() {
        let name = String(repeating: "Long name for a mode that goes on and on ", count: 5)
        let label = MenuItemText.bounded(name)

        #expect(label.hasSuffix(MenuItemText.ellipsis))
        #expect(Self.width(label) <= MenuItemText.maxWidth)
        #expect(label.count <= MenuItemText.maxCharacters)
        // The label is a prefix of the name, not a rewrite of it.
        #expect(name.hasPrefix(String(label.dropLast())))
        // No space left dangling before the ellipsis.
        #expect(label.dropLast().last?.isWhitespace == false)
    }

    @Test func wideGlyphsAreBoundByWidthNotCount() {
        // 60 CJK characters: under the character ceiling, over the width.
        let name = String(repeating: "音声入力", count: 15)
        let label = MenuItemText.bounded(name)

        #expect(name.count < MenuItemText.maxCharacters)
        #expect(label.hasSuffix(MenuItemText.ellipsis))
        #expect(Self.width(label) <= MenuItemText.maxWidth)
    }

    @Test func narrowGlyphsAreBoundByCharacterCount() {
        // 200 "i"s measure under the width bound but pass the character ceiling.
        let name = String(repeating: "i", count: 200)
        let label = MenuItemText.bounded(name, maxWidth: 10_000)

        #expect(label.count == MenuItemText.maxCharacters)
        #expect(label.hasSuffix(MenuItemText.ellipsis))
    }

    @Test func emojiIsNeverSplit() {
        let name = String(repeating: "👩‍👩‍👧‍👦", count: 100)
        let label = MenuItemText.bounded(name)

        #expect(label.dropLast().allSatisfy { $0 == "👩‍👩‍👧‍👦" })
    }
}
