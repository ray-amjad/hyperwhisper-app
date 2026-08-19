//
//  ShortcutKeyTokens.swift
//  hyperwhisper
//
//  Single tokenizer for `KeyboardShortcuts` descriptions such as "⌥Space".
//  Callers decide how a token is drawn (onboarding spells modifiers out as
//  words, the home screen draws glyphs), so this stays semantic and
//  Foundation-only to keep it testable without a view host.
//

import Foundation

/// One cap in a rendered shortcut. Modifiers come first in the order they
/// appear in the description, followed by at most one primary key.
enum ShortcutKeyToken: Equatable {
    case command
    case option
    case control
    case shift
    case capsLock
    case escape
    case `return`
    case space
    case key(String)
}

enum ShortcutKeyTokens {

    /// Split a shortcut description into modifiers plus a single primary key.
    /// An empty or modifier-only description yields no primary token rather
    /// than an empty-string one.
    static func tokenize(_ description: String) -> [ShortcutKeyToken] {
        var tokens: [ShortcutKeyToken] = []
        var remainder = ""

        for character in description {
            switch character {
            case "⌘": tokens.append(.command)
            case "⌥": tokens.append(.option)
            case "⌃": tokens.append(.control)
            case "⇧": tokens.append(.shift)
            case "⇪": tokens.append(.capsLock)
            default: remainder.append(character)
            }
        }

        let primary = remainder.trimmingCharacters(in: .whitespacesAndNewlines)
        if primary.isEmpty {
            // A remainder made only of whitespace is the space bar itself.
            if !remainder.isEmpty {
                tokens.append(.space)
            }
            return tokens
        }

        tokens.append(primaryToken(for: primary))
        return tokens
    }

    private static func primaryToken(for key: String) -> ShortcutKeyToken {
        switch key {
        case "⎋": return .escape
        case "↩", "↩︎": return .return
        default: break
        }

        switch key.lowercased() {
        case "escape": return .escape
        case "return": return .return
        case "space": return .space
        default: return .key(key)
        }
    }
}
