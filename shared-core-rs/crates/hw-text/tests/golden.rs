//! Golden parity tests for hw-text. Values are the unified cross-platform
//! behaviour captured from the current macOS/Windows implementations (and, for
//! filler words, the existing macOS `TranscriptionFillerWordTests`). Where the
//! two platforms diverged, the comment records which behaviour was adopted.

use hw_text::*;

// ---- smart spacing / CJK -------------------------------------------------

#[test]
fn cjk_detection() {
    assert!(!contains_cjk("Hello world."));
    assert!(contains_cjk("今日はいい天気ですね。"));
    assert!(contains_cjk("これはtestです")); // mixed, still >30% CJK
    assert!(!contains_cjk(""));
    assert!(!contains_cjk("   ...   ")); // only ws + punctuation
}

#[test]
fn trailing_space() {
    assert_eq!(append_trailing_space("Hello world.", "en"), "Hello world. ");
    assert_eq!(append_trailing_space("今日はいい天気ですね。", "ja"), "今日はいい天気ですね。");
    assert_eq!(append_trailing_space("今日はいい天気ですね。", "auto"), "今日はいい天気ですね。");
    assert_eq!(append_trailing_space("Hello world.", "auto"), "Hello world. ");
    assert_eq!(append_trailing_space("Hello world. ", "en"), "Hello world. "); // already spaced
    assert_eq!(append_trailing_space("", "en"), "");
    assert_eq!(append_trailing_space("text", "zh-CN"), "text"); // prefix match zh
}

// ---- autocapitalize (unified on macOS: pronoun + acronym guards) ----------

#[test]
fn autocapitalize_midsentence() {
    use CursorContext::*;
    assert_eq!(apply_autocapitalize("Hello", MidSentence), "hello");
    assert_eq!(apply_autocapitalize("API documentation", MidSentence), "API documentation"); // acronym
    assert_eq!(apply_autocapitalize("I think", MidSentence), "I think"); // pronoun (Windows gains this)
    assert_eq!(apply_autocapitalize("I'm happy", MidSentence), "I'm happy");
    assert_eq!(apply_autocapitalize("I\u{2019}m happy", MidSentence), "I\u{2019}m happy"); // curly apostrophe
    assert_eq!(apply_autocapitalize("I'll go", MidSentence), "I'll go");
    assert_eq!(apply_autocapitalize("Hello", StartOfSentence), "Hello"); // not mid-sentence
    assert_eq!(apply_autocapitalize("Hello", Unknown), "Hello");
    assert_eq!(apply_autocapitalize("already lower", MidSentence), "already lower");
}

// ---- vocab replacement (unified on macOS: literal replacement) ------------

#[test]
fn hardened_replacement() {
    assert_eq!(apply_hardened_replacement("Hello world", "world", "universe"), "Hello universe");
    // word boundary: substrings untouched
    assert_eq!(apply_hardened_replacement("category categorize", "cat", "feline"), "category categorize");
    // case-insensitive, all occurrences
    assert_eq!(apply_hardened_replacement("Hello HELLO hello", "hello", "goodbye"), "goodbye goodbye goodbye");
    // dollar sign in replacement stays literal (Windows $-backreference bug fixed)
    assert_eq!(apply_hardened_replacement("The price is $5", "price", "$5 value"), "The $5 value is $5");
    // only whole word, trailing punctuation preserved
    assert_eq!(apply_hardened_replacement("I like cats and a cat.", "cat", "feline"), "I like cats and a feline.");
    // \b fails after "++" (both platforms) -> no replacement
    assert_eq!(apply_hardened_replacement("Use C++ or C#", "C++", "C Plus Plus"), "Use C++ or C#");
    // empties are no-ops
    assert_eq!(apply_hardened_replacement("test text", "  ", "replace"), "test text");
    assert_eq!(apply_hardened_replacement("test text", "test", "  "), "test text");
}

// ---- text processing ------------------------------------------------------

#[test]
fn extract_cleaned() {
    assert_eq!(extract_cleaned_from_wrapped("<<CLEANED>>Hello<<END>>"), "Hello");
    assert_eq!(extract_cleaned_from_wrapped("<<CLEANED>>Hello world"), "Hello world");
    assert_eq!(extract_cleaned_from_wrapped("<<CLEANED>>\n  Hello  \n<<END>>"), "Hello");
    assert_eq!(extract_cleaned_from_wrapped("  <<CLEANED>>Hello<<END>>  "), "Hello");
    // strict (Windows) behaviour: no start marker -> empty (don't leak prompt)
    assert_eq!(extract_cleaned_from_wrapped("Hello world"), "");
}

