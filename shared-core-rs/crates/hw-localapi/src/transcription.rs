//! `POST /transcribe`'s failure table: which of the closed fourteen a
//! transcription failure carries, what the message says, and what the hint
//! tells the user to do.
//!
//! Issue #356 item 4. Three heads map their own transcription error type onto a
//! `(code, message, hint)` triple today:
//!
//! * macOS — `LocalAPIResponder.mapTranscriptionError`
//!   (`app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIErrors.swift`), over
//!   `TranscriptionError`.
//! * Windows — `LocalApiResponder.MapTranscriptionException`
//!   (`app/windows/HyperWhisper/Services/LocalApi/LocalApiErrors.cs`), over
//!   `TranscriptionErrorCode`, with every message coming from
//!   `TranscriptionException.GetUserMessage`.
//! * the portable head — almost nothing. `PortableTranscriptionErrorCode` has
//!   four cases and `ApplicationLocalApiBackend` throws all four away, so every
//!   transcription failure on Linux reaches the wire as one fixed
//!   `ENGINE_UNAVAILABLE` string.
//!
//! **The codes already agree. The messages and the hints do not**, and the hint
//! is the part a user reads. This module is the one table.
//!
//! # It adds no error code
//!
//! [`TranscriptionFailureReason`] is an *input* enum — the union of the three
//! heads' own error types — and every one of its variants maps onto a
//! [`LocalApiErrorCode`] that is already one of the closed fourteen. Nothing
//! here can widen that set; there is no `Other` variant and no string code.
//! `INTERNAL_ERROR` stays declared and unused in every head, exactly as #356
//! says.
//!
//! Every failure this module returns is a **business** failure: HTTP 200. That
//! is what macOS and Windows already send for all of these, and it is the rule
//! `failure.rs` writes down.
//!
//! # A function, not a static table
//!
//! Both real tables interpolate at runtime, so a constant table could not hold
//! them: macOS puts the provider name, a byte limit, a reason string, a network
//! detail, an HTTP status, a provider message and a model name into its
//! strings; Windows puts `ProviderName` and `RetryAfterSeconds` into the
//! message *and* the hint. [`TranscriptionFailureParams`] carries those slots
//! and [`map_transcription_error`] renders them.
//!
//! An absent slot is never rendered as an empty hole. Every arm has a
//! no-slot wording, and a slot that is present but blank counts as absent —
//! `provider: Some("")` reads the same as `None`.
//!
//! # Three hints stay the head's, because they name a product surface
//!
//! macOS says `"Add the API key in Settings → API Keys."` and Windows says
//! `"Add the API key in the Model Library API keys manager."`. Those are the
//! same semantic slot pointing at two different menus, and no string this crate
//! picks is right on both. So [`TranscriptionFailureParams::hint`] is a
//! caller-supplied slot, used for exactly the three reasons
//! [`TranscriptionFailureReason::takes_platform_hint`] names —
//! [`TranscriptionFailureReason::ApiKeyMissing`],
//! [`TranscriptionFailureReason::ApiKeyInvalid`] and
//! [`TranscriptionFailureReason::CloudAccountRequired`]. For every other reason
//! the hint is this table's and `params.hint` is **ignored**; a head that wants
//! to say something else there is drifting again, which is what item 4 exists
//! to stop.
//!
//! The precedent is in the same crate: [`crate::unauthorized`] takes its hint as
//! a parameter because it names the platform's own discovery-file path.
//!
//! # Which wording won, row by row
//!
//! macOS's is the base. Two macOS tests
//! (`HyperWhisperCloudEntitlementTests.swift`) call `mapTranscriptionError`
//! directly and assert on its literal strings — `"invalid or expired"`,
//! `"account key"`, `"temporarily blocked"`, and the two `Settings → …`
//! destinations. Windows and the portable head have **zero** coverage of their
//! tables' strings, so macOS's is the only wording under test and is the
//! sensible base. Where macOS has no arm at all, the wording is Windows's. The
//! rows that are *not* macOS's, and why:
//!
//! | reason | wording taken from | why |
//! |---|---|---|
//! | [`ModelFilesMissing`](TranscriptionFailureReason::ModelFilesMissing) | Windows | macOS has no such case; the model name moves into a slot so the message is not ONNX-specific |
//! | [`Cancelled`](TranscriptionFailureReason::Cancelled) | Windows | macOS has no cancellation case; the portable head has one and discards it |
//! | [`QuotaExceeded`](TranscriptionFailureReason::QuotaExceeded) | Windows | macOS's `.quotaExceeded` has no arm and falls through to the generic `TRANSCRIPTION_FAILED` |
//! | [`EngineStartFailed`](TranscriptionFailureReason::EngineStartFailed), [`EngineCrashed`](TranscriptionFailureReason::EngineCrashed), [`EngineTimeout`](TranscriptionFailureReason::EngineTimeout) | Windows | Parakeet-daemon cases macOS does not have; `"the Parakeet transcription engine"` becomes the provider slot so the row is engine-agnostic |
//! | [`AudioFileTooLarge`](TranscriptionFailureReason::AudioFileTooLarge) hint | Windows | macOS sends no hint; Windows's message ends in the actionable half, which belongs in the hint |
//! | [`EngineUnavailable`](TranscriptionFailureReason::EngineUnavailable) hint | Windows | same, and generalised from "use local transcription" because this reason covers local engines too |
//! | [`RateLimited`](TranscriptionFailureReason::RateLimited) hint | both | Windows's `"Retry after N seconds."` when the provider sent `Retry-After`, macOS's `"Try again in a moment."` when it did not |
//!
//! Three macOS strings are deliberately **not** copied byte for byte, and each
//! is a wart rather than a wording choice: `.providerNotAvailable` with no
//! provider renders `"engine is unavailable: Not available"` (lower-case
//! sentence start, and a reason that says nothing); `.invalidResponse` with no
//! detail renders a trailing `": "`; and `.audioFileTooLarge`'s message is
//! unreachable without the byte limit it interpolates. Each gets a no-slot
//! wording here instead.
//!
//! # What is deliberately not here
//!
//! Roughly 125 *other* sites across the three heads build a `(code, message,
//! hint)` triple inline in endpoint validation — a missing `path`, a mode id
//! that does not parse, a file outside the audio folders. #356 names three
//! tables, not the whole surface. Centralising those is a different and much
//! larger change, and it is the one that would need the Full Disk Access /
//! recording-folders hints this module does not carry.

