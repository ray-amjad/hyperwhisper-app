//! The Mode object's wire contract: the documented key union, the required set
//! for a create, the value bounds, and what "the same name" means.
//!
//! Issue #356 items 2 and 5. They are one module because they are one question
//! — *what does a head accept in a `POST`/`PATCH /modes` body* — answered three
//! different ways today.
//!
//! # The published contract is the tie-breaker
//!
//! Every decision here is what `mintlify-help/api-reference/local-api/openapi.yaml`
//! already publishes. That is not a coincidence: when two heads disagree, the
//! published contract is what a client was told, so it wins over whichever head
//! happens to be more permissive.
//!
//! # An unrecognised key is IGNORED, not rejected
//!
//! [`mode_key_classification`] is the authoritative key list — the one place the
//! `Mode` + `ModePatch` union is written down — and its verdict for an
//! unrecognised key is *ignore*.
//!
//! `openapi.yaml` documents five keys as "Windows only. macOS ignores this key",
//! so the published contract actively invites a cross-platform client to send
//! keys a given head does not implement. Rejecting would break that documented
//! write path. macOS and Windows already ignore — both drop an unmapped key
//! inside their JSON decoders, before any code here could see it — and the
//! portable head is the one that rejected
//! (`ApplicationLocalApiBackend.cs`, `default: throw new ArgumentException`).
//!
//! So this is **not an enforcement gate applied on all three heads before
//! storage**, and describing it that way would be wrong. It has exactly two call
//! sites: the portable head, where it replaces the `throw`, and Windows, where
//! it is a debug log. It is not called on macOS, whose decoder already conforms
//! and which would need a `JSONSerialization` pass added purely to log a key it
//! has already discarded.
//!
//! # Lengths are counted in Unicode scalar values
//!
//! `chars().count()`, everywhere. This has to be said out loud because the three
//! heads count differently today: `string.Length` on .NET is UTF-16 code units
//! and `String.count` in Swift is grapheme clusters. A 60-emoji mode name is 120
//! units on the portable head (refused at the 100 bound) and 60 scalars here
//! (accepted). Scalars are the only count all three can compute identically —
//! .NET has `EnumerateRunes`, Swift has `unicodeScalars` — and it is the count
//! that does not change when someone adds a combining mark.
//!
//! # Where the NFC line falls, and why it is not crossed here
//!
//! The collision rule splits deliberately. [`mode_name_comparison_key`] is
//! `trim` + `to_lowercase`, both `std`, and that is now **the** definition of
//! "the same name". It is not byte-identical to .NET's `OrdinalIgnoreCase`
//! (simple case mapping) nor to Core Data's `==[c]`, and that is the point: one
//! deterministic rule replaces three.
//!
//! What stays native is *pre*-normalisation. macOS's `ModeNamePolicy.normalized`
//! applies NFC (`precomposedStringWithCanonicalMapping`) and trims boundary
//! characters by Unicode general category, in front of the shared key.
//! Reproducing that here needs `unicode-normalization` and a category table, and
//! `Cargo.toml` states at length why this crate takes no dependency: it sits in
//! front of a loopback socket under `panic = "abort"`, so every added crate is
//! another thing that can abort the app from a hostile mode name.
//!
//! **Do not "fix" this by adding `unicode-normalization`.** The macOS pre-step
//! is already pinned by `ModesEndpointTests.swift`
//! (`normalizedName("Cafe\u{301}") == "Café"`, and zero-width/word-joiner names
//! rejected); those tests keep passing precisely because the pre-step stays
//! where it is.

use crate::failure::{Failure, LocalApiErrorCode};

/// Which request a body belongs to.
///
/// The required-key rule is create-only: `openapi.yaml`'s `Mode` schema carries
/// a `required:` list and its `ModePatch` schema does not, because "any field
/// omitted is left untouched" is the whole meaning of a patch.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModeOperation {
    /// `POST /modes`.
    Create,
    /// `PATCH /modes/{id}`.
    Patch,
}

/// What a head should do with a top-level key in a mode body.
///
/// Every variant means "keep going": none of them is a rejection. See the module
/// docs for why an unknown key is ignored rather than refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModeKeyClass {
    /// A documented key every head is expected to honour.
    Known,
    /// A documented key `openapi.yaml` marks "Windows only. macOS ignores this
    /// key". A head that does not implement it drops it, as the docs say it
    /// does.
    PlatformOnly,
    /// A documented key the server owns: it is returned on a `GET` and ignored
    /// on a write. `openapi.yaml`'s `ModePatch` description names these four.
    ReadOnly,
    /// Not in the union at all. Ignore it — and log it, because a key that shows
    /// up here is either a client typo or a key someone added to one head and
    /// not to this table.
    Unknown,
}

