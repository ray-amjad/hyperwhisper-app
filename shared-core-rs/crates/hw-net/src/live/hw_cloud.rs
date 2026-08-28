//! HyperWhisper Cloud's own `/ws/streaming-deepgram` relay — the DEFAULT
//! provider, and the only one that bills.
//!
//! Two consequences of it being ours:
//!
//! - **`credits_used` is billing data.** It arrives once, on `session_complete`,
//!   after the stop frame. Everything about the stop path below exists to not
//!   lose it.
//! - **The host is configurable.** `LiveConfig::base_url` points a build at a
//!   different backend; macOS's `#if DEBUG` build talks to staging. A hardcoded
//!   production host here would silently re-point a debug build at production
//!   and bill real credits against a developer's key.
//!
//! Auth is a query parameter, not a header, because a browser websocket client
//! cannot set headers — the same reason Deepgram uses a subprotocol.

use super::config::{
    bool_field, num_field, str_field, text_field, LiveConfig, LiveConnect, LiveError, LiveEvent,
    Query, StopStep,
};
use super::AudioFraming;
use crate::helpers::keyword_boost_terms;

/// The production relay. Matches Windows' `StreamingEndpoint` and
/// `shared-dotnet`'s literal.
const DEFAULT_BASE_URL: &str = "wss://transcribe-prod-v2.hyperwhisper.com";

/// The `sttProvider` whose route reproduces the endpoint this path hardcoded
/// (`/ws/streaming-deepgram`) before the live tier picker existed. Anything
/// unrecognised lands back here rather than deriving a path the relay 404s.
pub(super) const DEEPGRAM_STT: &str = "deepgram";

/// The catalog entry id that resolves to [`DEEPGRAM_STT`].
const DEFAULT_CLOUD_TIER: &str = "deepgramNova3";

/// The embedded catalog, parsed once.
///
/// `connect` runs on every socket and every reconnect; re-parsing the whole
/// `cloud-stt-catalog.json` there would be pure waste. A parse failure is not
/// reachable in a shipped build — the JSON is compile-time embedded and
/// `hw-catalog`'s own suite parses it — and the `None` it leaves behind is
/// handled the same way an unknown tier is: fall back to Deepgram.
fn catalog() -> Option<&'static hw_catalog::CloudSttCatalog> {
    static CATALOG: std::sync::OnceLock<Option<hw_catalog::CloudSttCatalog>> =
        std::sync::OnceLock::new();
    CATALOG
        .get_or_init(|| hw_catalog::CloudSttCatalog::embedded().ok())
        .as_ref()
}

/// Which upstream vendor the relay will use for `cloud_tier`.
///
/// The route is **derived**, never a table: `/ws/streaming-{sttProvider}`, where
/// `sttProvider` comes from the catalog entry the tier names. `deepgramNova3`
/// gives `/ws/streaming-deepgram`, byte-identical to the literal this replaced,
/// so every installed client keeps working; `geminiTranscribe` gives
/// `/ws/streaming-gemini-transcribe`.
///
/// A tier the catalog does not list — or lists but cannot serve live — falls
/// back to Deepgram. Deriving a path from an arbitrary string would let a stale
/// or hand-edited setting point the socket at a route the backend does not
/// serve, which fails as an opaque upgrade error rather than as a wrong-looking
/// transcript.
pub(super) fn stt_provider_for_tier(cloud_tier: Option<&str>) -> &'static str {
    let Some(catalog) = catalog() else {
        return DEEPGRAM_STT;
    };
    let requested = cloud_tier.map(str::trim).filter(|t| !t.is_empty());
    let tier = requested
        .filter(|t| {
            catalog
                .streaming_cloud_tier_entries()
                .any(|entry| entry.id.eq_ignore_ascii_case(t))
        })
        .unwrap_or(DEFAULT_CLOUD_TIER);
    // The catalog outlives the process, so the borrow is 'static; an entry with
    // no `sttProvider` is a catalog gap and takes the same fallback.
    catalog.stt_provider(tier).unwrap_or(DEEPGRAM_STT)
}

/// How long to wait for `session_complete` after sending `stop`.
///
/// Ten seconds, and a wait on the *event*, so a prompt completion returns
/// immediately. macOS instead hard-closes 500 ms after `stop` and therefore
/// loses a late `session_complete` — with the `credits_used` it carries. The
/// .NET behaviour wins: dropping billing data to save a few hundred
/// milliseconds on the rare slow completion is not a trade worth making, and
/// the wait is on an event so the common case costs nothing.
const SESSION_COMPLETE_TIMEOUT_MS: u64 = 10_000;

/// Vocabulary terms go out as one comma-joined `vocabulary=` parameter.
const MAX_TERMS: usize = 100;