use crate::failure::{Failure, LocalApiErrorCode};

/// Why a transcription failed, as the wire sees it.
///
/// The union of the three heads' own error types, and nothing more:
///
/// * macOS `TranscriptionError` (24 cases). Six of them —
///   `.maxRetriesExceeded`, `.streamingInterrupted`, `.busy`,
///   `.localRuntimeUnavailable`, `.insufficientCredits` and any case added
///   later — have no arm in `mapTranscriptionError` today and reach the wire
///   through its `default:` as `TRANSCRIPTION_FAILED` plus
///   `localizedDescription`. They map onto [`Self::TranscriptionFailed`] with
///   that text in [`TranscriptionFailureParams::detail`], which is the same
///   answer.
/// * Windows `TranscriptionErrorCode` (19 cases, `Unknown` included).
/// * the portable head's `PortableTranscriptionErrorCode` (4 cases:
///   `InvalidRequest`, `BackendUnavailable`, `TranscriptionFailed`,
///   `Cancelled`). All four fold onto reasons the other two heads already
///   produce, so including them adds no reason and no code.
///
/// Variants exist per *distinct wording*, not per source case: macOS's
/// `.invalidAudioFormat` and `.audioConversionFailed` already return the same
/// triple, so they share [`Self::AudioDecodeFailed`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TranscriptionFailureReason {
    /// The model the request selected is not downloaded.
    /// macOS `.modelNotDownloaded`; Windows `ModelNotLoaded`.
    ModelNotInstalled,
    /// The model is downloaded but its files are missing or incomplete.
    /// Windows `OnnxModelFileMissing`.
    ModelFilesMissing,
    /// The model exists but this build may not use it.
    /// macOS `.modelProtected`.
    ModelProtected,
    /// A BYOK provider has no API key configured.
    /// macOS `.apiKeyMissing`; Windows `ApiKeyMissing`.
    ApiKeyMissing,
    /// The configured API key was refused. macOS `.unauthorized`, except the
    /// HyperWhisper Cloud 403 (see [`Self::CloudRequestForbidden`]); Windows
    /// `Unauthorized`.
    ApiKeyInvalid,
    /// HyperWhisper Cloud was selected with no account key.
    /// macOS `.cloudAccountRequired`; Windows `CloudAccountRequired`.
    CloudAccountRequired,
    /// HyperWhisper Cloud answered 403, which is its abuse guard and not a
    /// credential fault.
    ///
    /// Its own reason because it is the one place a refusal must **not** be
    /// reported as a key problem: `transcribe`, `post-process`, `usage` and
    /// `assistant` answer 403 only for "your IP has been temporarily blocked",
    /// so `MISSING_API_KEY` would tell a client to rotate a valid key. macOS
    /// already special-cases it and a test pins it.
    CloudRequestForbidden,
    /// The audio file named by the request does not exist.
    /// macOS `.audioFileNotFound`; Windows `AudioFileNotFound`.
    AudioFileNotFound,
    /// The audio exists but will not decode.
    /// macOS `.invalidAudioFormat` / `.audioConversionFailed`; Windows
    /// `UnsupportedFormat`.
    AudioDecodeFailed,
    /// The audio is larger than the provider accepts.
    /// macOS `.audioFileTooLarge`; Windows `FileTooLarge`.
    AudioFileTooLarge,
    /// The request itself is malformed or self-contradictory.
    /// macOS `.invalidRequest`; Windows `InvalidRequest`; portable
    /// `InvalidRequest`.
    InvalidRequest,
    /// An upstream provider rate-limited the request.
    /// macOS `.rateLimited`; Windows `RateLimited`.
    RateLimited,
    /// The account is out of credit or over its plan quota.
    /// macOS `.quotaExceeded` / `.insufficientCredits`; Windows
    /// `QuotaExceeded`.
    QuotaExceeded,
    /// The transcription ran out of time.
    /// macOS `.timeout`.
    Timeout,
    /// The caller went away, or the app cancelled the run.
    /// Windows `Cancelled`; portable `Cancelled`.
    Cancelled,
    /// The engine or provider cannot serve the request at all.
    /// macOS `.providerNotAvailable`; Windows `ProviderUnavailable`; portable
    /// `BackendUnavailable`.
    EngineUnavailable,
    /// The network is down, or the request never reached the provider.
    /// macOS `.transientNetwork`; Windows `NetworkError`.
    NetworkUnavailable,
    /// A local engine process would not start.
    /// Windows `DaemonStartFailed`.
    EngineStartFailed,
    /// A local engine process died mid-run.
    /// Windows `DaemonCrashed`.
    EngineCrashed,
    /// A local engine process stopped answering.
    /// Windows `DaemonTimeout`.
    EngineTimeout,
    /// The resident local speech model was evicted under memory pressure and
    /// could not be reloaded. macOS `.localSpeechModelEvicted`.
    LocalModelEvicted,
    /// The provider answered, and the answer did not parse.
    /// macOS `.invalidResponse`.
    InvalidProviderResponse,
    /// The provider answered with an error status.
    /// macOS `.serverError`.
    ProviderServerError,
    /// The provider ran and reported no speech.
    /// macOS `.noSpeechDetected`; Windows `NoSpeechDetected`.
    NoSpeechDetected,
    /// Transcription ran and failed, with nothing more specific to say. The
    /// `default:` arm of both tables, and the portable head's
    /// `TranscriptionFailed`.
    TranscriptionFailed,
}