/// The keys a create body must carry: the `required:` list at `openapi.yaml`.
///
/// Adopted rather than invented. It is macOS's set by construction — `ModeDTO`
/// declares exactly these seven as non-optional `let`s, so a body missing one
/// fails `decode` — and it is the published one. Windows requires only `name`
/// today and the portable head requires nothing at all, so this **tightens two
/// heads onto their own published contract**. `{"name":"Only"}` creates a mode
/// on both today and becomes a failure after #356.
///
/// The alternative — shrink the set to `name` and loosen macOS — was rejected:
/// it would make macOS invent storage defaults for six fields it has never
/// defaulted, on the one head that cannot be built in CI's Linux sandbox, and it
/// would weaken `openapi.yaml` rather than meet it. If review disagrees, the
/// flip is this array plus the doc.
///
/// A unit test pins these against the same seven strings written out
/// independently; that test is the stand-in for the macOS decoder, which
/// enforces the set natively and never calls
/// [`missing_required_mode_keys`].
pub const REQUIRED_MODE_KEYS: [&str; 7] = [
    "name",
    "preset",
    "language",
    "model",
    "punctuation",
    "capitalization",
    "profanityFilter",
];

/// Keys the server owns. Present on a `GET`, ignored on a write.
const READ_ONLY_MODE_KEYS: [&str; 4] = ["id", "isSystemProvided", "createdDate", "modifiedDate"];

/// The five keys `openapi.yaml` marks "Windows only. macOS ignores this key".
const PLATFORM_ONLY_MODE_KEYS: [&str; 5] = [
    "localEngine",
    "localParakeetModel",
    "localPostProcessingModel",
    "customVocabulary",
    "providerType",
];

/// Every writable key in the `Mode` + `ModePatch` union that is not
/// platform-only.
///
/// In `openapi.yaml`'s `Mode` order. `ModePatch` is this list plus the
/// platform-only five: it is `Mode` minus the four read-only keys, so nothing
/// here is patch-only.
const KNOWN_MODE_KEYS: [&str; 24] = [
    "name",
    "preset",
    "language",
    "model",
    "punctuation",
    "capitalization",
    "profanityFilter",
    "customInstructions",
    "userSystemPrompt",
    "isDefault",
    "sortOrder",
    "languageModel",
    "cloudTranscriptionModel",
    "cloudProvider",
    "postProcessingMode",
    "postProcessingProvider",
    "englishSpelling",
    "useStreamingTranscription",
    "cloudAccuracyTier",
    "removeTrailingPeriod",
    "enableScreenOCR",
    "geminiCustomPrompt",
    "cloudPostProcessingModel",
    "cloudTranscriptionDomain",
];

/// Longest mode name, in Unicode scalar values.
pub const MODE_NAME_MAX_CHARS: usize = 100;
/// Longest `language`, in Unicode scalar values.
pub const MODE_LANGUAGE_MAX_CHARS: usize = 32;
/// Longest `preset`, in Unicode scalar values.
pub const MODE_PRESET_MAX_CHARS: usize = 64;
/// Longest `userSystemPrompt` / `geminiCustomPrompt`, in Unicode scalar values.
pub const MODE_PROMPT_MAX_CHARS: usize = 2000;
/// Most `customVocabulary` terms.
pub const MODE_CUSTOM_VOCABULARY_MAX_TERMS: usize = 1000;
/// Longest single `customVocabulary` term, in Unicode scalar values.
pub const MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS: usize = 200;
/// Smallest accepted `postProcessingMode`: 0 = off.
pub const MODE_POST_PROCESSING_MODE_MIN: i64 = 0;
/// Largest accepted `postProcessingMode`: 2 = local. 1 = cloud.
pub const MODE_POST_PROCESSING_MODE_MAX: i64 = 2;
/// Smallest accepted `sortOrder`: `Int16.min`.
pub const MODE_SORT_ORDER_MIN: i64 = -32768;
/// Largest accepted `sortOrder`: `Int16.max`.
pub const MODE_SORT_ORDER_MAX: i64 = 32767;

/// Classify one top-level key of a mode body.
///
/// Matching is exact and case-sensitive: JSON member names are case-sensitive
/// and every head's DTO spells these in camelCase.
///
/// **Call sites.** The portable head's `ApplyModeDocument`, where it replaces
/// `default: throw new ArgumentException($"Unsupported mode field …")` with
/// classify-and-ignore; and Windows, as a debug log beside its decode. **Not
/// called on macOS**, whose `Codable` decoder discards an unmapped key before
/// any Rust could see it — see the module docs.
#[must_use]
pub fn mode_key_classification(key: &str) -> ModeKeyClass {
    if KNOWN_MODE_KEYS.contains(&key) {
        ModeKeyClass::Known
    } else if PLATFORM_ONLY_MODE_KEYS.contains(&key) {
        ModeKeyClass::PlatformOnly
    } else if READ_ONLY_MODE_KEYS.contains(&key) {
        ModeKeyClass::ReadOnly
    } else {
        ModeKeyClass::Unknown
    }
}

