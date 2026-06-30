//! Per-provider health probes (WP-B5). Builds a cheap, key-light request to check
//! whether a provider endpoint is reachable and the configured key is valid, and
//! parses the response into a [`ProviderHealth`] verdict. Plain Rust, sans-I/O:
//! the platform performs the request; this module only describes it and grades
//! the result. Deterministic and golden-testable.
//!
//! ## Endpoint table (per provider)
//!
//! | Provider          | Method | Endpoint                                            | Auth                          |
//! |-------------------|--------|----------------------------------------------------|-------------------------------|
//! | OpenAI            | GET    | `https://api.openai.com/v1/models`                 | `Authorization: Bearer`       |
//! | Groq              | GET    | `https://api.groq.com/openai/v1/models`            | `Authorization: Bearer`       |
//! | Deepgram          | GET    | `https://api.deepgram.com/v1/projects`             | `Authorization: Token`        |
//! | AssemblyAI        | GET    | `https://api.assemblyai.com/v2/transcript?limit=1` | `Authorization: <key>` (raw)  |
//! | ElevenLabs        | GET    | `https://api.elevenlabs.io/v1/models`              | `xi-api-key: <key>`           |
//! | Mistral           | GET    | `https://api.mistral.ai/v1/models`                 | `Authorization: Bearer`       |
//! | Soniox            | GET    | `https://api.soniox.com/v1/models`                 | `Authorization: Bearer`       |
//! | Gemini            | GET    | `https://generativelanguage.googleapis.com/v1beta/models?key=<key>` | query `key` |
//! | Grok              | GET    | `https://api.x.ai/v1/models`                       | `Authorization: Bearer`       |
//! | HyperWhisperCloud | GET    | HW Cloud `/health` (base_url)                       | none (always reachable)       |
//! | AzureMai          | GET    | HW Cloud `/health` (routed)                         | none                          |
//! | GoogleChirp       | GET    | HW Cloud `/health` (routed)                         | none                          |
//!
//! PARITY: the vendor health endpoints + auth schemes come from macOS
//! `CloudProviderHealthManager.swift` / each provider's `healthCheck(...)` and
//! Windows `CloudProviderHealthService.cs`'s `TranscriptionEndpoints` table.
//!
//! ## Unification choices (documented divergences)
//!
//! - **ElevenLabs probes STT capability via `POST /v1/speech-to-text`** (mirrors
//!   the old macOS probe). A `GET /v1/models` only proves the key can list models —
//!   a models-list-only key shows green even though it cannot reach STT. We instead
//!   POST to the STT endpoint with `xi-api-key` and an empty body: a valid key that
//!   can reach STT is rejected for the *body* (400/422, "bad request"), which we
//!   treat as **healthy**; 401/403 mean the key is unauthorized. See
//!   [`parse_health_response`]'s ElevenLabs branch.
//! - **Gemini & Grok treat HTTP 400 as unauthorized.** Both vendors return 400 for
//!   an invalid key on the models endpoint (macOS + Windows both special-case
//!   this). [`parse_health_response`] folds 400 into "not healthy" for these two.
//! - **Routed/HW-Cloud providers** (`HyperWhisperCloud`, `AzureMai`,
//!   `GoogleChirp`) need no API key and are treated as always reachable on both
//!   platforms (`performHealthCheck` returns `.healthy` immediately). We still
//!   build a real `GET <base>/health` request for callers that want to probe the
//!   HW Cloud edge; [`parse_health_response`] grades it by 2xx like the others.

use crate::contract::{Header, HttpMethod, HttpRequest, HttpResponse, Provider, ProviderHealth};

/// Default HyperWhisper Cloud base URL used for routed providers' health probe
/// when no `base_url` override is supplied. The real platform passes the base via
/// `TranscribeParams.base_url`; health probes are parameterless, so a sensible
/// default lives here and can be overridden with [`build_health_request_with_base`].
pub const HW_CLOUD_HEALTH_DEFAULT: &str = "https://api.hyperwhisper.com/health";

/// The vendor health endpoint + auth scheme for a direct (non-routed) provider.
struct HealthEndpoint {
    method: HttpMethod,
    url: &'static str,
    auth: HealthAuth,
}

/// How a direct provider's health probe authenticates. (Routed / HW-Cloud
/// providers carry no key and are built by `build_routed`, so there is no
/// "no-auth" variant here.)
enum HealthAuth {
    /// `Authorization: Bearer <key>`.
    Bearer,
    /// `Authorization: Token <key>` (Deepgram).
    Token,
    /// `Authorization: <key>` (raw, no scheme — AssemblyAI).
    Raw,
    /// `xi-api-key: <key>` (ElevenLabs).
    XiApiKey,
    /// `?key=<key>` query param (Gemini).
    QueryKey,
}