/// Every reason, in declaration order.
///
/// Next to the enum so "which codes can this table emit" is checkable rather
/// than asserted in a comment — the tests walk this array.
pub const ALL_TRANSCRIPTION_FAILURE_REASONS: [TranscriptionFailureReason; 25] = [
    TranscriptionFailureReason::ModelNotInstalled,
    TranscriptionFailureReason::ModelFilesMissing,
    TranscriptionFailureReason::ModelProtected,
    TranscriptionFailureReason::ApiKeyMissing,
    TranscriptionFailureReason::ApiKeyInvalid,
    TranscriptionFailureReason::CloudAccountRequired,
    TranscriptionFailureReason::CloudRequestForbidden,
    TranscriptionFailureReason::AudioFileNotFound,
    TranscriptionFailureReason::AudioDecodeFailed,
    TranscriptionFailureReason::AudioFileTooLarge,
    TranscriptionFailureReason::InvalidRequest,
    TranscriptionFailureReason::RateLimited,
    TranscriptionFailureReason::QuotaExceeded,
    TranscriptionFailureReason::Timeout,
    TranscriptionFailureReason::Cancelled,
    TranscriptionFailureReason::EngineUnavailable,
    TranscriptionFailureReason::NetworkUnavailable,
    TranscriptionFailureReason::EngineStartFailed,
    TranscriptionFailureReason::EngineCrashed,
    TranscriptionFailureReason::EngineTimeout,
    TranscriptionFailureReason::LocalModelEvicted,
    TranscriptionFailureReason::InvalidProviderResponse,
    TranscriptionFailureReason::ProviderServerError,
    TranscriptionFailureReason::NoSpeechDetected,
    TranscriptionFailureReason::TranscriptionFailed,
];

impl TranscriptionFailureReason {
    /// Which of the closed fourteen this reason carries.
    ///
    /// Every arm names a variant of [`LocalApiErrorCode`], so item 4 cannot add
    /// a code even by accident.
    #[must_use]
    pub fn code(self) -> LocalApiErrorCode {
        match self {
            TranscriptionFailureReason::ModelNotInstalled
            | TranscriptionFailureReason::ModelFilesMissing
            | TranscriptionFailureReason::ModelProtected => LocalApiErrorCode::ModelNotInstalled,
            TranscriptionFailureReason::ApiKeyMissing
            | TranscriptionFailureReason::ApiKeyInvalid
            | TranscriptionFailureReason::CloudAccountRequired => LocalApiErrorCode::MissingApiKey,
            TranscriptionFailureReason::CloudRequestForbidden
            | TranscriptionFailureReason::RateLimited
            | TranscriptionFailureReason::QuotaExceeded => LocalApiErrorCode::RateLimited,
            TranscriptionFailureReason::AudioFileNotFound => LocalApiErrorCode::FileNotFound,
            TranscriptionFailureReason::AudioDecodeFailed => LocalApiErrorCode::AudioDecodeFailed,
            TranscriptionFailureReason::AudioFileTooLarge
            | TranscriptionFailureReason::InvalidRequest => LocalApiErrorCode::InvalidRequest,
            TranscriptionFailureReason::Timeout | TranscriptionFailureReason::Cancelled => {
                LocalApiErrorCode::Timeout
            }
            TranscriptionFailureReason::EngineUnavailable
            | TranscriptionFailureReason::NetworkUnavailable
            | TranscriptionFailureReason::EngineStartFailed
            | TranscriptionFailureReason::EngineCrashed
            | TranscriptionFailureReason::EngineTimeout
            | TranscriptionFailureReason::LocalModelEvicted => LocalApiErrorCode::EngineUnavailable,
            TranscriptionFailureReason::InvalidProviderResponse
            | TranscriptionFailureReason::ProviderServerError
            | TranscriptionFailureReason::NoSpeechDetected
            | TranscriptionFailureReason::TranscriptionFailed => {
                LocalApiErrorCode::TranscriptionFailed
            }
        }
    }

