//! Conformance-vector tests for the shared language catalog.
//!
//! `shared-conformance/language-vectors.json` is the cross-platform source of
//! truth for the catalog issue #285 moved into `hw-catalog`. Two things live in
//! it:
//!
//! * `catalog` — every row, in picker order, each carrying a `decision` field.
//!   `both` means `LanguageData.swift` and `LanguageInfo.cs` both had the row;
//!   `macos` means only macOS had it, so the Windows picker GAINS it; `renamed`
//!   means both had it and the display name changes. This list IS the review
//!   surface. A row appearing, disappearing or being renamed is a visible
//!   change to every language picker on every platform.
//! * `canonicalCases`, `lookupCases` and `scalarCases` — the canonicalizer, the
//!   alias map and the scalar helpers. Windows had none of these: its lookup
//!   was `Code == code`, so a stored `en_GB` matched nothing and the picker
//!   printed the raw tag.
//!
//! The `Locale.localizedString` display-name fallback stays native, so a
//! `lookupCases` row with a null `displayName` is the core saying "I do not
//! know this one, localize it yourself".
//!
//! Regenerate after an intended catalog change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-core --test language_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. A row that moves without a matching `hw-catalog` edit is
//! a regression, not a refresh.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use hyperwhisper_core::ffi_catalog::{
    language_all, language_canonical_code, language_canonicalize, language_info, language_is_english,
    language_normalize, language_popular_codes, language_resolve,
};

const VECTORS_PATH: &str = "../../../shared-conformance/language-vectors.json";

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

/// Every code `LanguageInfo.AllLanguages` carried before this change. The rows
/// the shared catalog has and this list does not are exactly what the Windows
/// picker gains.
const WINDOWS_CODES: &[&str] = &[
    "auto",
    "en",
    "ja",
    "es",
    "zh",
    "zh-TW",
    "nl",
    "hi",
    "ru",
    "ko",
    "it",
    "uk",
    "pl",
    "pt",
    "el",
    "cs",
    "sv",
    "no",
    "da",
    "id",
    "af",
    "sq",
    "am",
    "ar",
    "hy",
    "as",
    "az",
    "ba",
    "eu",
    "be",
    "bn",
    "bs",
    "br",
    "bg",
    "yue",
    "ca",
    "hr",
    "et",
    "fo",
    "fi",
    "fr",
    "gl",
    "ka",
    "de",
    "gu",
    "ht",
    "ha",
    "haw",
    "he",
    "hu",
    "is",
    "jw",
    "kn",
    "kk",
    "km",
    "lo",
    "la",
    "lv",
    "ln",
    "lt",
    "lb",
    "mk",
    "mg",
    "ms",
    "ml",
    "mt",
    "mi",
    "mr",
    "mn",
    "my",
    "ne",
    "nn",
    "oc",
    "ps",
    "fa",
    "pa",
    "ro",
    "sa",
    "sr",
    "sn",
    "sd",
    "si",
    "sk",
    "sl",
    "so",
    "su",
    "sw",
    "tl",
    "tg",
    "ta",
    "tt",
    "te",
    "th",
    "bo",
    "tr",
    "tk",
    "ur",
    "uz",
    "vi",
    "cy",
    "yi",
    "yo",];

/// The one row whose display name changes. `zh-TW` and `zh-Hant` both read
/// "Chinese (Traditional)", so the macOS picker showed the same words twice.
const RENAMED_CODES: &[&str] = &["zh-TW"];