/// Resolve the vendor health endpoint for a direct provider. Routed providers
/// (HW Cloud / Azure / Google) return `None` — they probe the HW Cloud base.
fn endpoint(provider: Provider) -> Option<HealthEndpoint> {
    // Default health probe is a cheap GET; ElevenLabs is the lone exception (POST
    // to the STT endpoint — see the module note + `parse_health_response`).
    let e = |url, auth| {
        Some(HealthEndpoint {
            method: HttpMethod::Get,
            url,
            auth,
        })
    };
    match provider {
        Provider::Openai => e("https://api.openai.com/v1/models", HealthAuth::Bearer),
        Provider::Groq => e("https://api.groq.com/openai/v1/models", HealthAuth::Bearer),
        Provider::Deepgram => e("https://api.deepgram.com/v1/projects", HealthAuth::Token),
        Provider::Assemblyai => e(
            "https://api.assemblyai.com/v2/transcript?limit=1",
            HealthAuth::Raw,
        ),
        // ElevenLabs: POST to STT (an empty body is rejected 400/422 for a valid
        // key — graded healthy in `parse_health_response`). A GET /v1/models would
        // pass a models-list-only key that cannot actually reach STT.
        Provider::Elevenlabs => Some(HealthEndpoint {
            method: HttpMethod::Post,
            url: "https://api.elevenlabs.io/v1/speech-to-text",
            auth: HealthAuth::XiApiKey,
        }),
        Provider::Mistral => e("https://api.mistral.ai/v1/models", HealthAuth::Bearer),
        Provider::Soniox => e("https://api.soniox.com/v1/models", HealthAuth::Bearer),
        Provider::Gemini => e(
            "https://generativelanguage.googleapis.com/v1beta/models",
            HealthAuth::QueryKey,
        ),
        Provider::Grok => e("https://api.x.ai/v1/models", HealthAuth::Bearer),
        // Routed / HW Cloud: no vendor endpoint — handled by the base-URL path.
        Provider::HyperWhisperCloud | Provider::AzureMai | Provider::GoogleChirp => None,
    }
}

/// Build a lightweight health-check request for `provider` using its default
/// endpoint and the supplied API key. Routed providers probe
/// [`HW_CLOUD_HEALTH_DEFAULT`]; use [`build_health_request_with_base`] to override.
///
/// `api_key` is ignored for routed providers (they need no key). For direct
/// providers it is placed in the header/query the vendor expects.
pub fn build_health_request(provider: Provider, api_key: &str) -> HttpRequest {
    build_health_request_with_base(provider, api_key, None)
}

/// Like [`build_health_request`] but with an explicit HW Cloud base URL for routed
/// providers (the platform passes this via `TranscribeParams.base_url`). For
/// direct providers `base_url` is ignored (their endpoint is fixed).
pub fn build_health_request_with_base(
    provider: Provider,
    api_key: &str,
    base_url: Option<&str>,
) -> HttpRequest {
    match endpoint(provider) {
        Some(ep) => build_direct(ep, api_key),
        None => build_routed(base_url),
    }
}

/// Build a GET request to a direct vendor endpoint with the right auth.
fn build_direct(ep: HealthEndpoint, api_key: &str) -> HttpRequest {
    let mut headers: Vec<Header> = Vec::new();
    let mut url = ep.url.to_string();

    match ep.auth {
        HealthAuth::Bearer => {
            headers.push(Header::new("Authorization", format!("Bearer {api_key}")));
        }
        HealthAuth::Token => {
            headers.push(Header::new("Authorization", format!("Token {api_key}")));
        }
        HealthAuth::Raw => {
            headers.push(Header::new("Authorization", api_key.to_string()));
        }
        HealthAuth::XiApiKey => {
            headers.push(Header::new("xi-api-key", api_key.to_string()));
        }
        HealthAuth::QueryKey => {
            // Append `key=<api_key>` to the query (the endpoint has no existing
            // query string, so `?` is correct here).
            let sep = if url.contains('?') { '&' } else { '?' };
            url = format!("{url}{sep}key={api_key}");
        }
    }
    headers.push(Header::new("Accept", "application/json"));

    HttpRequest {
        method: ep.method,
        url,
        headers,
        body: crate::contract::Body::Empty,
    }
}