    /// Whether this reason's hint is the caller's rather than this table's.
    ///
    /// True for the three reasons whose hint has to name a product surface —
    /// macOS's `Settings → API Keys` / `Settings → HyperWhisper Cloud` against
    /// Windows's `Model Library API keys manager`. For those,
    /// [`TranscriptionFailureParams::hint`] is the hint verbatim and `None`
    /// means no hint. For every other reason the hint is this table's and
    /// `params.hint` is ignored.
    ///
    /// Not exported across FFI: a head passes whatever hint it has and this
    /// decides. It is `pub` so the rule is readable and testable rather than
    /// buried in a `match`.
    #[must_use]
    pub fn takes_platform_hint(self) -> bool {
        matches!(
            self,
            TranscriptionFailureReason::ApiKeyMissing
                | TranscriptionFailureReason::ApiKeyInvalid
                | TranscriptionFailureReason::CloudAccountRequired
        )
    }
}

/// The runtime values a message or hint interpolates.
///
/// All seven are optional and every arm has a wording for the absent case, so a
/// head that knows nothing beyond the reason still gets a complete sentence.
/// A blank or whitespace-only string counts as absent.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TranscriptionFailureParams {
    /// The provider or engine display name — `"OpenAI"`, `"HyperWhisper
    /// Cloud"`, `"Parakeet"`.
    pub provider: Option<String>,
    /// Free text from the failure itself: macOS's `reason` / `details` /
    /// `message` associated values, Windows's `ex.Message`, the portable head's
    /// `PortableTranscriptionFailure.Message`.
    pub detail: Option<String>,
    /// The model involved, for the two model-shaped reasons.
    pub model: Option<String>,
    /// The provider's byte limit, for [`TranscriptionFailureReason::AudioFileTooLarge`].
    pub limit_bytes: Option<u64>,
    /// The HTTP status the provider returned, for
    /// [`TranscriptionFailureReason::ProviderServerError`].
    pub http_status: Option<u16>,
    /// The provider's `Retry-After`, in seconds, for
    /// [`TranscriptionFailureReason::RateLimited`].
    pub retry_after_seconds: Option<u32>,
    /// The head's own hint, used only by the three reasons
    /// [`TranscriptionFailureReason::takes_platform_hint`] names.
    pub hint: Option<String>,
}

/// The one `(code, message, hint)` table for a transcription failure. HTTP 200,
/// always.
///
/// See the module docs for which head's wording each row took, and for why the
/// hint is the caller's on exactly three of them.
#[must_use]
pub fn map_transcription_error(
    reason: TranscriptionFailureReason,
    params: &TranscriptionFailureParams,
) -> Failure {
    let failure = Failure::business(reason.code(), message_for(reason, params));
    match hint_for(reason, params) {
        Some(hint) => failure.with_hint(hint),
        None => failure,
    }
}

/// A slot's value, or `None` when it is absent or blank.
fn slot(value: &Option<String>) -> Option<&str> {
    value
        .as_deref()
        .map(str::trim)
        .filter(|text| !text.is_empty())
}