/// Which of [`REQUIRED_MODE_KEYS`] a create body did not carry, in the order
/// [`REQUIRED_MODE_KEYS`] lists them.
///
/// `present` is the head's list of top-level key names, in whatever order its
/// JSON reader yielded them. An empty result means the body is complete.
///
/// **Call sites: the portable head and Windows only, and that is deliberate.**
/// The portable head already walks `document.EnumerateObject()` and has the
/// names in hand. Windows needs a new reader to get them — it cannot infer
/// presence from `ModeDto`, whose `Punctuation`/`Capitalization`/`ProfanityFilter`
/// are non-nullable `bool`, so an absent key and `false` are the same value.
/// macOS needs neither: its decoder *is* this check, and the unit test pinning
/// [`REQUIRED_MODE_KEYS`] against the seven literal strings is what keeps the two
/// in step.
#[must_use]
pub fn missing_required_mode_keys(present: &[String]) -> Vec<String> {
    REQUIRED_MODE_KEYS
        .into_iter()
        .filter(|required| !present.iter().any(|key| key == required))
        .map(String::from)
        .collect()
}

/// The fields [`validate_mode`] bounds, plus what the request is and which keys
/// it carried.
///
/// Every field is optional because a `PATCH` body legitimately omits any of
/// them; on a `CREATE` the required-key check is what makes an omission a
/// failure, not the absence of a value here.
///
/// The two numbers are `i64`, not `i32`/`i16`, so a head can hand over a value
/// that is out of range **without pre-truncating it**. The portable head's
/// `property.Value.GetInt32()` throws `FormatException` on
/// `{"sortOrder": 99999999999}` today, and nothing in its middleware catches
/// that — an unhandled HTTP 500 with no envelope. Widening the crossing is what
/// lets that become an ordinary `INVALID_REQUEST`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModeValidationInput {
    /// Create or patch. Only a create is checked against
    /// [`REQUIRED_MODE_KEYS`].
    pub operation: ModeOperation,
    /// The body's top-level key names. Used only for the required-key check, so
    /// a patch may pass an empty list.
    pub present_keys: Vec<String>,
    /// `name`, as sent. Trimmed here; the caller keeps its own storage name.
    pub name: Option<String>,
    /// `language`.
    pub language: Option<String>,
    /// `preset`.
    pub preset: Option<String>,
    /// `postProcessingMode`. 0 = off, 1 = cloud, 2 = local.
    pub post_processing_mode: Option<i64>,
    /// `sortOrder`.
    pub sort_order: Option<i64>,
    /// `userSystemPrompt`.
    pub user_system_prompt: Option<String>,
    /// `geminiCustomPrompt`.
    pub gemini_custom_prompt: Option<String>,
    /// `customVocabulary`.
    pub custom_vocabulary: Option<Vec<String>>,
}

impl ModeValidationInput {
    /// An input with nothing set, for the common case of filling two or three
    /// fields on a patch.
    #[must_use]
    pub fn new(operation: ModeOperation) -> ModeValidationInput {
        ModeValidationInput {
            operation,
            present_keys: Vec::new(),
            name: None,
            language: None,
            preset: None,
            post_processing_mode: None,
            sort_order: None,
            user_system_prompt: None,
            gemini_custom_prompt: None,
            custom_vocabulary: None,
        }
    }
}

