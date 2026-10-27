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

/// A symbol edge still needs a whole-word neighbour: not a word character and
/// not the same symbol. So `c#` stays out of `c##`, `c++` out of `c+++`, and
/// `.net` out of `..net` and `asp.net` (review round 1 on PR #2647).
#[test]
fn a_symbol_edge_next_to_more_symbols_or_a_word_does_not_match() {
    assert_eq!(
        apply_hardened_replacement("c## and c#", "c#", "C-Sharp"),
        "c## and C-Sharp"
    );
    assert_eq!(
        apply_hardened_replacement("c+++ and c++", "c++", "C Plus Plus"),
        "c+++ and C Plus Plus"
    );
    assert_eq!(
        apply_hardened_replacement("..net and .net", ".net", ".NET"),
        "..net and .NET"
    );
    assert_eq!(
        apply_hardened_replacement("asp.net and .net", ".net", ".NET"),
        "asp.net and .NET"
    );
}

/// An entry made only of symbols replaces a standalone occurrence and leaves
/// the same symbol inside `C++`, `5%`, `C#` and `#tag` alone. With no anchor at
/// all, `+` → `plus` would turn `C++` into `Cplusplus`.
#[test]
fn a_symbol_only_entry_does_not_rewrite_inside_other_tokens() {
    assert_eq!(
        apply_hardened_replacement("I use C++ + Rust", "+", "plus"),
        "I use C++ plus Rust"
    );
    assert_eq!(
        apply_hardened_replacement("5% more, 100 %", "%", "percent"),
        "5% more, 100 percent"
    );
    assert_eq!(
        apply_hardened_replacement("C# #tag # x", "#", "hash"),
        "C# #tag hash x"
    );
    assert_eq!(
        apply_hardened_replacement("a -- b --- c", "--", "—"),
        "a — b --- c"
    );
}

/// Other punctuation next to a symbol edge is a boundary.
#[test]
fn other_punctuation_next_to_a_symbol_edge_still_matches() {
    assert_eq!(
        apply_hardened_replacement("(c++), c#. .net!", "c++", "C Plus Plus"),
        "(C Plus Plus), c#. .net!"
    );
    assert_eq!(
        apply_hardened_replacement("(c++), c#. .net!", "c#", "C-Sharp"),
        "(c++), C-Sharp. .net!"
    );
    assert_eq!(
        apply_hardened_replacement("(c++), c#. .net!", ".net", ".NET"),
        "(c++), c#. .NET!"
    );
    assert_eq!(
        apply_hardened_replacement("\"5%\"", "5%", "five percent"),
        "\"five percent\""
    );
}

/// A rejected candidate does not hide a real occurrence right after it, and
/// non-ASCII neighbours are handled on char boundaries.
#[test]
fn rejected_candidates_and_non_ascii_neighbours() {
    assert_eq!(apply_hardened_replacement("++ +", "+", "plus"), "++ plus");
    assert_eq!(
        apply_hardened_replacement("é% 5% ü", "5%", "five percent"),
        "é% five percent ü"
    );
    assert_eq!(
        apply_hardened_replacement("日本c# c#", "c#", "C-Sharp"),
        "日本c# C-Sharp"
    );
}