/// The message for a reason, with whichever slots the caller filled.
fn message_for(reason: TranscriptionFailureReason, params: &TranscriptionFailureParams) -> String {
    let provider = slot(&params.provider);
    let detail = slot(&params.detail);
    let model = slot(&params.model);

    match reason {
        // macOS, verbatim.
        TranscriptionFailureReason::ModelNotInstalled => {
            String::from("Required model is not installed.")
        }
        // Windows ("ONNX model files are missing."), with the model name in a
        // slot so the row is not ONNX-specific.
        TranscriptionFailureReason::ModelFilesMissing => match model {
            Some(model) => format!("Required files for {model} are missing or incomplete."),
            None => String::from("Required model files are missing or incomplete."),
        },
        // macOS, verbatim.
        TranscriptionFailureReason::ModelProtected => String::from("Required model is locked."),
        // macOS, verbatim, including the `this provider` fallback.
        TranscriptionFailureReason::ApiKeyMissing => {
            format!(
                "API key for {} is missing.",
                provider.unwrap_or("this provider")
            )
        }
        // macOS, verbatim. `HyperWhisperCloudEntitlementTests` asserts on
        // "invalid or expired".
        TranscriptionFailureReason::ApiKeyInvalid => {
            format!(
                "API key for {} is invalid or expired.",
                provider.unwrap_or("this provider")
            )
        }
        // macOS, verbatim. The same test asserts the message names an
        // "account key".
        TranscriptionFailureReason::CloudAccountRequired => {
            String::from("HyperWhisper Cloud requires an account key.")
        }
        // macOS, verbatim.
        TranscriptionFailureReason::CloudRequestForbidden => {
            String::from("HyperWhisper Cloud denied this request.")
        }
        // macOS, verbatim.
        TranscriptionFailureReason::AudioFileNotFound => String::from("Audio file not found."),
        // macOS, verbatim.
        TranscriptionFailureReason::AudioDecodeFailed => {
            String::from("Could not decode the audio file.")
        }
        // macOS's shape. macOS interpolates both slots unconditionally; the
        // three other arms are new, because a head that does not know the limit
        // must not send `"exceeds  limit ( bytes)"`.
        TranscriptionFailureReason::AudioFileTooLarge => match (provider, params.limit_bytes) {
            (Some(provider), Some(limit)) => {
                format!("Audio file exceeds {provider} limit ({limit} bytes).")
            }
            (None, Some(limit)) => {
                format!("Audio file exceeds the provider limit ({limit} bytes).")
            }
            (Some(provider), None) => format!("Audio file is too large for {provider}."),
            (None, None) => String::from("Audio file is too large."),
        },
        // macOS, verbatim, plus the detail the portable head's workflow
        // messages would otherwise lose.
        TranscriptionFailureReason::InvalidRequest => match detail {
            Some(detail) => format!("Invalid request: {detail}"),
            None => String::from("Invalid request."),
        },
        // macOS ("Provider rate-limited the request."), with the provider name
        // in the slot macOS leaves generic.
        TranscriptionFailureReason::RateLimited => {
            format!(
                "{} rate-limited the request.",
                provider.unwrap_or("Provider")
            )
        }
        // Windows's message, minus its trailing "Add credits …" half, which is
        // Windows's hint and belongs there.
        TranscriptionFailureReason::QuotaExceeded => {
            format!("{} quota exceeded.", provider.unwrap_or("Provider"))
        }
        // macOS, verbatim.
        TranscriptionFailureReason::Timeout => String::from("Transcription timed out."),
        // Windows, verbatim.
        TranscriptionFailureReason::Cancelled => String::from("Transcription was cancelled."),
        // macOS's shape. macOS's no-provider fallback renders
        // "engine is unavailable: Not available" — a lower-case sentence start
        // and a reason that says nothing; the three other arms replace it.
        TranscriptionFailureReason::EngineUnavailable => match (provider, detail) {
            (Some(provider), Some(detail)) => format!("{provider} is unavailable: {detail}"),
            (Some(provider), None) => format!("{provider} is unavailable."),
            (None, Some(detail)) => format!("The transcription engine is unavailable: {detail}"),
            (None, None) => String::from("The transcription engine is unavailable."),
        },
        // macOS, verbatim, including the `transient failure` fallback.
        TranscriptionFailureReason::NetworkUnavailable => {
            format!("Network error: {}", detail.unwrap_or("transient failure"))
        }
        // Windows, with "the Parakeet transcription engine" in the provider
        // slot so the row serves any engine.
        TranscriptionFailureReason::EngineStartFailed => {
            format!(
                "{} failed to start.",
                provider.unwrap_or("The transcription engine")
            )
        }
        // Windows, same slot treatment.
        TranscriptionFailureReason::EngineCrashed => {
            format!(
                "{} crashed during transcription.",
                provider.unwrap_or("The transcription engine")
            )
        }
        // Windows, same slot treatment.
        TranscriptionFailureReason::EngineTimeout => {
            format!(
                "{} timed out.",
                provider.unwrap_or("The transcription engine")
            )
        }
        // macOS, verbatim, including the `The local speech model` fallback.
        TranscriptionFailureReason::LocalModelEvicted => {
            format!(
                "{} was unloaded to free memory and could not be reloaded.",
                model.unwrap_or("The local speech model")
            )
        }
        // macOS, minus the trailing ": " it emits when the detail is nil.
        TranscriptionFailureReason::InvalidProviderResponse => match detail {
            Some(detail) => format!("Provider returned an invalid response: {detail}"),
            None => String::from("Provider returned an invalid response."),
        },
        // macOS's shape; the last three arms cover the slots macOS's throw site
        // always fills and another head's may not.
        TranscriptionFailureReason::ProviderServerError => match (params.http_status, detail) {
            (Some(status), Some(detail)) => format!("Provider error (HTTP {status}): {detail}"),
            (Some(status), None) => format!("Provider error (HTTP {status})."),
            (None, Some(detail)) => format!("Provider error: {detail}"),
            (None, None) => String::from("Provider error."),
        },
        // macOS, verbatim. Windows returns a *localized* string here
        // (`Loc.S("errors.noSpeechDetected")`); the wire is not localized, and
        // macOS's sibling arms are hardcoded English by design.
        TranscriptionFailureReason::NoSpeechDetected => {
            String::from("No speech detected in audio.")
        }
        // Both heads pass their own text here — `localizedDescription` on
        // macOS, `ex.Message` on Windows, the failure message on the portable
        // head — which is what the two `default:` arms already do.
        TranscriptionFailureReason::TranscriptionFailed => match detail {
            Some(detail) => String::from(detail),
            None => String::from("Transcription failed."),
        },
    }
}