/// Validate a mode body's shape and value ranges. `None` means it is acceptable.
///
/// **Call sites: all three heads**, on both the create and the patch path.
///
/// The order is fixed so two heads cannot report different first failures for
/// the same body: required keys, then `name`, `language`, `preset`,
/// `postProcessingMode`, `sortOrder`, the two prompts, then `customVocabulary`.
///
/// EVERY failure here is a business failure: HTTP 200 carrying
/// `INVALID_REQUEST`, a value the caller can read and correct. That includes a
/// missing required key, which was HTTP 400 until review round 1 — see
/// [`missing_required_keys_failure`] for why the published envelope rule in
/// `openapi.yaml`'s `info.description` settles it. **No new error code.**
///
/// # What is deliberately *not* checked here
///
/// The cross-field rule "an enabled `postProcessingMode` requires a provider".
/// The portable head's version is one line; Windows's
/// `ValidatePostProcessingSelection` is a much richer rule that reaches into
/// `CustomEndpointManager`, `LanguageModelInfo` and
/// `PlatformHelper.SupportsLocalLlmPostProcessing`; macOS has none. Folding them
/// together is a *capability* question, not a wire-shape one, and platform
/// capability is exactly what this crate keeps out — the same reason
/// [`crate::resolve_engine_alias`] returns an id and not an availability verdict.
/// All three heads keep their own rule.
///
/// Also not here: `model`, `cloudProvider`, `localEngine` and the other
/// catalog-backed strings. Those are membership tests against a catalog each
/// head owns, and `cloudProvider` already has a shared home in `hw-catalog`'s
/// `CloudSttCatalog`.
#[must_use]
pub fn validate_mode(input: &ModeValidationInput) -> Option<Failure> {
    if input.operation == ModeOperation::Create {
        let missing = missing_required_mode_keys(&input.present_keys);
        if !missing.is_empty() {
            return Some(missing_required_keys_failure(&missing));
        }
    }

    if let Some(name) = &input.name {
        let trimmed = name.trim();
        if mode_name_comparison_key(name).is_none() {
            return Some(invalid("Mode 'name' cannot be empty"));
        }
        if trimmed.chars().count() > MODE_NAME_MAX_CHARS {
            return Some(invalid(format!(
                "Mode name must contain 1 to {MODE_NAME_MAX_CHARS} characters."
            )));
        }
    }

    if let Some(language) = &input.language {
        if !within(language, 1, MODE_LANGUAGE_MAX_CHARS) {
            return Some(invalid("Mode language is invalid."));
        }
    }

    if let Some(preset) = &input.preset {
        if !within(preset, 1, MODE_PRESET_MAX_CHARS) {
            return Some(invalid("Mode preset is invalid."));
        }
    }

    if let Some(mode) = input.post_processing_mode {
        if !(MODE_POST_PROCESSING_MODE_MIN..=MODE_POST_PROCESSING_MODE_MAX).contains(&mode) {
            return Some(invalid("Mode 'postProcessingMode' must be 0, 1, or 2"));
        }
    }

    if let Some(order) = input.sort_order {
        if !(MODE_SORT_ORDER_MIN..=MODE_SORT_ORDER_MAX).contains(&order) {
            return Some(invalid(format!(
                "Mode 'sortOrder' must be between {MODE_SORT_ORDER_MIN} and {MODE_SORT_ORDER_MAX}"
            )));
        }
    }

    for prompt in [&input.user_system_prompt, &input.gemini_custom_prompt]
        .into_iter()
        .flatten()
    {
        if prompt.chars().count() > MODE_PROMPT_MAX_CHARS {
            return Some(invalid(format!(
                "Mode prompt exceeds {MODE_PROMPT_MAX_CHARS} characters."
            )));
        }
    }

    if let Some(vocabulary) = &input.custom_vocabulary {
        let too_many = vocabulary.len() > MODE_CUSTOM_VOCABULARY_MAX_TERMS;
        let term_too_long = vocabulary
            .iter()
            .any(|term| term.chars().count() > MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS);
        if too_many || term_too_long {
            return Some(invalid("Custom vocabulary is invalid."));
        }
    }

    None
}

/// The key a name is compared *by*. `None` when nothing is left after trimming.
///
/// Trim Unicode whitespace (`str::trim`, which is `char::is_whitespace`), then
/// `to_lowercase`. Both `std`. **This is the definition of "the same name"** —
/// it is what [`mode_name_conflict`] compares and what an emptiness check should
/// ask. It is *not* a storage name: each head keeps writing whatever its own
/// pre-normalisation produced.
///
/// **Call sites: all three heads.** On macOS it sits behind
/// `ModeNamePolicy.normalized`, which keeps NFC and the general-category trim —
/// see the module docs on why that half does not move into this crate.
#[must_use]
pub fn mode_name_comparison_key(name: &str) -> Option<String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_lowercase())
    }
}

/// Whether `candidate` collides with any of `other_names`.
///
/// `other_names` is the caller's **already-filtered** list: every head excludes
/// the record being edited before it compares (`item.Id != mode.Id`,
/// `clash.Id != existing.Id`, `clash.id != mode.id`), and that filter stays in
/// the head because only the head knows which record it is writing.
///
/// A candidate that is empty under [`mode_name_comparison_key`] never collides —
/// an empty name is [`validate_mode`]'s failure, not this one's, and reporting it
/// as a collision would name the wrong problem.
///
/// **Call sites: all three heads.** macOS's needs its collision lookup to stop
/// being a store-side `name ==[c] %@` fetch and become "fetch the candidate
/// names, compare here" — six lines in `fetchMode(byName:in:)`, on a head that
/// already does full-table mode fetches to list and delete. If that is judged
/// too invasive, macOS keeps `==[c]` and this has two call sites rather than
/// three — but then the collision rule is still not one rule.
#[must_use]
pub fn mode_name_conflict(candidate: &str, other_names: &[String]) -> bool {
    match mode_name_comparison_key(candidate) {
        None => false,
        Some(key) => other_names
            .iter()
            .filter_map(|other| mode_name_comparison_key(other))
            .any(|other| other == key),
    }
}

/// The response a colliding name gets: HTTP 200 carrying `MODE_NAME_TAKEN`.
///
/// The message is macOS's and Windows's, verbatim — two heads already send it
/// and it names the offending value. The portable head's
/// `"A mode with this name already exists."` names nothing and, worse, arrives
/// as an `ArgumentException` that its middleware turns into HTTP **400
/// `INVALID_REQUEST`**: `MODE_NAME_TAKEN` is declared on that head and never
/// emitted.
///
/// The hint follows the two heads' split by operation rather than flattening it:
/// both macOS and Windows attach "choose a different name" on create and send no
/// hint on patch. Preserving that keeps this a pure refactor on the two heads
/// that already ship the string.
///
/// **Call sites: all three heads.**
#[must_use]
pub fn mode_name_taken_failure(name: &str, operation: ModeOperation) -> Failure {
    let failure = Failure::business(
        LocalApiErrorCode::ModeNameTaken,
        format!("A mode named '{name}' already exists"),
    );
    match operation {
        ModeOperation::Create => {
            failure.with_hint("Choose a different name or PATCH the existing mode instead.")
        }
        ModeOperation::Patch => failure,
    }
}

