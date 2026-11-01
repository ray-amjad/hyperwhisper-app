//! Regression tests for #2941: removing a filler word next to a line break must
//! keep the line break. The boundary groups of the filler regex match `\s`,
//! which includes `\n`, and the replacement used to write a single space, so a
//! paragraph break after a filler was merged into one line.

use hw_text::remove_filler_words;

fn en(text: &str) -> String {
    remove_filler_words(text, Some("en"))
}

/// The issue's repro: a filler at the end of a paragraph.
#[test]
fn filler_removal_keeps_a_paragraph_break() {
    assert_eq!(
        en("First point um\n\nSecond point"),
        "First point\n\nSecond point"
    );
}

#[test]
fn filler_at_the_start_of_a_paragraph_keeps_the_break() {
    assert_eq!(
        en("First point\n\num second point"),
        "First point\n\nsecond point"
    );
}

#[test]
fn filler_alone_on_its_own_line_leaves_one_line_break() {
    assert_eq!(en("a\num\nb"), "a\nb");
}

#[test]
fn filler_alone_between_paragraph_breaks_leaves_one_paragraph_break() {
    assert_eq!(en("a\n\num\n\nb"), "a\n\nb");
    // The larger break wins, whichever side it is on.
    assert_eq!(en("a\num\n\nb"), "a\n\nb");
    assert_eq!(en("a\n\num\nb"), "a\n\nb");
}

#[test]
fn adjacent_fillers_across_a_break() {
    assert_eq!(en("a um\n\num b"), "a\n\nb");
    assert_eq!(en("a uh um\n\num er b"), "a\n\nb");
}

#[test]
fn filler_with_a_trailing_comma_before_a_break() {
    assert_eq!(en("a um,\n\nb"), "a\n\nb");
}

#[test]
fn crlf_line_breaks_are_kept_whole() {
    assert_eq!(
        en("First point um\r\n\r\nSecond point"),
        "First point\r\n\r\nSecond point"
    );
    assert_eq!(en("a\r\num\r\nb"), "a\r\nb");
    assert_eq!(en("a\r\n\r\num\r\n\r\nb"), "a\r\n\r\nb");
}

#[test]
fn horizontal_whitespace_around_the_break_is_dropped_with_the_filler() {
    assert_eq!(en("a  um \n\nb"), "a\n\nb");
}

#[test]
fn filler_at_the_text_edges_next_to_a_break() {
    // Leading filler: everything up to the first word goes (unchanged).
    assert_eq!(en("um\n\nhello there"), "Hello there");
    // Trailing filler: the dangling break before it goes too.
    assert_eq!(en("a\num"), "a");
    assert_eq!(en("a\r\num"), "a");
    // Trailing break after a final filler is kept as a break.
    assert_eq!(en("a um\n"), "a\n");
}

#[test]
fn horizontal_behaviour_is_unchanged() {
    assert_eq!(
        en("so uh I think we should um go"),
        "so I think we should go"
    );
    assert_eq!(en("I think, uh, we should go"), "I think, we should go");
    // The extra space of a double-spaced run before a filler is preserved,
    // exactly as before #2941.
    assert_eq!(en("a  um b"), "a  b");
    assert_eq!(en("a  um"), "a ");
    assert_eq!(en("  um b"), " b");
    // A line with no filler is untouched.
    assert_eq!(en("one\n\ntwo"), "one\n\ntwo");
}