/// The hint for a reason: the caller's for the three product-surface rows, this
/// table's for the rest.
fn hint_for(
    reason: TranscriptionFailureReason,
    params: &TranscriptionFailureParams,
) -> Option<String> {
    let hint = match reason {
        // The three product-surface rows: the head's hint verbatim, and no
        // hint at all when it has none. `takes_platform_hint` names the same
        // three, and a test walks every reason through both so they cannot
        // drift apart.
        TranscriptionFailureReason::ApiKeyMissing
        | TranscriptionFailureReason::ApiKeyInvalid
        | TranscriptionFailureReason::CloudAccountRequired => {
            return slot(&params.hint).map(String::from)
        }
        // macOS, verbatim.
        TranscriptionFailureReason::ModelNotInstalled => {
            "Install the model from the app's Library, or pick a different engine/model."
        }
        // Windows ("Re-download the ONNX model from the Model Library."),
        // pointed at the same surface as the row above so the two agree.
        TranscriptionFailureReason::ModelFilesMissing => {
            "Re-download the model from the app's Library."
        }
        // macOS, verbatim. The test asserts "temporarily blocked", and that the
        // hint names neither Settings destination.
        TranscriptionFailureReason::CloudRequestForbidden => {
            "Your network is temporarily blocked. Wait a few minutes, then try again."
        }
        // macOS, verbatim. Windows sends no hint here and gains one.
        TranscriptionFailureReason::AudioFileNotFound => {
            "Pass an absolute path the running app can read."
        }
        // macOS, verbatim. Windows's list omits flac, which both heads in fact
        // accept (`audio/flac` is in each head's content-type table).
        TranscriptionFailureReason::AudioDecodeFailed => {
            "Use a supported format (wav, m4a, mp3, flac)."
        }
        // The actionable half of Windows's message. Neither head sends a hint
        // for this today.
        TranscriptionFailureReason::AudioFileTooLarge => {
            "Try a shorter recording, or use a local engine."
        }
        // Windows's when the provider told us how long to wait, macOS's when it
        // did not. Both heads keep the hint they have.
        TranscriptionFailureReason::RateLimited => {
            return Some(match params.retry_after_seconds {
                Some(seconds) => format!("Retry after {seconds} seconds."),
                None => String::from("Try again in a moment."),
            })
        }
        // Windows, verbatim.
        TranscriptionFailureReason::QuotaExceeded => "Add credits in the provider's billing page.",
        // The actionable half of Windows's message, generalised: this reason
        // covers a local engine too, so "use local transcription" would be
        // wrong half the time.
        TranscriptionFailureReason::EngineUnavailable => {
            "Try again later, or pick a different engine."
        }
        // macOS, verbatim. Windows sends no hint here and gains one.
        TranscriptionFailureReason::NetworkUnavailable => "Check connectivity and retry.",
        // Windows ("Please restart the app.").
        TranscriptionFailureReason::EngineStartFailed => "Restart HyperWhisper and try again.",
        // Windows ("Please try again.").
        TranscriptionFailureReason::EngineCrashed => "Try again.",
        // Windows ("Please try a shorter recording.").
        TranscriptionFailureReason::EngineTimeout => "Try a shorter recording.",
        // macOS, verbatim.
        TranscriptionFailureReason::LocalModelEvicted => {
            "Close some apps to free memory and retry."
        }
        // No hint on either head, and nothing true to say: the caller already
        // has the detail in the message.
        TranscriptionFailureReason::ModelProtected
        | TranscriptionFailureReason::InvalidRequest
        | TranscriptionFailureReason::Timeout
        | TranscriptionFailureReason::Cancelled
        | TranscriptionFailureReason::InvalidProviderResponse
        | TranscriptionFailureReason::ProviderServerError
        | TranscriptionFailureReason::NoSpeechDetected
        | TranscriptionFailureReason::TranscriptionFailed => return None,
    };
    Some(String::from(hint))
}

#[cfg(test)]
mod tests {
    use super::{
        map_transcription_error, TranscriptionFailureParams, TranscriptionFailureReason,
        ALL_TRANSCRIPTION_FAILURE_REASONS,
    };
    use crate::failure::{LocalApiErrorCode, ALL_ERROR_CODES};

    fn params() -> TranscriptionFailureParams {
        TranscriptionFailureParams::default()
    }

    /// Every filled slot at once, so an arm that reads the wrong one is visible.
    fn full_params() -> TranscriptionFailureParams {
        TranscriptionFailureParams {
            provider: Some(String::from("OpenAI")),
            detail: Some(String::from("boom")),
            model: Some(String::from("whisper-large-v3")),
            limit_bytes: Some(26_214_400),
            http_status: Some(502),
            retry_after_seconds: Some(30),
            hint: Some(String::from("Add the API key in Settings → API Keys.")),
        }
    }

    /// The whole point of the reason enum: it is an *input* type over the
    /// closed fourteen, and cannot widen them.
    #[test]
    fn every_reason_maps_inside_the_closed_fourteen() {
        assert_eq!(ALL_TRANSCRIPTION_FAILURE_REASONS.len(), 25);
        for reason in ALL_TRANSCRIPTION_FAILURE_REASONS {
            let code = reason.code();
            assert!(
                ALL_ERROR_CODES.contains(&code),
                "{reason:?} left the closed set"
            );
            assert_eq!(
                LocalApiErrorCode::from_wire_value(code.wire_value()),
                Some(code)
            );
        }
    }

    /// A transcription failure is a business outcome. Both heads already answer
    /// 200 for all of these; the portable head's 400/500 answers are what
    /// phase 5 removes.
    #[test]
    fn every_reason_is_an_http_200_business_failure() {
        for reason in ALL_TRANSCRIPTION_FAILURE_REASONS {
            for parameters in [params(), full_params()] {
                let failure = map_transcription_error(reason, &parameters);
                assert_eq!(failure.http_status(), 200, "{reason:?}");
                assert_eq!(failure.code, reason.code(), "{reason:?}");
            }
        }
    }

