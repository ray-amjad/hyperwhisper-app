//! Tag scanning and tokenizing.
//!
//! A one-for-one port of `ReleaseNotesHTML.tagEnd` / `endsAttributeValue` /
//! `parseTag` (Swift) and `InlineHtml.FindTagEnd` / `EndsAttributeValue` /
//! `ParseTag` (C#). Every index is a Unicode scalar index into a `&[char]`; see
//! the crate doc comment.
//!
//! Every read goes through `slice::get`, never `slice[i]` — `indexing_slicing`
//! is denied for this crate because the workspace aborts on panic and this input
//! is remote.

/// One tag, tokenized: the element name (lowercased), whether it closes an
/// element, whether it closes itself, and its href if it has one.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Tag {
    pub(crate) name: String,
    pub(crate) is_closing: bool,
    pub(crate) is_self_closing: bool,
    pub(crate) href: Option<String>,
}

/// Trim whitespace off both ends of a scalar slice.
///
/// Swift trims `CharacterSet.whitespacesAndNewlines` and C# `string.Trim()`
/// trims the Unicode White_Space set. `char::is_whitespace` is White_Space, so
/// this follows the C# side; the two sets differ only on characters no test and
/// no feed carries.
fn trim(raw: &[char]) -> &[char] {
    let mut start = 0usize;
    let mut end = raw.len();

    while start < end && raw.get(start).is_some_and(|c| c.is_whitespace()) {
        start += 1;
    }
    while end > start
        && raw
            .get(end.wrapping_sub(1))
            .is_some_and(|c| c.is_whitespace())
    {
        end -= 1;
    }

    raw.get(start..end).unwrap_or(&[])
}

/// Whether a quoted attribute value may end just before `index`: another
/// attribute, the tag's own `/` or `>`, or the end of the fragment follows it.
fn ends_attribute_value(chars: &[char], index: usize) -> bool {
    match chars.get(index) {
        None => true,
        Some(&character) => character == '>' || character == '/' || character.is_whitespace(),
    }
}

/// Index of the `>` that ends the tag opened at `start`, ignoring any `>` inside
/// a quoted attribute value — a URL may carry one in its query. `None` when the
/// tag is never closed. A quote that is never closed falls back to the first
/// `>`, so one malformed attribute cannot swallow the rest of the fragment as
/// markup.
///
/// A quote only opens a value where [`parse_tag`] would read one: straight after
/// an `=`. Anywhere else it is an ordinary character, so the apostrophe in a
/// bare `href=it's` cannot pair up with a later one and run the scan past the
/// `>` that really ends the tag. The closing quote has to end a value too, so a
/// value left open in its own tag cannot pair up with the quote of a later one
/// either.
pub(crate) fn tag_end(chars: &[char], start: usize) -> Option<usize> {
    let mut index = start.saturating_add(1);
    let mut in_value_position = false;

    while let Some(&character) = chars.get(index) {
        if in_value_position && (character == '"' || character == '\'') {
            let rest = chars.get(index.saturating_add(1)..).unwrap_or(&[]);
            let Some(offset) = rest.iter().position(|&c| c == character) else {
                break;
            };
            let quoted_end = index.saturating_add(1).saturating_add(offset);

            if !ends_attribute_value(chars, quoted_end.saturating_add(1)) {
                break;
            }

            index = quoted_end.saturating_add(1);
            in_value_position = false;
            continue;
        }

        if character == '>' {
            return Some(index);
        }

        // Whitespace between `=` and the value is allowed, so it leaves the
        // position alone rather than ending it.
        if !character.is_whitespace() {
            in_value_position = character == '=';
        }
        index = index.saturating_add(1);
    }

    chars
        .get(start..)
        .and_then(|tail| tail.iter().position(|&c| c == '>'))
        .map(|offset| start.saturating_add(offset))
}