#[test]
fn strip_wrapper_markers_lenient() {
    // wrapped -> extract
    assert_eq!(strip_wrapper_markers("<<CLEANED>>Hello<<END>>"), "Hello");
    // NOT wrapped -> passthrough (the routed/raw-transcript case strict would break)
    assert_eq!(strip_wrapper_markers("Hello world"), "Hello world");
    // stray end tag stripped on passthrough
    assert_eq!(strip_wrapper_markers("Hello world<<END>>"), "Hello world");
}

#[test]
fn strip_wrapper_markers_rejects_leaked_prompt() {
    // GOLDEN (F2): an unwrapped response that echoes our prompt scaffolding must
    // NOT pass through (it would leak the system prompt / app + screen context).
    // Empty return → the caller keeps the original transcript.
    assert_eq!(
        strip_wrapper_markers("<APPLICATION_CONTEXT>\n<APP>Mail</APP>\n</APPLICATION_CONTEXT>"),
        ""
    );
    assert_eq!(
        strip_wrapper_markers("ignore prior text\n<SCREEN_CONTEXT>\nsecret\n</SCREEN_CONTEXT>"),
        ""
    );
    assert_eq!(strip_wrapper_markers("<MODE_FLAGS>\n</MODE_FLAGS>"), "");
    // A clean unwrapped paraphrase (no prompt tags) is still returned as-is.
    assert_eq!(
        strip_wrapper_markers("Here is the cleaned sentence."),
        "Here is the cleaned sentence."
    );
}

#[test]
fn sanitize_streaming() {
    assert_eq!(sanitize_streaming_buffer("Preamble <<CLEANED>>Hello"), "Hello");
    assert_eq!(sanitize_streaming_buffer("<<CLEANED>>Hello<<END>>"), "Hello");
    assert_eq!(sanitize_streaming_buffer("<<CLEANED>>"), "");
}

#[test]
fn trailing_period() {
    assert_eq!(remove_trailing_period("Hello."), "Hello");
    assert_eq!(remove_trailing_period("Hello..."), "Hello..."); // ellipsis preserved
    assert_eq!(remove_trailing_period("Hello.."), "Hello..");
    assert_eq!(remove_trailing_period("Hello"), "Hello");
}

#[test]
fn filler_words() {
    // The 11 cases from macOS TranscriptionFillerWordTests.
    assert_eq!(remove_filler_words("so uh I think we should um go", Some("en")), "so I think we should go");
    assert_eq!(remove_filler_words("well er maybe later", Some("en-GB")), "well maybe later");
    assert_eq!(remove_filler_words("ich denke er ist groß", Some("de")), "ich denke er ist groß");
    assert_eq!(remove_filler_words("Wir treffen uns um drei Uhr", Some("de")), "Wir treffen uns um drei Uhr");
    assert_eq!(remove_filler_words("ich denke er ist groß", None), "ich denke er ist groß");
    assert_eq!(remove_filler_words("Uh, I think we should go", Some("en")), "I think we should go");
    assert_eq!(remove_filler_words("um, the cat sat down", Some("en")), "The cat sat down");
    assert_eq!(remove_filler_words("uh the meeting starts soon", Some("en")), "The meeting starts soon");
    assert_eq!(remove_filler_words("so uh, I think we should go", Some("en")), "so I think we should go");
    assert_eq!(remove_filler_words("I think, uh, we should go", Some("en")), "I think, we should go");
    assert_eq!(remove_filler_words("I think we should go uh", Some("en")), "I think we should go");
}

#[test]
fn voice_commands() {
    assert_eq!(process_voice_commands("new line"), "\n\n");
    assert_eq!(process_voice_commands("newline"), "\n\n");
    assert_eq!(process_voice_commands("New Line."), "\n\n");
    // regex doesn't consume surrounding spaces (true behaviour on both platforms)
    assert_eq!(process_voice_commands("hello new line world"), "hello \n\n world");
    assert_eq!(process_voice_commands(""), "");
    // A4 regression: the command must NOT fire mid-word and leave an orphan "s".
    // The word boundary after "line" prevents matching inside "newlines"/"new lines".
    assert_eq!(process_voice_commands("newlines"), "newlines");
    assert_eq!(
        process_voice_commands("new lines between sections"),
        "new lines between sections"
    );
    // ...but a real "new line." (with trailing punctuation) still matches.
    assert_eq!(process_voice_commands("new line."), "\n\n");
}

#[test]
fn finalize_streaming() {
    assert_eq!(finalize_streaming_text("Hello\r\nworld"), "Hello\nworld");
    assert_eq!(finalize_streaming_text("Hello\n\n\nworld"), "Hello\n\nworld");
    assert_eq!(finalize_streaming_text("  Hello  \nworld  \n"), "Hello\nworld");
    assert_eq!(finalize_streaming_text(""), "");
    assert_eq!(finalize_streaming_text("   "), "");
}