    /// No arm may render an empty hole, leave a dangling separator, or start a
    /// sentence lower-case — the three shapes the native tables get wrong when
    /// an associated value is nil.
    #[test]
    fn no_message_or_hint_renders_an_empty_slot() {
        for reason in ALL_TRANSCRIPTION_FAILURE_REASONS {
            for (filled, parameters) in [(false, params()), (true, full_params())] {
                let failure = map_transcription_error(reason, &parameters);
                let message = &failure.message;
                assert!(!message.is_empty(), "{reason:?}");
                assert_eq!(message.trim(), message, "{reason:?}: {message}");
                assert!(!message.contains(": ."), "{reason:?}: {message}");
                assert!(!message.contains("  "), "{reason:?}: {message}");
                assert!(!message.ends_with(": "), "{reason:?}: {message}");
                assert!(!message.ends_with(':'), "{reason:?}: {message}");
                // Only this table's own wording is held to a capital: several
                // rows open with a caller-supplied slot — a provider name, a
                // model id, or the head's own error text — whose case is the
                // head's. With no slots filled, every row is this table's, and
                // macOS's `"engine is unavailable: …"` is the wart being
                // fixed.
                if !filled {
                    assert!(
                        message
                            .chars()
                            .next()
                            .is_some_and(|first| !first.is_lowercase()),
                        "{reason:?}: {message}"
                    );
                }
                if let Some(hint) = &failure.hint {
                    assert!(!hint.is_empty(), "{reason:?}");
                    assert_eq!(hint.trim(), hint, "{reason:?}: {hint}");
                    assert!(hint.ends_with('.'), "{reason:?}: {hint}");
                }
            }
        }
    }

    /// The hint slot is the head's on exactly three rows, and is ignored
    /// everywhere else. A head that could override any hint would put the
    /// wording back where item 4 found it.
    #[test]
    fn the_platform_hint_slot_passes_through_on_three_reasons_only() {
        let platform = [
            TranscriptionFailureReason::ApiKeyMissing,
            TranscriptionFailureReason::ApiKeyInvalid,
            TranscriptionFailureReason::CloudAccountRequired,
        ];
        // The predicate and the table must agree for every reason, or a head
        // would pass a hint that is silently dropped — or worse, keep one the
        // table meant to own.
        let head_hint = TranscriptionFailureParams {
            hint: Some(String::from("A hint only this head could write.")),
            ..TranscriptionFailureParams::default()
        };
        for reason in ALL_TRANSCRIPTION_FAILURE_REASONS {
            assert_eq!(
                reason.takes_platform_hint(),
                platform.contains(&reason),
                "{reason:?}"
            );
            let used = map_transcription_error(reason, &head_hint).hint.as_deref()
                == Some("A hint only this head could write.");
            assert_eq!(used, reason.takes_platform_hint(), "{reason:?}");
        }

        for reason in platform {
            let windows = TranscriptionFailureParams {
                hint: Some(String::from(
                    "Add the API key in the Model Library API keys manager.",
                )),
                ..TranscriptionFailureParams::default()
            };
            assert_eq!(
                map_transcription_error(reason, &windows).hint.as_deref(),
                Some("Add the API key in the Model Library API keys manager."),
                "{reason:?}"
            );
            // No hint from the head means no hint on the wire — this crate has
            // nothing true to put there.
            assert_eq!(map_transcription_error(reason, &params()).hint, None);
        }

        // Everywhere else the table's hint stands.
        let overridden = map_transcription_error(
            TranscriptionFailureReason::NetworkUnavailable,
            &full_params(),
        );
        assert_eq!(
            overridden.hint.as_deref(),
            Some("Check connectivity and retry.")
        );
    }