/// HTTP 200 carrying `INVALID_REQUEST`: the create body left out keys
/// `openapi.yaml` marks required.
///
/// **It is a business failure, not a protocol failure**, and that is the
/// published rule rather than a preference. `openapi.yaml`'s `info.description`
/// — unchanged by issue #356 — says expected business failures return HTTP 200
/// with the `{"ok":false,"error":{"code":…}}` envelope, and that *"HTTP 4xx is
/// reserved for protocol failures: malformed JSON (400), missing or invalid
/// bearer token (401), or a rejected Host/Origin header (403)"*. A body that is
/// well-formed JSON and merely incomplete is none of those three.
///
/// This was HTTP 400 when the rule moved into this crate, on the reasoning that
/// macOS answers 400 for the same body. It does — but on macOS that body is a
/// `decode` failure, which genuinely IS the protocol case, while on the other
/// two heads it is validation. Matching the status of a different fault is not
/// the same as agreeing.
///
/// The hint is macOS's, verbatim (`ModesEndpoint.swift`'s create decode
/// failure), because macOS is the head whose decoder already enforces this set
/// and its hint already lists all seven.
fn missing_required_keys_failure(missing: &[String]) -> Failure {
    invalid(format!(
        "Mode is missing required field(s): {}",
        missing.join(", ")
    ))
    .with_hint(format!(
        "Required: {}. See /modes GET for the full shape.",
        REQUIRED_MODE_KEYS.join(", ")
    ))
}

/// HTTP 200 carrying `INVALID_REQUEST` — a value the caller can read and fix.
fn invalid(message: impl Into<String>) -> Failure {
    Failure::business(LocalApiErrorCode::InvalidRequest, message)
}

/// Whether `value`'s scalar count is within `min..=max`.
fn within(value: &str, min: usize, max: usize) -> bool {
    let length = value.chars().count();
    length >= min && length <= max
}

#[cfg(test)]
mod tests {
    use super::{
        missing_required_mode_keys, mode_key_classification, mode_name_comparison_key,
        mode_name_conflict, mode_name_taken_failure, validate_mode, ModeKeyClass, ModeOperation,
        ModeValidationInput, KNOWN_MODE_KEYS, MODE_CUSTOM_VOCABULARY_MAX_TERMS,
        MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS, MODE_NAME_MAX_CHARS, MODE_PROMPT_MAX_CHARS,
        PLATFORM_ONLY_MODE_KEYS, READ_ONLY_MODE_KEYS, REQUIRED_MODE_KEYS,
    };
    use crate::failure::LocalApiErrorCode;