pub(super) fn connect(config: &LiveConfig) -> Result<LiveConnect, LiveError> {
    let mut query = Query::default();
    // License key first, device id as the trial fallback, and neither is a
    // hard failure — the relay has nothing to charge against.
    if let Some(license_key) = super::config::present(&config.license_key) {
        query.push("license_key", license_key);
    } else if let Some(device_id) = super::config::present(&config.device_id) {
        query.push("device_id", device_id);
    } else {
        return Err(LiveError::MissingCredential);
    }

    // Verbatim, like Deepgram: this endpoint relays the tag straight through, so
    // truncating `zh-TW` here would ask Deepgram for Simplified Chinese. The
    // relay lowercases what it receives before forwarding, so this does not.
    let language = super::language_tag(config.language.as_deref());
    if let Some(language) = &language {
        query.push("language", language);
    }

    // Withholding vocabulary under auto-detect is a DEEPGRAM constraint (Nova-3
    // silently drops keyterms with no language), not a HyperWhisper Cloud one —
    // so it is gated on the tier's upstream, not on the provider. Gemini accepts
    // `custom_vocabulary` under auto-detect, and vocabulary is the whole reason
    // to pick that tier; applying Deepgram's rule to it would delete the feature
    // for auto-detect users.
    let stt_provider = stt_provider_for_tier(config.cloud_tier.as_deref());
    if language.is_some() || stt_provider != DEEPGRAM_STT {
        let terms = keyword_boost_terms(&config.vocabulary, Some(MAX_TERMS));
        if !terms.is_empty() {
            query.push("vocabulary", &terms.join(", "));
        }
    }

    Ok(LiveConnect {
        url: format!(
            "{}/ws/streaming-{stt_provider}{}",
            base_url(config),
            query.suffix()
        ),
        // No headers. The client-identity headers the heads add
        // (`X-HyperWhisper-Platform`, `X-HyperWhisper-Version`) are the
        // platform's to know, not the core's — see `LiveConnect::headers`.
        headers: Vec::new(),
        subprotocols: Vec::new(),
        sample_rate: super::required_sample_rate(super::LiveProvider::HyperWhisperCloud),
        framing: AudioFraming::Binary,
        start_frames: Vec::new(),
        session_starts_on_open: false,
    })
}

/// The configured base URL with an HTTP scheme mapped to its websocket
/// equivalent, trailing slash removed.
///
/// The heads store one backend URL and use it for both the REST and the
/// websocket path, so `https://…` is the shape that arrives here. macOS does
/// this same substitution inline.
fn base_url(config: &LiveConfig) -> String {
    let raw = match super::config::present(&config.base_url) {
        Some(raw) => raw.trim(),
        None => return DEFAULT_BASE_URL.to_string(),
    };
    let trimmed = raw.trim_end_matches('/');
    if let Some(rest) = trimmed.strip_prefix("https://") {
        format!("wss://{rest}")
    } else if let Some(rest) = trimmed.strip_prefix("http://") {
        format!("ws://{rest}")
    } else {
        trimmed.to_string()
    }
}

pub(super) fn stop_sequence() -> Vec<StopStep> {
    vec![
        StopStep::SendText {
            text: r#"{"type":"stop"}"#.to_string(),
        },
        StopStep::WaitForSessionComplete {
            timeout_ms: SESSION_COMPLETE_TIMEOUT_MS,
        },
        StopStep::Close,
    ]
}

pub(super) fn parse(root: &serde_json::Value) -> LiveEvent {
    match str_field(root, "type") {
        Some("ready") => LiveEvent::SessionStarted {
            session_id: str_field(root, "sessionId").map(str::to_string),
        },
        Some("transcript") => match text_field(root, "text") {
            Some(text) if bool_field(root, "is_final") == Some(true) => {
                LiveEvent::FinalTranscript {
                    text: text.to_string(),
                }
            }
            Some(text) => LiveEvent::PartialTranscript {
                text: text.to_string(),
            },
            None => LiveEvent::Ignore,
        },
        Some("session_complete") => LiveEvent::SessionComplete {
            duration_seconds: num_field(root, "duration_seconds"),
            credits_used: num_field(root, "credits_used"),
        },
        Some("error") => LiveEvent::Error {
            message: super::config::error_message(root, "Unknown server error"),
            kind: None,
        },
        // The only provider that warns.
        //
        // NOT COVERED: `remaining_seconds`. Windows DID read it — its client
        // appended "(N seconds remaining)" to the warning text (see
        // `git show main:…/StreamingTranscriptionClient.cs:649`) — and this
        // module drops it, so that suffix is gone on Windows. The field is
        // unreachable today: no route in `hyperwhisper-cloud/src` emits
        // `remaining_seconds` on any frame, and the wording the relay puts in
        // `message` carries the same information. Carrying it again means
        // widening `LiveEvent::Warning`, which is a follow-up, not a silent
        // omission.
        Some("warning") => LiveEvent::Warning {
            message: text_field(root, "message")
                .unwrap_or("Server warning")
                .to_string(),
        },
        _ => LiveEvent::Ignore,
    }
}
