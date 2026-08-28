//! Character-entity decoding for release-notes HTML.
//!
//! Ported from the two native decoders it replaces:
//! `ReleaseNotesHTML.decodeEntity` (Swift) and `InlineHtml.DecodeEntity` (C#).
//!
//! # Decision (a): numeric entities are STRICT here
//!
//! The two natives disagreed, each in one direction, and each by accident:
//!
//! | input      | macOS today                            | Windows today                            | here      |
//! |------------|----------------------------------------|------------------------------------------|-----------|
//! | `&#+65;`   | `A` — `UInt32(_, radix: 10)` takes `+` | literal — `NumberStyles.None`            | literal   |
//! | `&#x 41;`  | literal                                | `A` — `NumberStyles.HexNumber` takes WS  | literal   |
//!
//! Neither was pinned by a test on either head, neither is in any real feed, and
//! this is remote input. So both go strict: the body after `#` (and after an
//! `x`/`X`) must be nothing but digits of the radix. That rejects a leading `+`,
//! a leading `-`, leading or trailing whitespace, and any separator.

/// Longest entity we will look ahead for, **counted in Unicode scalar values**
/// and including the `&` and the `;`. See the crate doc comment for why the unit
/// is pinned to scalars rather than graphemes (macOS today) or UTF-16 code units
/// (Windows today).
pub(crate) const ENTITY_SCAN_LIMIT: usize = 12;

/// The nine named entities both feeds use. `nbsp` is U+00A0 on both natives
/// (verified byte-for-byte), and U+00A0 is deliberately *not* collapsible
/// whitespace — see [`crate::inline`].
const NAMED_ENTITIES: [(&str, &str); 9] = [
    ("amp", "&"),
    ("lt", "<"),
    ("gt", ">"),
    ("quot", "\""),
    ("apos", "'"),
    ("nbsp", "\u{00A0}"),
    ("mdash", "\u{2014}"),
    ("ndash", "\u{2013}"),
    ("hellip", "\u{2026}"),
];

/// Decode the body of an entity (`amp`, `#8212`, `#x2014`).
///
/// `None` for anything unrecognised, so the `&` stays literal text.
pub(crate) fn decode_entity_body(body: &[char]) -> Option<String> {
    // Named lookup is case-insensitive on both natives (Swift lowercases the
    // body into a lowercase-keyed table; C# uses an OrdinalIgnoreCase
    // dictionary). Every key is ASCII, so the two agree.
    let lowered: String = body.iter().flat_map(|c| c.to_lowercase()).collect();
    if let Some((_, replacement)) = NAMED_ENTITIES.iter().find(|(name, _)| *name == lowered) {
        return Some((*replacement).to_string());
    }

    let (first, digits) = body.split_first()?;
    if *first != '#' {
        return None;
    }

    let (radix, digits) = match digits.split_first() {
        Some((&('x' | 'X'), rest)) => (16u32, rest),
        _ => (10u32, digits),
    };

    if digits.is_empty() {
        return None;
    }

    // Decision (a). `char::to_digit` is ASCII-only, so this also rejects
    // non-ASCII digits, which neither native accepted either.
    let mut value: u32 = 0;
    for digit in digits {
        let parsed = digit.to_digit(radix)?;
        value = value.checked_mul(radix)?.checked_add(parsed)?;
    }

    if value > 0x0010_FFFF {
        return None;
    }

    // Rejects the surrogate range too, matching `Unicode.Scalar(_:)` on macOS
    // and the `ArgumentOutOfRangeException` catch around `char.ConvertFromUtf32`
    // on Windows.
    char::from_u32(value).map(|c| c.to_string())
}

/// Decode the entity starting at `start` (which the caller has checked is an
/// `&`), and report the scalar index just past its `;`.
///
/// `None` when there is no complete, recognised entity there.
pub(crate) fn decode_entity_at(chars: &[char], start: usize) -> Option<(String, usize)> {
    let limit = chars.len().min(start.saturating_add(ENTITY_SCAN_LIMIT));
    let window = chars.get(start..limit)?;

    let offset = window.iter().position(|&c| c == ';')?;
    if offset == 0 {
        // The `;` would be the `&` itself. Mirrors C#'s `semicolon <= start`.
        return None;
    }

    let semicolon = start.checked_add(offset)?;
    let body = chars.get(start.checked_add(1)?..semicolon)?;
    let decoded = decode_entity_body(body)?;

    Some((decoded, semicolon.checked_add(1)?))
}