    fn names(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| String::from(*value)).collect()
    }

    fn create_with(keys: &[&str]) -> ModeValidationInput {
        ModeValidationInput {
            present_keys: names(keys),
            ..ModeValidationInput::new(ModeOperation::Create)
        }
    }

    fn complete_create() -> ModeValidationInput {
        create_with(&REQUIRED_MODE_KEYS)
    }

    /// The required set is the seven strings `openapi.yaml:330` lists, written
    /// out a second time so a reordering or a rename cannot pass unnoticed.
    ///
    /// This test is also the stand-in for the macOS decoder. macOS enforces the
    /// set natively — `ModeDTO` declares exactly these seven as non-optional —
    /// and never calls `missing_required_mode_keys`, so nothing else ties the
    /// two together.
    #[test]
    fn the_required_set_is_the_documented_seven() {
        assert_eq!(
            REQUIRED_MODE_KEYS.to_vec(),
            vec![
                "name",
                "preset",
                "language",
                "model",
                "punctuation",
                "capitalization",
                "profanityFilter",
            ]
        );
        assert_eq!(REQUIRED_MODE_KEYS.len(), 7);
        // Every required key is also a known key, or a conformant body would be
        // one this crate then classifies as unrecognised.
        for key in REQUIRED_MODE_KEYS {
            assert_eq!(mode_key_classification(key), ModeKeyClass::Known, "{key}");
        }
    }

    /// The union is 33 keys — `openapi.yaml`'s `Mode` schema — split 24 / 5 / 4,
    /// with no key in two buckets.
    #[test]
    fn the_key_union_is_the_documented_thirty_three() {
        assert_eq!(KNOWN_MODE_KEYS.len(), 24);
        assert_eq!(PLATFORM_ONLY_MODE_KEYS.len(), 5);
        assert_eq!(READ_ONLY_MODE_KEYS.len(), 4);

        let mut all: Vec<&str> = KNOWN_MODE_KEYS
            .into_iter()
            .chain(PLATFORM_ONLY_MODE_KEYS)
            .chain(READ_ONLY_MODE_KEYS)
            .collect();
        assert_eq!(all.len(), 33);
        all.sort_unstable();
        let mut unique = all.clone();
        unique.dedup();
        assert_eq!(all, unique, "a key appears in two buckets");
    }

    #[test]
    fn keys_classify_into_the_documented_buckets() {
        assert_eq!(mode_key_classification("name"), ModeKeyClass::Known);
        assert_eq!(
            mode_key_classification("cloudTranscriptionDomain"),
            ModeKeyClass::Known
        );
        // The five `openapi.yaml` marks "Windows only. macOS ignores this key".
        for key in PLATFORM_ONLY_MODE_KEYS {
            assert_eq!(
                mode_key_classification(key),
                ModeKeyClass::PlatformOnly,
                "{key}"
            );
        }
        for key in READ_ONLY_MODE_KEYS {
            assert_eq!(
                mode_key_classification(key),
                ModeKeyClass::ReadOnly,
                "{key}"
            );
        }
        // Not in the union, and case matters: JSON member names are
        // case-sensitive and every head's DTO spells them in camelCase.
        for key in ["notAField", "Name", "NAME", "", "sortorder", " name"] {
            assert_eq!(mode_key_classification(key), ModeKeyClass::Unknown, "{key}");
        }
    }

    #[test]
    fn missing_required_keys_are_reported_in_declaration_order() {
        assert_eq!(missing_required_mode_keys(&names(&REQUIRED_MODE_KEYS)), {
            let empty: Vec<String> = Vec::new();
            empty
        });
        // The Windows create body that works today.
        assert_eq!(
            missing_required_mode_keys(&names(&["name"])),
            names(&[
                "preset",
                "language",
                "model",
                "punctuation",
                "capitalization",
                "profanityFilter"
            ])
        );
        // Order follows REQUIRED_MODE_KEYS, not the caller's key order.
        assert_eq!(
            missing_required_mode_keys(&names(&["profanityFilter", "model", "language"])),
            names(&["name", "preset", "punctuation", "capitalization"])
        );
        // Extra keys are not this function's business.
        assert!(missing_required_mode_keys(&names(
            &["notAField", "id", "sortOrder"]
                .iter()
                .chain(REQUIRED_MODE_KEYS.iter())
                .copied()
                .collect::<Vec<&str>>()
        ))
        .is_empty());
    }

    /// A create missing a required key is HTTP **200** carrying
    /// `INVALID_REQUEST`: a well-formed body that is merely incomplete is an
    /// expected business failure, and `openapi.yaml`'s `info.description`
    /// reserves 4xx for malformed JSON, a bad token and a rejected origin. A
    /// patch is never checked against the set.
    #[test]
    fn a_create_needs_the_seven_and_a_patch_does_not() {
        let failure = validate_mode(&create_with(&["name"])).expect("incomplete create");
        assert_eq!(failure.http_status(), 200);
        assert_eq!(failure.code, LocalApiErrorCode::InvalidRequest);
        assert!(
            failure.message.contains("preset"),
            "{}",
            failure.message.clone()
        );
        assert_eq!(
            failure.hint.as_deref(),
            Some(
                "Required: name, preset, language, model, punctuation, capitalization, profanityFilter. See /modes GET for the full shape."
            )
        );

        assert_eq!(validate_mode(&complete_create()), None);
        // The same body as a patch: fine, because every ModePatch key is
        // optional.
        assert_eq!(
            validate_mode(&ModeValidationInput::new(ModeOperation::Patch)),
            None
        );
        assert_eq!(
            validate_mode(&ModeValidationInput {
                present_keys: names(&["name"]),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            None
        );
    }

    /// Every bound failure is a *business* failure: HTTP 200 carrying
    /// `INVALID_REQUEST`, one of the closed 14. Never a 400 and never a new code.
    #[test]
    fn every_bound_failure_is_http_200_invalid_request() {
        let out_of_range = [
            ModeValidationInput {
                name: Some("x".repeat(MODE_NAME_MAX_CHARS + 1)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                language: Some(String::new()),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                preset: Some("p".repeat(65)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                post_processing_mode: Some(3),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                sort_order: Some(32768),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                user_system_prompt: Some("u".repeat(MODE_PROMPT_MAX_CHARS + 1)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                custom_vocabulary: Some(vec![
                    String::from("a");
                    MODE_CUSTOM_VOCABULARY_MAX_TERMS + 1
                ]),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
        ];
        for input in &out_of_range {
            let failure = validate_mode(input).expect("out of range");
            assert_eq!(failure.http_status(), 200, "{input:?}");
            assert_eq!(failure.code, LocalApiErrorCode::InvalidRequest, "{input:?}");
        }
    }

    /// The exact strings. Two of them are macOS's, and macOS is the only head
    /// with a test on its wording; the rest are the portable head's, which is
    /// the only head that had these bounds at all.
    #[test]
    fn the_bound_messages_are_the_shipping_ones() {
        let message = |input: ModeValidationInput| {
            validate_mode(&input)
                .map(|failure| failure.message)
                .unwrap()
        };
        assert_eq!(
            message(ModeValidationInput {
                name: Some(String::from("   ")),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode 'name' cannot be empty"
        );
        assert_eq!(
            message(ModeValidationInput {
                name: Some("x".repeat(101)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode name must contain 1 to 100 characters."
        );
        assert_eq!(
            message(ModeValidationInput {
                language: Some("l".repeat(33)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode language is invalid."
        );
        assert_eq!(
            message(ModeValidationInput {
                preset: Some(String::new()),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode preset is invalid."
        );
        assert_eq!(
            message(ModeValidationInput {
                post_processing_mode: Some(-1),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode 'postProcessingMode' must be 0, 1, or 2"
        );
        assert_eq!(
            message(ModeValidationInput {
                sort_order: Some(-32769),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode 'sortOrder' must be between -32768 and 32767"
        );
        assert_eq!(
            message(ModeValidationInput {
                gemini_custom_prompt: Some("g".repeat(2001)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Mode prompt exceeds 2000 characters."
        );
        assert_eq!(
            message(ModeValidationInput {
                custom_vocabulary: Some(
                    vec!["t".repeat(MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS + 1)]
                ),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            "Custom vocabulary is invalid."
        );
    }

    /// The exact edge of every bound is accepted. A head that used `>=` where
    /// this uses `>` would refuse a value the docs publish as legal.
    #[test]
    fn the_bounds_are_inclusive_at_the_edge() {
        let accepted = [
            ModeValidationInput {
                name: Some("x".repeat(MODE_NAME_MAX_CHARS)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                language: Some("l".repeat(32)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                preset: Some("p".repeat(64)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                post_processing_mode: Some(0),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                post_processing_mode: Some(2),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                sort_order: Some(-32768),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                sort_order: Some(32767),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                user_system_prompt: Some("u".repeat(MODE_PROMPT_MAX_CHARS)),
                gemini_custom_prompt: Some("g".repeat(MODE_PROMPT_MAX_CHARS)),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
            ModeValidationInput {
                custom_vocabulary: Some(vec![
                    "t".repeat(MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS);
                    MODE_CUSTOM_VOCABULARY_MAX_TERMS
                ]),
                ..ModeValidationInput::new(ModeOperation::Patch)
            },
        ];
        for input in &accepted {
            assert_eq!(validate_mode(input), None, "{:?}", input.operation);
        }
    }

    /// Length is Unicode **scalar values**, not UTF-16 code units and not
    /// grapheme clusters.
    ///
    /// This is the divergence the shared rule closes, and it is client-visible.
    /// 60 astral emoji are 120 UTF-16 units, so the portable head refuses them
    /// against its 100 bound today; they are 60 scalars here and are accepted.
    /// A 100-scalar name of combining sequences is far fewer grapheme clusters,
    /// so Swift's `String.count` would have accepted more than 100 scalars.
    #[test]
    fn length_is_counted_in_unicode_scalar_values() {
        // 60 astral emoji: 60 scalars, 120 UTF-16 code units, 60 graphemes.
        let emoji = "😀".repeat(60);
        assert_eq!(emoji.chars().count(), 60);
        assert_eq!(emoji.encode_utf16().count(), 120);
        assert_eq!(
            validate_mode(&ModeValidationInput {
                name: Some(emoji),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            None,
            "120 UTF-16 units is 60 scalars and must be accepted"
        );

        // 101 astral emoji: 101 scalars, over the bound whatever .NET counts.
        assert!(validate_mode(&ModeValidationInput {
            name: Some("😀".repeat(MODE_NAME_MAX_CHARS + 1)),
            ..ModeValidationInput::new(ModeOperation::Patch)
        })
        .is_some());

        // 100 combining sequences: 200 scalars but only 100 grapheme clusters,
        // so a head counting graphemes would let it through.
        let combining = "e\u{301}".repeat(100);
        assert_eq!(combining.chars().count(), 200);
        assert!(
            validate_mode(&ModeValidationInput {
                name: Some(combining),
                ..ModeValidationInput::new(ModeOperation::Patch)
            })
            .is_some(),
            "200 scalars is over the bound even at 100 graphemes"
        );

        // And the same rule applies to a vocabulary term, not just the name.
        assert!(validate_mode(&ModeValidationInput {
            custom_vocabulary: Some(vec!["😀".repeat(MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS + 1)]),
            ..ModeValidationInput::new(ModeOperation::Patch)
        })
        .is_some());
    }

    /// The name bound is measured after trimming, as the portable head measures
    /// it (`mode.Name = mode.Name.Trim()` runs first).
    #[test]
    fn the_name_bound_is_measured_after_trimming() {
        assert_eq!(
            validate_mode(&ModeValidationInput {
                name: Some(format!("   {}   ", "x".repeat(MODE_NAME_MAX_CHARS))),
                ..ModeValidationInput::new(ModeOperation::Patch)
            }),
            None
        );
    }

    /// The order is fixed: whichever failure fires first, every head reports the
    /// same one for the same body.
    #[test]
    fn the_first_failure_is_the_same_on_every_head() {
        // Required keys beat every value bound.
        let failure = validate_mode(&ModeValidationInput {
            present_keys: names(&["name"]),
            name: Some(String::new()),
            sort_order: Some(i64::MAX),
            ..ModeValidationInput::new(ModeOperation::Create)
        })
        .expect("incomplete create");
        assert_eq!(failure.http_status(), 200);
        assert!(failure.message.contains("missing required field(s)"));

        // Then name, before language.
        let failure = validate_mode(&ModeValidationInput {
            name: Some(String::new()),
            language: Some("l".repeat(99)),
            ..ModeValidationInput::new(ModeOperation::Patch)
        })
        .expect("empty name");
        assert_eq!(failure.message, "Mode 'name' cannot be empty");
    }

    /// `i64` in, not `i32`/`i16`. The whole point of the wide crossing is that a
    /// head hands over the value it received rather than pre-truncating it: the
    /// portable head's `GetInt32()` throws `FormatException` on this input today
    /// and nothing catches it.
    #[test]
    fn an_out_of_int32_sort_order_is_an_ordinary_failure() {
        for order in [99_999_999_999_i64, i64::MAX, i64::MIN, -99_999_999_999] {
            let failure = validate_mode(&ModeValidationInput {
                sort_order: Some(order),
                ..ModeValidationInput::new(ModeOperation::Patch)
            })
            .expect("out of Int16 range");
            assert_eq!(failure.http_status(), 200);
            assert_eq!(failure.code, LocalApiErrorCode::InvalidRequest);
        }
    }

    #[test]
    fn the_comparison_key_trims_then_lowercases() {
        assert_eq!(
            mode_name_comparison_key("  Work Mode  ").as_deref(),
            Some("work mode")
        );
        assert_eq!(mode_name_comparison_key("HYPER").as_deref(), Some("hyper"));
        // Unicode whitespace, not just ASCII: a non-breaking space and an
        // ideographic space are both `char::is_whitespace`.
        assert_eq!(
            mode_name_comparison_key("\u{00A0}\u{3000}Mail\t\n").as_deref(),
            Some("mail")
        );
        // Full Unicode lowering, which is what makes this not
        // `OrdinalIgnoreCase`.
        assert_eq!(
            mode_name_comparison_key("STRASSE").as_deref(),
            Some("strasse")
        );
        assert_eq!(mode_name_comparison_key("İ").as_deref(), Some("i\u{307}"));

        for empty in ["", "   ", "\t\n", "\u{00A0}", "\u{3000}\u{2009}"] {
            assert_eq!(mode_name_comparison_key(empty), None, "{empty:?}");
        }

        // NFC is NOT applied here — it stays macOS's pre-step. Do not "fix"
        // this by adding `unicode-normalization`; see the module docs.
        assert_ne!(
            mode_name_comparison_key("Cafe\u{301}"),
            mode_name_comparison_key("Café")
        );
    }

    #[test]
    fn a_collision_ignores_case_and_boundary_whitespace() {
        let existing = names(&["Work", "  Mail  ", "note"]);
        assert!(mode_name_conflict("work", &existing));
        assert!(mode_name_conflict("WORK", &existing));
        assert!(mode_name_conflict("  work  ", &existing));
        assert!(mode_name_conflict("mail", &existing));
        assert!(mode_name_conflict("NOTE", &existing));
        assert!(!mode_name_conflict("meeting", &existing));
        assert!(!mode_name_conflict("wor k", &existing));
        assert!(!mode_name_conflict("work2", &existing));

        // An empty candidate is validate_mode's failure, not a collision — even
        // against a stored name that is itself blank.
        assert!(!mode_name_conflict("   ", &existing));
        assert!(!mode_name_conflict("", &names(&["   "])));
        // A blank stored name never swallows a real candidate either.
        assert!(!mode_name_conflict("work", &names(&["  ", ""])));
        assert!(!mode_name_conflict("anything", &[]));
    }

    /// The message is the two-head one, and the hint follows their split by
    /// operation: present on create, absent on patch.
    #[test]
    fn the_taken_failure_is_the_two_head_response() {
        let create = mode_name_taken_failure("Work", ModeOperation::Create);
        assert_eq!(create.http_status(), 200);
        assert_eq!(create.code, LocalApiErrorCode::ModeNameTaken);
        assert_eq!(create.message, "A mode named 'Work' already exists");
        assert_eq!(
            create.hint.as_deref(),
            Some("Choose a different name or PATCH the existing mode instead.")
        );

        let patch = mode_name_taken_failure("Work", ModeOperation::Patch);
        assert_eq!(patch.message, create.message);
        assert_eq!(patch.hint, None);

        // The name goes on the wire inside a JSON string, and a mode name is
        // caller-chosen, so it has to survive the encoder rather than break out
        // of it.
        assert_eq!(
            mode_name_taken_failure("a\"b", ModeOperation::Patch).to_json(),
            r#"{"ok":false,"error":{"code":"MODE_NAME_TAKEN","message":"A mode named 'a\"b' already exists"}}"#
        );
    }
}
