//! Regression tests for issue #2612: a vocabulary replacement whose word starts
//! or ends with a symbol (C++, C#, .NET, 5%) never fired, because `vocab.rs`
//! built `\b<escaped word>\b` and `\b` cannot sit between two non-word
//! characters (e.g. "+" and the following space). The docs
//! (mintlify-help/vocabulary.mdx "Text replacements") promise a whole-word,
//! case-insensitive replacement.
//!
//! The first five tests are the bug hunt's repro (bh-20261026) as filed.
use hw_text::apply_hardened_replacement;

#[test]
fn replacement_fires_for_word_ending_in_symbol() {
    assert_eq!(
        apply_hardened_replacement("I write c++ daily", "C++", "C++"),
        "I write C++ daily"
    );
}
#[test]
fn replacement_fires_for_word_ending_in_hash() {
    assert_eq!(
        apply_hardened_replacement("I write c# code", "c#", "C-Sharp"),
        "I write C-Sharp code"
    );
}
#[test]
fn replacement_fires_for_word_starting_with_dot() {
    assert_eq!(
        apply_hardened_replacement("use .net today", ".net", ".NET"),
        "use .NET today"
    );
}
#[test]
fn replacement_fires_for_percent_number() {
    assert_eq!(
        apply_hardened_replacement("costs 5% more", "5%", "five percent"),
        "costs five percent more"
    );
}
#[test]
fn control_plain_word_still_whole_word_only() {
    assert_eq!(
        apply_hardened_replacement("metadata eta", "eta", "ETA"),
        "metadata ETA"
    );
}

/// The word-character side keeps its whole-word anchor: `c#` does not match
/// inside `abc#`, and `5%` does not match inside `15%`.
#[test]
fn the_word_side_is_still_anchored() {
    assert_eq!(
        apply_hardened_replacement("abc# and c#", "c#", "C-Sharp"),
        "abc# and C-Sharp"
    );
    assert_eq!(
        apply_hardened_replacement("15% or 5%", "5%", "five percent"),
        "15% or five percent"
    );
    assert_eq!(
        apply_hardened_replacement("dotnet .net", ".net", ".NET"),
        "dotnet .NET"
    );
    assert_eq!(
        apply_hardened_replacement(".network", ".net", ".NET"),
        ".network"
    );
}

/// The symbol-edged word matches at the start and end of the text, before
/// punctuation, every occurrence, and ignoring case.
#[test]
fn symbol_edged_words_match_at_edges_and_every_occurrence() {
    assert_eq!(
        apply_hardened_replacement("c++", "C++", "C Plus Plus"),
        "C Plus Plus"
    );
    assert_eq!(
        apply_hardened_replacement("C++, c++ and C++.", "c++", "C Plus Plus"),
        "C Plus Plus, C Plus Plus and C Plus Plus."
    );
    assert_eq!(
        apply_hardened_replacement(".NET is .net", ".net", ".NET"),
        ".NET is .NET"
    );
}

/// A multi-word entry with a symbol edge works the same way.
#[test]
fn multi_word_entry_ending_in_symbol() {
    assert_eq!(
        apply_hardened_replacement(
            "I like objective c++ a lot",
            "objective c++",
            "Objective-C++"
        ),
        "I like Objective-C++ a lot"
    );
}

/// A symbol in the middle of a word never needed the change and still works;
/// regex metacharacters in the word stay literal.
#[test]
fn inner_symbols_and_metacharacters_stay_literal() {
    assert_eq!(
        apply_hardened_replacement("node.js rocks", "node.js", "Node.js"),
        "Node.js rocks"
    );
    assert_eq!(
        apply_hardened_replacement("nodexjs rocks", "node.js", "Node.js"),
        "nodexjs rocks"
    );
    assert_eq!(apply_hardened_replacement("a (b) c", "(b)", "B"), "a B c");
}

/// The replacement stays literal for a symbol-edged word too.
#[test]
fn replacement_text_stays_literal() {
    assert_eq!(
        apply_hardened_replacement("c# here", "c#", "$0 $1"),
        "$0 $1 here"
    );
}