/// Walk a tag's body once — a leading `/`, the element name, then attribute by
/// attribute — and report everything the caller needs to know about it. The
/// first href wins; its value keeps its own case.
///
/// The tag is walked rather than searched, so a value that happens to contain
/// `href=` — a title, say — can never be mistaken for the attribute itself, and
/// a bare value that ends in `/` — most URLs — is not mistaken for a
/// self-closing tag. Only a `/` standing where a name may start, with nothing
/// but whitespace after it, closes the tag. An unterminated quote gives up on
/// the rest of the tag instead of inventing a value out of it.
pub(crate) fn parse_tag(raw: &[char]) -> Tag {
    let body = trim(raw);
    let mut index = 0usize;

    let is_closing = body.first() == Some(&'/');
    if is_closing {
        index = 1;
    }

    let mut name = String::new();
    let mut have_name = false;
    let mut is_self_closing = false;
    let mut href: Option<String> = None;

    while index < body.len() {
        while body.get(index).is_some_and(|c| c.is_whitespace()) {
            index += 1;
        }
        if index >= body.len() {
            break;
        }

        // A `/` where a name could start is the tag closing itself — `<a/>`,
        // `<br />`, `<a href=… />` — but only if nothing but whitespace follows
        // it, so the next token read clears this again. A `/` inside a bare
        // value, on the other hand, is simply part of the URL.
        if body.get(index) == Some(&'/') {
            is_self_closing = true;
            index += 1;
            continue;
        }

        is_self_closing = false;

        let token_start = index;
        while body
            .get(index)
            .is_some_and(|&c| !c.is_whitespace() && c != '=' && c != '/')
        {
            index += 1;
        }
        let token_end = index;

        // The first token is the element name; every token after it is an
        // attribute, valued or not. The name is taken before its own value is
        // read, so a malformed one — `<br = >` — gives up on the rest of the tag
        // without taking the element with it.
        let is_name = !have_name;
        if is_name {
            name = body
                .get(token_start..token_end)
                .unwrap_or(&[])
                .iter()
                .flat_map(|c| c.to_lowercase())
                .collect();
            have_name = true;
        }

        while body.get(index).is_some_and(|c| c.is_whitespace()) {
            index += 1;
        }

        let mut value: Option<String> = None;
        if body.get(index) == Some(&'=') {
            index += 1;
            while body.get(index).is_some_and(|c| c.is_whitespace()) {
                index += 1;
            }
            if index >= body.len() {
                break; // `href=` with nothing after it
            }

            let quote = body.get(index).copied().unwrap_or('\0');
            if quote == '"' || quote == '\'' {
                let rest = body.get(index.saturating_add(1)..).unwrap_or(&[]);
                let Some(offset) = rest.iter().position(|&c| c == quote) else {
                    break; // unterminated: nothing here is trustworthy
                };
                let quoted_end = index.saturating_add(1).saturating_add(offset);
                value = Some(
                    body.get(index.saturating_add(1)..quoted_end)
                        .unwrap_or(&[])
                        .iter()
                        .collect(),
                );
                index = quoted_end.saturating_add(1);
            } else {
                let value_start = index;
                while body.get(index).is_some_and(|c| !c.is_whitespace()) {
                    index += 1;
                }
                value = Some(body.get(value_start..index).unwrap_or(&[]).iter().collect());
            }
        }

        if !is_name && href.is_none() {
            let token: String = body
                .get(token_start..token_end)
                .unwrap_or(&[])
                .iter()
                .collect();
            if token.eq_ignore_ascii_case("href") {
                href = value;
            }
        }
    }

    Tag {
        name,
        is_closing,
        is_self_closing,
        href,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tag(raw: &str) -> Tag {
        let chars: Vec<char> = raw.chars().collect();
        parse_tag(&chars)
    }

    fn end(html: &str, start: usize) -> Option<usize> {
        let chars: Vec<char> = html.chars().collect();
        tag_end(&chars, start)
    }

    #[test]
    fn a_plain_tag_gives_its_name() {
        assert_eq!(tag("b").name, "b");
        assert_eq!(tag("/b").name, "b");
        assert!(tag("/b").is_closing);
        assert_eq!(tag("BR").name, "br");
        assert!(!tag("b").is_self_closing);
    }

    #[test]
    fn href_is_read_whatever_its_quoting_and_case() {
        assert_eq!(
            tag("A HREF='https://example.com/a' class=\"x\"")
                .href
                .as_deref(),
            Some("https://example.com/a")
        );
        assert_eq!(
            tag("a class=\"x\" href=https://example.com/a")
                .href
                .as_deref(),
            Some("https://example.com/a")
        );
        assert_eq!(
            tag("a href = \"https://example.com/a\"").href.as_deref(),
            Some("https://example.com/a")
        );
    }

    #[test]
    fn an_href_inside_another_attributes_value_does_not_win() {
        assert_eq!(
            tag("a title=\"see href=http://evil.example more\" href=\"https://real.example\"")
                .href
                .as_deref(),
            Some("https://real.example")
        );
        assert_eq!(
            tag("a data-href=\"https://evil.example\" href=\"https://real.example\"")
                .href
                .as_deref(),
            Some("https://real.example")
        );
    }

    #[test]
    fn a_slash_ending_a_bare_href_belongs_to_the_url_not_the_tag() {
        let bare = tag("a href=https://example.com/");
        assert_eq!(bare.href.as_deref(), Some("https://example.com/"));
        assert!(!bare.is_self_closing);

        let closed = tag("a href=https://example.com/ /");
        assert_eq!(closed.href.as_deref(), Some("https://example.com/"));
        assert!(closed.is_self_closing);

        let leading = tag("a / href=https://example.com/");
        assert_eq!(leading.href.as_deref(), Some("https://example.com/"));
        assert!(!leading.is_self_closing);
    }

    #[test]
    fn a_malformed_value_on_the_first_token_keeps_the_element_name() {
        assert_eq!(tag("br = ").name, "br");
        assert_eq!(tag("br = \"unterminated").name, "br");
    }

    #[test]
    fn an_unterminated_quote_yields_no_href() {
        assert_eq!(tag("a href=\"https://example.com").href, None);
        assert_eq!(tag("a href=").href, None);
    }

    #[test]
    fn a_closing_tag_can_also_close_itself() {
        let both = tag("/a/");
        assert!(both.is_closing);
        assert!(both.is_self_closing);
        assert_eq!(both.name, "a");
    }

    #[test]
    fn a_quoted_angle_bracket_does_not_end_the_tag() {
        // `<a href="https://ex.com/?q=a>b" title="t">` — the `>` at offset 27
        // is inside the value; the tag really ends at offset 38.
        let html = "<a href=\"https://ex.com/?q=a>b\" title=\"t\">here";
        assert_eq!(end(html, 0), Some(41));
    }

    #[test]
    fn an_unclosed_value_falls_back_to_the_first_angle_bracket() {
        let html = "<a href=\"https://ex.com/latency>the page</a> for <b class=\"hl\">x</b>";
        assert_eq!(end(html, 0), Some(31));
    }

    #[test]
    fn an_apostrophe_in_a_bare_value_is_an_ordinary_character() {
        let html = "<a href=it's>label</a>";
        assert_eq!(end(html, 0), Some(12));
    }

    #[test]
    fn a_tag_that_is_never_closed_has_no_end() {
        assert_eq!(end("2 < 3 and counting", 2), None);
        assert_eq!(end("<b", 0), None);
    }
}