    /// A slot that is present but blank reads as absent, so a head that passes
    /// `""` instead of `null` still gets a sentence.
    #[test]
    fn a_blank_slot_reads_as_absent() {
        let blank = TranscriptionFailureParams {
            provider: Some(String::from("   ")),
            detail: Some(String::new()),
            model: Some(String::from("\t")),
            hint: Some(String::from(" ")),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::ApiKeyMissing, &blank).message,
            "API key for this provider is missing."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::ApiKeyMissing, &blank).hint,
            None
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::EngineUnavailable, &blank).message,
            "The transcription engine is unavailable."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::TranscriptionFailed, &blank)
                .message,
            "Transcription failed."
        );
    }

    /// Every interpolation slot, rendered.
    #[test]
    fn every_slot_renders_where_its_row_uses_it() {
        let provider = TranscriptionFailureParams {
            provider: Some(String::from("ElevenLabs")),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::ApiKeyMissing, &provider).message,
            "API key for ElevenLabs is missing."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::RateLimited, &provider).message,
            "ElevenLabs rate-limited the request."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::QuotaExceeded, &provider).message,
            "ElevenLabs quota exceeded."
        );

        let daemon = TranscriptionFailureParams {
            provider: Some(String::from("Parakeet")),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::EngineStartFailed, &daemon).message,
            "Parakeet failed to start."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::EngineCrashed, &daemon).message,
            "Parakeet crashed during transcription."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::EngineTimeout, &daemon).message,
            "Parakeet timed out."
        );

        let too_large = TranscriptionFailureParams {
            provider: Some(String::from("OpenAI")),
            limit_bytes: Some(26_214_400),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::AudioFileTooLarge, &too_large)
                .message,
            "Audio file exceeds OpenAI limit (26214400 bytes)."
        );

        let detailed = TranscriptionFailureParams {
            provider: Some(String::from("Whisper")),
            detail: Some(String::from("model failed to load")),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::EngineUnavailable, &detailed)
                .message,
            "Whisper is unavailable: model failed to load"
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::NetworkUnavailable, &detailed)
                .message,
            "Network error: model failed to load"
        );
        assert_eq!(
            map_transcription_error(
                TranscriptionFailureReason::InvalidProviderResponse,
                &detailed
            )
            .message,
            "Provider returned an invalid response: model failed to load"
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::InvalidRequest, &detailed).message,
            "Invalid request: model failed to load"
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::TranscriptionFailed, &detailed)
                .message,
            "model failed to load"
        );

        let server = TranscriptionFailureParams {
            detail: Some(String::from("upstream refused")),
            http_status: Some(502),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::ProviderServerError, &server)
                .message,
            "Provider error (HTTP 502): upstream refused"
        );

        let evicted = TranscriptionFailureParams {
            model: Some(String::from("ggml-large-v3-turbo")),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::LocalModelEvicted, &evicted)
                .message,
            "ggml-large-v3-turbo was unloaded to free memory and could not be reloaded."
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::ModelFilesMissing, &evicted)
                .message,
            "Required files for ggml-large-v3-turbo are missing or incomplete."
        );

        let retry = TranscriptionFailureParams {
            retry_after_seconds: Some(42),
            ..TranscriptionFailureParams::default()
        };
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::RateLimited, &retry)
                .hint
                .as_deref(),
            Some("Retry after 42 seconds.")
        );
        assert_eq!(
            map_transcription_error(TranscriptionFailureReason::RateLimited, &params())
                .hint
                .as_deref(),
            Some("Try again in a moment.")
        );
    }

    /// The strings `HyperWhisperCloudEntitlementTests.swift:613-663` asserts on
    /// by substring. macOS is the only head whose wording is under test, which
    /// is why it is the reconciliation base — and these three are the ones a
    /// reworded row would break in CI rather than in the field.
    #[test]
    fn the_macos_tested_substrings_survive_the_reconciliation() {
        let cloud = TranscriptionFailureParams {
            provider: Some(String::from("HyperWhisper Cloud")),
            hint: Some(String::from(
                "Update the API key in Settings → HyperWhisper Cloud.",
            )),
            ..TranscriptionFailureParams::default()
        };
        let invalid = map_transcription_error(TranscriptionFailureReason::ApiKeyInvalid, &cloud);
        assert!(invalid.message.contains("invalid or expired"));
        assert_eq!(invalid.code, LocalApiErrorCode::MissingApiKey);
        assert_eq!(
            invalid.hint.as_deref(),
            Some("Update the API key in Settings → HyperWhisper Cloud.")
        );

        let account = map_transcription_error(
            TranscriptionFailureReason::CloudAccountRequired,
            &TranscriptionFailureParams {
                hint: Some(String::from(
                    "Add your account key in Settings → HyperWhisper Cloud.",
                )),
                ..TranscriptionFailureParams::default()
            },
        );
        assert!(account.message.contains("account key"));
        assert_eq!(account.code, LocalApiErrorCode::MissingApiKey);
        assert!(account.hint.is_some());

        // The Cloud 403 is the abuse guard: RATE_LIMITED, no "invalid or
        // expired", and a hint that names no Settings destination.
        let forbidden =
            map_transcription_error(TranscriptionFailureReason::CloudRequestForbidden, &cloud);
        assert_eq!(forbidden.code, LocalApiErrorCode::RateLimited);
        assert!(!forbidden.message.contains("invalid or expired"));
        let hint = forbidden.hint.unwrap_or_default();
        assert!(hint.contains("temporarily blocked"));
        assert!(!hint.contains("Settings →"));
    }

    /// The four codes the portable head's own enum has to reach, and the four
    /// reasons phase 5 routes them through. `BackendUnavailable` keeps
    /// `ENGINE_UNAVAILABLE`, which is what `/recording/toggle`'s existing test
    /// asserts.
    #[test]
    fn the_portable_heads_four_cases_have_somewhere_to_land() {
        for (reason, code) in [
            (
                TranscriptionFailureReason::InvalidRequest,
                LocalApiErrorCode::InvalidRequest,
            ),
            (
                TranscriptionFailureReason::EngineUnavailable,
                LocalApiErrorCode::EngineUnavailable,
            ),
            (
                TranscriptionFailureReason::TranscriptionFailed,
                LocalApiErrorCode::TranscriptionFailed,
            ),
            (
                TranscriptionFailureReason::Cancelled,
                LocalApiErrorCode::Timeout,
            ),
        ] {
            assert_eq!(reason.code(), code, "{reason:?}");
        }
    }
}