const BOTH: &str = "both";
const MACOS: &str = "macos";
const RENAMED: &str = "renamed";

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    popular_codes: Vec<String>,
    catalog: Vec<CatalogVector>,
    canonical_cases: Vec<CanonicalVector>,
    lookup_cases: Vec<LookupVector>,
    scalar_cases: Vec<ScalarVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct CatalogVector {
    code: String,
    display_name: String,
    /// `both`, `macos` or `renamed`. See the module docs.
    decision: String,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct CanonicalVector {
    name: String,
    input: String,
    canonical: String,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct LookupVector {
    name: String,
    input: String,
    /// The canonical code the resolver returns for `input`.
    code: String,
    /// `null` means the catalog does not know the code and the host localizes.
    display_name: Option<String>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct ScalarVector {
    name: String,
    /// `null` is a genuinely absent code, which every helper has a rule for.
    input: Option<String>,
    normalized: String,
    canonical_code: String,
    is_english: bool,
}

fn canonical_inputs() -> Vec<(&'static str, &'static str)> {
    vec![
        ("a bare primary subtag lowercases", "EN"),
        ("an underscore is a separator", "en_gb"),
        ("a two-character subtag uppercases", "en-gb"),
        ("a four-character script subtag title-cases", "ZH-HANT"),
        ("surrounding whitespace is trimmed", "  zh-hant  "),
        ("a three-character subtag lowercases", "es-419"),
        ("so does a five-character one, which is why the key is es-latam", "es-LATAM"),
        ("an empty tag means automatic", ""),
        ("whitespace only means automatic", "   "),
        ("separators with no subtags are not automatic", "-"),
        ("a three-part tag canonicalizes every subtag", "zh_hant_hk"),
    ]
}

fn lookup_inputs() -> Vec<(&'static str, &'static str)> {
    vec![
        ("automatic resolves without a catalog row", "auto"),
        ("a plain primary subtag", "en"),
        ("a stored Windows-style tag that used to match nothing", "en_GB"),
        ("an all-caps tag", "EN-GB"),
        ("an all-lowercase script tag", "zh-hant"),
        ("the row Windows never had", "pt-BR"),
        ("the renamed Traditional Chinese row", "zh-TW"),
        ("the other Traditional Chinese row", "zh-Hant"),
        ("the LatAm Spanish row keeps its lowercased key", "es-LATAM"),
        ("an unknown code is handed back for the host to localize", "xx_yy"),
        ("so is a plausible but uncatalogued primary subtag", "zz"),
    ]
}

fn scalar_inputs() -> Vec<(&'static str, Option<&'static str>)> {
    vec![
        ("an absent code defaults to English", None),
        ("an empty code is automatic to normalize and English to store", Some("")),
        ("a regional English tag", Some("en-GB")),
        ("an underscored regional English tag", Some("en_gb")),
        ("a script-qualified Chinese tag", Some("zh_Hant")),
        ("automatic is not English", Some("auto")),
        ("a three-letter code is not English", Some("eng")),
        ("Brazilian Portuguese", Some("pt_br")),
    ]
}

fn build_document() -> Document {
    let renamed = |code: &str| RENAMED_CODES.contains(&code);
    let windows_had = |code: &str| WINDOWS_CODES.iter().any(|had| had == &code);

    Document {
        description: "Golden language-catalog vectors (issue #285). Generated from hw-catalog \
            by `cargo test -p hw-core --test language_vectors -- --ignored regenerate`. \
            `catalog` is every row in picker order with a `decision` field: both / macos / \
            renamed — `macos` rows are the ones the Windows picker GAINS, `renamed` rows \
            change what the user reads. A null `displayName` in `lookupCases` means the \
            catalog does not know the code and the host localizes it with its own system \
            database."
            .to_string(),
        popular_codes: language_popular_codes(),
        catalog: language_all()
            .into_iter()
            .map(|row| CatalogVector {
                decision: if renamed(&row.code) {
                    RENAMED.to_string()
                } else if windows_had(&row.code) {
                    BOTH.to_string()
                } else {
                    MACOS.to_string()
                },
                display_name: row.display_name.unwrap_or_default(),
                code: row.code,
            })
            .collect(),
        canonical_cases: canonical_inputs()
            .into_iter()
            .map(|(name, input)| CanonicalVector {
                name: name.to_string(),
                canonical: language_canonicalize(input.to_string()),
                input: input.to_string(),
            })
            .collect(),
        lookup_cases: lookup_inputs()
            .into_iter()
            .map(|(name, input)| {
                let resolved = language_resolve(vec![input.to_string()]);
                let row = resolved
                    .into_iter()
                    .next()
                    .expect("resolve always answers one row per code");
                LookupVector {
                    name: name.to_string(),
                    input: input.to_string(),
                    code: row.code,
                    display_name: row.display_name,
                }
            })
            .collect(),
        scalar_cases: scalar_inputs()
            .into_iter()
            .map(|(name, input)| ScalarVector {
                name: name.to_string(),
                normalized: language_normalize(input.map(str::to_string)),
                canonical_code: language_canonical_code(input.map(str::to_string)),
                is_english: language_is_english(input.map(str::to_string)),
                input: input.map(str::to_string),
            })
            .collect(),
    }
}

fn load_document() -> Document {
    let raw = std::fs::read_to_string(vectors_path())
        .expect("shared-conformance/language-vectors.json must exist");
    serde_json::from_str(&raw).expect("language-vectors.json must parse")
}

/// Writes the vectors from the current shared-core answer. Ignored by default;
/// run it deliberately after an intended catalog change, then read the diff.
#[test]
#[ignore = "regenerates shared-conformance/language-vectors.json"]
fn regenerate() {
    let doc = build_document();
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}

/// The committed vectors are exactly what the shared core answers today.
#[test]
fn vectors_match_the_shared_core() {
    assert_eq!(load_document(), build_document(), "the catalog drifted — regenerate and read the diff");
}

/// The catalog is the reconciled superset: macOS loses nothing and Windows
/// gains every row it was missing.
#[test]
fn the_catalog_is_a_superset_of_both_native_lists() {
    let doc = load_document();
    assert_eq!(doc.catalog.len(), 126, "126 rows: 125 languages plus automatic");

    for code in WINDOWS_CODES {
        assert!(
            doc.catalog.iter().any(|row| row.code == *code),
            "the shared catalog dropped {code}, which the Windows picker had"
        );
    }

    let gained: Vec<&str> = doc
        .catalog
        .iter()
        .filter(|row| row.decision == MACOS)
        .map(|row| row.code.as_str())
        .collect();
    assert_eq!(
        gained.len(),
        24,
        "the Windows picker gains 24 rows, not {}: {gained:?}",
        gained.len()
    );

    let renamed: Vec<&str> = doc
        .catalog
        .iter()
        .filter(|row| row.decision == RENAMED)
        .map(|row| row.code.as_str())
        .collect();
    assert_eq!(renamed, vec!["zh-TW"], "only zh-TW is renamed");
}

/// Two rows that read the same are two rows the user cannot tell apart, and the
/// tail of the picker is sorted by display name, so equal names leave the order
/// undefined.
#[test]
fn every_display_name_in_the_catalog_is_unique() {
    let doc = load_document();
    for row in &doc.catalog {
        assert_eq!(
            doc.catalog
                .iter()
                .filter(|other| other.display_name == row.display_name)
                .count(),
            1,
            "{} is the display name of more than one row",
            row.display_name
        );
    }
}

/// Every popular code has a catalog row, and they open the list in order.
#[test]
fn the_popular_codes_open_the_picker_in_order() {
    let doc = load_document();
    assert_eq!(
        doc.catalog.first().map(|row| row.code.as_str()),
        Some("auto")
    );
    for (index, code) in doc.popular_codes.iter().enumerate() {
        assert_eq!(
            doc.catalog.get(index + 1).map(|row| row.code.as_str()),
            Some(code.as_str())
        );
    }
}

/// A `lookupCases` row with no display name is the core handing the code back
/// for the host's own `Locale.localizedString`. The set must not be empty, or
/// the fallback stops being exercised on any platform.
#[test]
fn the_native_display_name_fallback_still_has_a_case() {
    let doc = load_document();
    assert!(
        doc.lookup_cases
            .iter()
            .any(|case| case.display_name.is_none()),
        "no unknown-code row left — the native fallback is no longer covered"
    );
    for case in &doc.lookup_cases {
        assert_eq!(
            case.code,
            language_canonicalize(case.input.clone()),
            "{}: the resolved code must be the canonical form of the input",
            case.name
        );
    }
}

/// Every catalog row resolves to itself. A row whose code is not canonical
/// would be unreachable from a stored setting.
#[test]
fn every_catalog_row_resolves_to_itself() {
    for row in load_document().catalog {
        let found = language_info(row.code.clone())
            .unwrap_or_else(|| panic!("{} is in the catalog but does not resolve", row.code));
        assert_eq!(found.code, row.code);
        assert_eq!(found.display_name, Some(row.display_name));
    }
}
