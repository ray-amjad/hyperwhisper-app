import AppKit

/// Bounds free user text before it becomes a menu item title (issue #1436).
///
/// A mode name has no length cap: not in the editor, not in Core Data, not in a
/// restored backup. A SwiftUI `Menu` inside the menu bar extra, a context menu or
/// a `.menu` picker becomes an `NSMenu`, and an `NSMenu` sizes itself to its widest
/// item. It does not ellipsise until it hits the screen edge, so a 200-character
/// name opened the Select Mode and Transcribe File submenus about 1,150 pt wide.
/// `.lineLimit`, `.truncationMode` and `.frame` have no effect there: the only
/// place to bound it is the string handed to the item.
///
/// This is the macOS twin of `MenuItemText` on Windows (#557). The bound is on
/// rendered width in the menu font, not a character count, because sixty CJK
/// glyphs are about twice as wide as sixty Latin ones. `maxCharacters` is a second
/// ceiling, so the answer stays bounded however the text measures. Only the
/// display is cut: callers keep the full name for everything else.
enum MenuItemText {
    /// The widest a menu label may render, in points: roughly fifty Latin
    /// characters at the menu font, far longer than any real mode name.
    static let maxWidth: CGFloat = 360

    /// A cheap second ceiling on the label, in characters (grapheme clusters).
    static let maxCharacters = 80

    static let ellipsis = "…"

    /// `text` unchanged when it fits, else its longest prefix that fits with "…"
    /// appended. Trailing whitespace before the ellipsis is dropped.
    static func bounded(
        _ text: String,
        maxWidth: CGFloat = maxWidth,
        maxCharacters: Int = maxCharacters,
        font: NSFont = NSFont.menuFont(ofSize: 0)
    ) -> String {
        let attributes: [NSAttributedString.Key: Any] = [.font: font]
        func width(_ s: String) -> CGFloat {
            (s as NSString).size(withAttributes: attributes).width
        }

        let characters = Array(text)
        if characters.count <= maxCharacters && width(text) <= maxWidth {
            return text
        }

        // Binary search for the longest prefix whose label (prefix + "…") fits.
        var low = 0
        var high = min(characters.count, maxCharacters - 1)
        while low < high {
            let mid = (low + high + 1) / 2
            if width(label(characters, mid)) <= maxWidth {
                low = mid
            } else {
                high = mid - 1
            }
        }
        return label(characters, low)
    }

    private static func label(_ characters: [Character], _ count: Int) -> String {
        var prefix = String(characters.prefix(count))
        while let last = prefix.last, last.isWhitespace {
            prefix.removeLast()
        }
        return prefix + ellipsis
    }
}