/// Decode every character entity in a fragment, leaving all other text — markup
/// included — exactly as it was.
///
/// Used for `href` values only. Nothing else about an href may change: running
/// it through the whole parser stripped markup and collapsed whitespace inside
/// it, quietly opening a different address than the feed asked for.
pub(crate) fn decode_entities(text: &str) -> String {
    if !text.contains('&') {
        return text.to_string();
    }

    let chars: Vec<char> = text.chars().collect();
    let mut result = String::with_capacity(text.len());
    let mut index = 0usize;

    while let Some(&character) = chars.get(index) {
        if character == '&' {
            if let Some((decoded, end)) = decode_entity_at(&chars, index) {
                result.push_str(&decoded);
                index = end;
                continue;
            }
        }

        result.push(character);
        index += 1;
    }

    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn decode(body: &str) -> Option<String> {
        let chars: Vec<char> = body.chars().collect();
        decode_entity_body(&chars)
    }

    #[test]
    fn named_entities_decode_whatever_their_case() {
        assert_eq!(decode("amp").as_deref(), Some("&"));
        assert_eq!(decode("AMP").as_deref(), Some("&"));
        assert_eq!(decode("NbSp").as_deref(), Some("\u{00A0}"));
        assert_eq!(decode("hellip").as_deref(), Some("\u{2026}"));
        assert_eq!(decode("bogus"), None);
    }

    #[test]
    fn numeric_entities_decode_in_both_radices() {
        assert_eq!(decode("#8212").as_deref(), Some("\u{2014}"));
        assert_eq!(decode("#x2014").as_deref(), Some("\u{2014}"));
        assert_eq!(decode("#X2014").as_deref(), Some("\u{2014}"));
        assert_eq!(decode("#38").as_deref(), Some("&"));
    }

    /// Decision (a), the macOS-only accident: `UInt32(_, radix: 10)` accepts a
    /// leading `+`, so `&#+65;` decoded to "A" there and stayed literal on
    /// Windows. Neither head pinned it. It is rejected now.
    #[test]
    fn a_leading_plus_no_longer_decodes() {
        assert_eq!(decode("#+65"), None);
        assert_eq!(decode("#x+41"), None);
        assert_eq!(decode("#-65"), None);
    }

    /// Decision (a), the Windows-only accident: `NumberStyles.HexNumber` allows
    /// leading white, so `&#x 41;` decoded to "A" there and stayed literal on
    /// macOS. Neither head pinned it. It is rejected now.
    #[test]
    fn leading_whitespace_no_longer_decodes() {
        assert_eq!(decode("#x 41"), None);
        assert_eq!(decode("# 65"), None);
        assert_eq!(decode("#65 "), None);
        assert_eq!(decode("#x\u{00A0}41"), None);
    }

    #[test]
    fn out_of_range_and_surrogate_code_points_stay_literal() {
        assert_eq!(decode("#x110000"), None);
        assert_eq!(decode("#1114112"), None);
        assert_eq!(decode("#xD800"), None);
        assert_eq!(decode("#xDFFF"), None);
        assert_eq!(decode("#x10FFFF").as_deref(), Some("\u{10FFFF}"));
        // Overflow must not panic — the workspace builds with panic = "abort".
        assert_eq!(decode("#xFFFFFFFFFFFF"), None);
        assert_eq!(decode("#99999999999999999999"), None);
    }

    #[test]
    fn a_malformed_body_stays_literal() {
        assert_eq!(decode(""), None);
        assert_eq!(decode("#"), None);
        assert_eq!(decode("#x"), None);
        assert_eq!(decode("#12a4"), None);
        assert_eq!(decode("#xZZ"), None);
        // Non-ASCII digits are not digits, on any of the three heads.
        assert_eq!(decode("#\u{0663}"), None);
    }

    /// The scan limit is 12 scalars counted from the `&` inclusive, so the
    /// longest decodable numeric entity is `&#x0010FFFF;` at exactly 12.
    #[test]
    fn the_scan_limit_is_twelve_scalars_from_the_ampersand() {
        let chars: Vec<char> = "&#x0010FFFF;".chars().collect();
        assert_eq!(chars.len(), ENTITY_SCAN_LIMIT);
        assert_eq!(
            decode_entity_at(&chars, 0),
            Some(("\u{10FFFF}".to_string(), 12))
        );

        // One scalar longer, and the `;` falls outside the window.
        let too_long: Vec<char> = "&#x00010FFFF;".chars().collect();
        assert_eq!(decode_entity_at(&too_long, 0), None);
    }

    /// The window counts SCALARS, not bytes: a multi-byte scalar inside the
    /// window must not shorten it. (macOS counts graphemes today, Windows
    /// UTF-16 units — no test on either head pinned a non-ASCII scan window.)
    #[test]
    fn the_scan_window_counts_scalars_not_bytes() {
        // 11 scalars, but 20 UTF-8 bytes.
        let chars: Vec<char> = "&\u{2014}\u{2014}\u{2014}\u{2014}\u{2014};"
            .chars()
            .collect();
        assert_eq!(chars.len(), 7);
        // Unrecognised body, so still literal — but the `;` WAS found.
        assert_eq!(decode_entity_at(&chars, 0), None);

        let named: Vec<char> = "&hellip;".chars().collect();
        assert_eq!(
            decode_entity_at(&named, 0),
            Some(("\u{2026}".to_string(), 8))
        );
    }

    #[test]
    fn decode_entities_leaves_markup_alone() {
        assert_eq!(
            decode_entities("https://ex.com/p?a=1&amp;b=2"),
            "https://ex.com/p?a=1&b=2"
        );
        assert_eq!(
            decode_entities("https://ex.com/p?a=1&#38;b=2"),
            "https://ex.com/p?a=1&b=2"
        );
        assert_eq!(
            decode_entities("https://ex.com/?q=<b>x</b>"),
            "https://ex.com/?q=<b>x</b>"
        );
        assert_eq!(decode_entities("no ampersand here"), "no ampersand here");
        assert_eq!(decode_entities("&bogus; stays"), "&bogus; stays");
    }
}