/// Build a GET request to the HW Cloud `/health` edge for routed providers.
fn build_routed(base_url: Option<&str>) -> HttpRequest {
    let url = base_url
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(HW_CLOUD_HEALTH_DEFAULT)
        .to_string();

    HttpRequest {
        method: HttpMethod::Get,
        url,
        headers: vec![Header::new("Accept", "application/json")],
        body: crate::contract::Body::Empty,
    }
}

/// Parse a health-check response into a [`ProviderHealth`] verdict.
///
/// PARITY (macOS `CloudProviderHealthManager` per-provider switches + Windows
/// `PerformTranscriptionHealthCheckAsync`):
/// - 2xx → healthy.
/// - 401 / 403 → not healthy (unauthorized).
/// - **ElevenLabs** (STT-capability probe, `POST /v1/speech-to-text` with an empty
///   body): 400 / 422 also → **healthy** — a "bad request"/"unprocessable" verdict
///   means the key is valid and *can reach STT*; only the (deliberately empty) body
///   was rejected. 401/403 still mean unauthorized.
/// - **Gemini & Grok**: 400 also → not healthy (these vendors return 400 for an
///   invalid key — special-cased on both platforms).
/// - anything else (other 4xx, 5xx, etc.) → not healthy (unreachable).
///
/// The boolean `healthy` collapses macOS's `.healthy` vs `.unauthorized`/
/// `.unreachable` into a single reachable-and-authorized verdict; the raw status
/// is preserved in `ProviderHealth.status` so the caller can distinguish causes.
pub fn parse_health_response(provider: Provider, resp: &HttpResponse) -> ProviderHealth {
    let status = resp.status;
    let healthy = match provider {
        // ElevenLabs probes STT with an empty body: a 400/422 ("bad request" /
        // "unprocessable" — body rejected) proves the key can reach STT, so it is
        // healthy. 401/403 fall through to not-healthy below.
        Provider::Elevenlabs => {
            (200..=299).contains(&status) || status == 400 || status == 422
        }
        _ => (200..=299).contains(&status),
    };

    ProviderHealth {
        provider,
        healthy,
        status: Some(status),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::Body;

    fn header<'a>(req: &'a HttpRequest, name: &str) -> Option<&'a str> {
        req.headers
            .iter()
            .find(|h| h.name.eq_ignore_ascii_case(name))
            .map(|h| h.value.as_str())
    }

    fn resp(status: u16) -> HttpResponse {
        HttpResponse {
            status,
            headers: vec![],
            body: vec![],
        }
    }

    // ---- request building: endpoints + auth (golden) ----

    #[test]
    fn openai_health_request() {
        let req = build_health_request(Provider::Openai, "sk-test");
        assert_eq!(req.method, HttpMethod::Get);
        assert_eq!(req.url, "https://api.openai.com/v1/models");
        assert_eq!(header(&req, "Authorization"), Some("Bearer sk-test"));
        assert!(matches!(req.body, Body::Empty));
    }

    #[test]
    fn groq_health_request() {
        let req = build_health_request(Provider::Groq, "gsk-test");
        assert_eq!(req.url, "https://api.groq.com/openai/v1/models");
        assert_eq!(header(&req, "Authorization"), Some("Bearer gsk-test"));
    }

    #[test]
    fn deepgram_uses_token_scheme_and_projects_endpoint() {
        let req = build_health_request(Provider::Deepgram, "dg-test");
        assert_eq!(req.url, "https://api.deepgram.com/v1/projects");
        assert_eq!(header(&req, "Authorization"), Some("Token dg-test"));
    }

    #[test]
    fn assemblyai_uses_raw_authorization_and_limit_query() {
        let req = build_health_request(Provider::Assemblyai, "aai-test");
        assert_eq!(req.url, "https://api.assemblyai.com/v2/transcript?limit=1");
        // Raw key — no "Bearer"/"Token" prefix.
        assert_eq!(header(&req, "Authorization"), Some("aai-test"));
    }

    #[test]
    fn elevenlabs_posts_to_stt_with_xi_api_key_header() {
        let req = build_health_request(Provider::Elevenlabs, "el-test");
        // STT-capability probe: POST /v1/speech-to-text, not GET /v1/models.
        assert_eq!(req.method, HttpMethod::Post);
        assert_eq!(req.url, "https://api.elevenlabs.io/v1/speech-to-text");
        assert_eq!(header(&req, "xi-api-key"), Some("el-test"));
        assert_eq!(header(&req, "Authorization"), None);
        assert!(matches!(req.body, Body::Empty));
    }

    #[test]
    fn mistral_and_soniox_use_bearer_models_endpoint() {
        let m = build_health_request(Provider::Mistral, "mi-test");
        assert_eq!(m.url, "https://api.mistral.ai/v1/models");
        assert_eq!(header(&m, "Authorization"), Some("Bearer mi-test"));

        let s = build_health_request(Provider::Soniox, "so-test");
        assert_eq!(s.url, "https://api.soniox.com/v1/models");
        assert_eq!(header(&s, "Authorization"), Some("Bearer so-test"));
    }

    #[test]
    fn gemini_puts_key_in_query_not_header() {
        let req = build_health_request(Provider::Gemini, "g-test");
        assert_eq!(
            req.url,
            "https://generativelanguage.googleapis.com/v1beta/models?key=g-test"
        );
        assert_eq!(header(&req, "Authorization"), None);
    }

    #[test]
    fn grok_uses_bearer_models_endpoint() {
        let req = build_health_request(Provider::Grok, "xai-test");
        assert_eq!(req.url, "https://api.x.ai/v1/models");
        assert_eq!(header(&req, "Authorization"), Some("Bearer xai-test"));
    }

    #[test]
    fn routed_providers_probe_hw_cloud_default_with_no_auth() {
        for p in [
            Provider::HyperWhisperCloud,
            Provider::AzureMai,
            Provider::GoogleChirp,
        ] {
            let req = build_health_request(p, "ignored");
            assert_eq!(req.url, HW_CLOUD_HEALTH_DEFAULT);
            assert_eq!(header(&req, "Authorization"), None);
            assert_eq!(header(&req, "xi-api-key"), None);
        }
    }

    #[test]
    fn routed_provider_honors_base_url_override() {
        let req = build_health_request_with_base(
            Provider::AzureMai,
            "ignored",
            Some("https://staging.hw.test/health"),
        );
        assert_eq!(req.url, "https://staging.hw.test/health");
    }

    #[test]
    fn direct_provider_ignores_base_url_override() {
        let req =
            build_health_request_with_base(Provider::Openai, "sk", Some("https://nope.test/health"));
        assert_eq!(req.url, "https://api.openai.com/v1/models");
    }

    // ---- response parsing: status → verdict (golden) ----

    #[test]
    fn status_2xx_is_healthy() {
        for status in [200u16, 201, 204, 299] {
            let h = parse_health_response(Provider::Openai, &resp(status));
            assert!(h.healthy, "status={status}");
            assert_eq!(h.status, Some(status));
            assert_eq!(h.provider, Provider::Openai);
        }
    }

    #[test]
    fn status_401_403_not_healthy() {
        for status in [401u16, 403] {
            let h = parse_health_response(Provider::Mistral, &resp(status));
            assert!(!h.healthy, "status={status}");
            assert_eq!(h.status, Some(status));
        }
    }

    #[test]
    fn status_400_not_healthy_for_all_providers() {
        // 400 is not 2xx → not healthy regardless of provider. (Gemini/Grok
        // semantically map 400 → unauthorized; here it's simply "not healthy".)
        for p in [Provider::Gemini, Provider::Grok, Provider::Openai] {
            let h = parse_health_response(p, &resp(400));
            assert!(!h.healthy, "provider={p:?}");
            assert_eq!(h.status, Some(400));
        }
    }

    #[test]
    fn status_5xx_not_healthy() {
        let h = parse_health_response(Provider::Deepgram, &resp(503));
        assert!(!h.healthy);
        assert_eq!(h.status, Some(503));
    }

    #[test]
    fn elevenlabs_bad_request_is_healthy_key_reaches_stt() {
        // GOLDEN (B3): the STT probe sends an empty body, so a valid key gets a
        // 400/422 ("body rejected") — that proves STT reachability ⇒ healthy.
        for status in [200u16, 400, 422] {
            let h = parse_health_response(Provider::Elevenlabs, &resp(status));
            assert!(h.healthy, "elevenlabs status={status} should be healthy");
            assert_eq!(h.status, Some(status));
        }
    }

    #[test]
    fn elevenlabs_unauthorized_is_not_healthy() {
        // GOLDEN (B3): 401/403 still mean the key cannot reach STT.
        for status in [401u16, 403] {
            let h = parse_health_response(Provider::Elevenlabs, &resp(status));
            assert!(!h.healthy, "elevenlabs status={status} should be unhealthy");
        }
    }

    #[test]
    fn non_elevenlabs_400_422_stay_not_healthy() {
        // The 400/422→healthy carve-out is ElevenLabs-only; other providers keep
        // strict 2xx grading.
        for status in [400u16, 422] {
            assert!(!parse_health_response(Provider::Openai, &resp(status)).healthy);
        }
    }
}
