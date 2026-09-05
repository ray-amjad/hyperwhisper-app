    use super::*;

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    const ALL_PROVIDERS: [HwProvider; 15] = [
        HwProvider::HyperWhisperCloud,
        HwProvider::Openai,
        HwProvider::Groq,
        HwProvider::Elevenlabs,
        HwProvider::Mistral,
        HwProvider::Grok,
        HwProvider::Deepgram,
        HwProvider::Soniox,
        HwProvider::Assemblyai,
        HwProvider::Gemini,
        HwProvider::AzureMai,
        HwProvider::GoogleChirp,
        HwProvider::GeminiTranscribe,
        HwProvider::GeminiTranscribeLive,
        HwProvider::Meta,
    ];

    /// A distinct label per FFI provider arm, written independently of the
    /// `From` impls under test. Exhaustive `match` — a new provider fails to
    /// compile here until it is added to the mapping tests too.
    fn hw_tag(p: &HwProvider) -> &'static str {
        match p {
            HwProvider::HyperWhisperCloud => "hyperwhisper_cloud",
            HwProvider::Openai => "openai",
            HwProvider::Groq => "groq",
            HwProvider::Elevenlabs => "elevenlabs",
            HwProvider::Mistral => "mistral",
            HwProvider::Grok => "grok",
            HwProvider::Deepgram => "deepgram",
            HwProvider::Soniox => "soniox",
            HwProvider::Assemblyai => "assemblyai",
            HwProvider::Gemini => "gemini",
            HwProvider::AzureMai => "azure_mai",
            HwProvider::GoogleChirp => "google_chirp",
            HwProvider::GeminiTranscribe => "gemini_transcribe",
            HwProvider::GeminiTranscribeLive => "gemini_transcribe_live",
            HwProvider::Meta => "meta",
        }
    }

    /// The same labels for the contract enum, so the two sides can be compared
    /// without going through the conversions being tested.
    fn contract_tag(p: &c::Provider) -> &'static str {
        match p {
            c::Provider::HyperWhisperCloud => "hyperwhisper_cloud",
            c::Provider::Openai => "openai",
            c::Provider::Groq => "groq",
            c::Provider::Elevenlabs => "elevenlabs",
            c::Provider::Mistral => "mistral",
            c::Provider::Grok => "grok",
            c::Provider::Deepgram => "deepgram",
            c::Provider::Soniox => "soniox",
            c::Provider::Assemblyai => "assemblyai",
            c::Provider::Gemini => "gemini",
            c::Provider::AzureMai => "azure_mai",
            c::Provider::GoogleChirp => "google_chirp",
            c::Provider::GeminiTranscribe => "gemini_transcribe",
            c::Provider::GeminiTranscribeLive => "gemini_transcribe_live",
            c::Provider::Meta => "meta",
        }
    }

    fn method_tag(m: &HttpMethod) -> &'static str {
        match m {
            HttpMethod::Get => "GET",
            HttpMethod::Post => "POST",
            HttpMethod::Put => "PUT",
            HttpMethod::Delete => "DELETE",
        }
    }

    /// Every field set to a value that is unique within the struct, so a
    /// conversion that reads the wrong field cannot look correct.
    fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "key-alpha".to_string(),
            model: "whisper-1".to_string(),
            language: Some("en-US".to_string()),
            vocabulary: vec!["Rust".to_string(), "UniFFI".to_string()],
            prompt: Some("Be terse.".to_string()),
            temperature: Some(0.25),
            audio_path: "/tmp/take-one.wav".to_string(),
            audio_mime: Some("audio/wav".to_string()),
            base_url: Some("https://cloud.example.test/".to_string()),
            license_key: Some("licence-bravo".to_string()),
            device_id: Some("device-charlie".to_string()),
            routed_provider: Some("provider-delta".to_string()),
            routed_model: Some("model-echo".to_string()),
            routed_domain: Some("domain-foxtrot".to_string()),
            // Spelled out, not `..Default::default()`, so the next field added
            // to this record breaks loudly right here.
            share_anonymous_speed_data: true,
        }
    }

    fn response(status: u16, body: &str) -> HttpResponse {
        HttpResponse {
            status,
            headers: Vec::new(),
            body: body.as_bytes().to_vec(),
        }
    }

    fn response_with_headers(status: u16, headers: &[(&str, &str)], body: &str) -> HttpResponse {
        HttpResponse {
            status,
            headers: headers
                .iter()
                .map(|(name, value)| Header {
                    name: (*name).to_string(),
                    value: (*value).to_string(),
                })
                .collect(),
            body: body.as_bytes().to_vec(),
        }
    }

    fn header(request: &HttpRequest, name: &str) -> Option<String> {
        request
            .headers
            .iter()
            .find(|h| h.name.eq_ignore_ascii_case(name))
            .map(|h| h.value.clone())
    }

    fn parts_of(body: &Body) -> &[HwPart] {
        match body {
            Body::Multipart { parts, .. } => parts,
            _ => panic!("expected a multipart body"),
        }
    }

    /// The value of the first multipart field named `name`.
    fn field(parts: &[HwPart], name: &str) -> Option<String> {
        parts.iter().find_map(|p| match p {
            HwPart::Field { name: n, value } if n == name => Some(value.clone()),
            _ => None,
        })
    }

    /// The single `FileRef` part, as `(field, path, mime, filename)`.
    fn file_ref(parts: &[HwPart]) -> (String, String, String, String) {
        parts
            .iter()
            .find_map(|p| match p {
                HwPart::FileRef {
                    field,
                    path,
                    mime,
                    filename,
                } => Some((
                    field.clone(),
                    path.clone(),
                    mime.clone(),
                    filename.clone(),
                )),
                _ => None,
            })
            .expect("expected a FileRef part")
    }

    /// The query string of a built URL.
    fn query_of(url: &str) -> &str {
        url.split_once('?').expect("expected a query string").1
    }

    /// `Result::expect_err` needs `Debug` on the Ok type, which the UniFFI
    /// records deliberately do not derive. This unwraps the error side without
    /// that bound.
    fn expect_error<T>(result: Result<T, HwTranscriptionError>, what: &str) -> HwTranscriptionError {
        match result {
            Ok(_) => panic!("{what}"),
            Err(error) => error,
        }
    }

    // -----------------------------------------------------------------------
    // Provider enum mapping
    // -----------------------------------------------------------------------

    #[test]
    fn provider_maps_to_the_same_contract_arm_in_both_directions() {
        for provider in ALL_PROVIDERS {
            let expected = hw_tag(&provider);
            let contract: c::Provider = provider.into();
            assert_eq!(
                contract_tag(&contract),
                expected,
                "HwProvider -> contract mapping is wrong for {expected}"
            );
            let back: HwProvider = contract.into();
            assert_eq!(
                hw_tag(&back),
                expected,
                "contract -> HwProvider mapping is wrong for {expected}"
            );
        }
    }

    /// Pins each direct provider's arm to an observable endpoint + auth scheme,
    /// so two arms swapped in *both* directions (which a round-trip cannot see)
    /// still fails. The three routed providers share one endpoint and are
    /// covered by the round-trip test above plus `routed_providers_probe_...`.
    #[test]
    fn health_request_targets_each_vendor_endpoint_with_its_auth_scheme() {
        let cases: [(HwProvider, &str, &str, &str, &str); 9] = [
            (
                HwProvider::Openai,
                "GET",
                "https://api.openai.com/v1/models",
                "Authorization",
                "Bearer key-alpha",
            ),
            (
                HwProvider::Groq,
                "GET",
                "https://api.groq.com/openai/v1/models",
                "Authorization",
                "Bearer key-alpha",
            ),
            (
                HwProvider::Mistral,
                "GET",
                "https://api.mistral.ai/v1/models",
                "Authorization",
                "Bearer key-alpha",
            ),
            (
                HwProvider::Grok,
                "GET",
                "https://api.x.ai/v1/models",
                "Authorization",
                "Bearer key-alpha",
            ),
            (
                HwProvider::Soniox,
                "GET",
                "https://api.soniox.com/v1/models",
                "Authorization",
                "Bearer key-alpha",
            ),
            (
                HwProvider::Deepgram,
                "GET",
                "https://api.deepgram.com/v1/projects",
                "Authorization",
                "Token key-alpha",
            ),
            (
                HwProvider::Assemblyai,
                "GET",
                "https://api.assemblyai.com/v2/transcript?limit=1",
                "Authorization",
                "key-alpha",
            ),
            (
                HwProvider::Elevenlabs,
                "POST",
                "https://api.elevenlabs.io/v1/speech-to-text",
                "xi-api-key",
                "key-alpha",
            ),
            (
                HwProvider::Gemini,
                "GET",
                "https://generativelanguage.googleapis.com/v1beta/models?key=key-alpha",
                "Accept",
                "application/json",
            ),
        ];

        for (provider, method, url, auth_name, auth_value) in cases {
            let tag = hw_tag(&provider);
            let request = build_health_request(provider, "key-alpha".to_string());
            assert_eq!(method_tag(&request.method), method, "method for {tag}");
            assert_eq!(request.url, url, "url for {tag}");
            assert_eq!(
                header(&request, auth_name).as_deref(),
                Some(auth_value),
                "{auth_name} for {tag}"
            );
        }
    }

    #[test]
    fn routed_providers_probe_the_hw_cloud_health_edge() {
        for provider in [
            HwProvider::HyperWhisperCloud,
            HwProvider::AzureMai,
            HwProvider::GoogleChirp,
        ] {
            let tag = hw_tag(&provider);
            let request = build_health_request(provider, "key-alpha".to_string());
            assert_eq!(request.url, hw_cloud_health_default(), "default url for {tag}");
            // No API key is carried on the routed probe.
            assert_eq!(header(&request, "Authorization"), None, "auth for {tag}");
        }

        let overridden = build_health_request_with_base(
            HwProvider::AzureMai,
            "key-alpha".to_string(),
            Some("https://edge.example.test/health".to_string()),
        );
        assert_eq!(overridden.url, "https://edge.example.test/health");
    }

    #[test]
    fn health_verdict_carries_the_probed_provider_and_status_back() {
        for provider in ALL_PROVIDERS {
            let tag = hw_tag(&provider);
            let verdict = parse_health_response(provider, response(200, "{}"));
            assert_eq!(hw_tag(&verdict.provider), tag, "verdict provider for {tag}");
            assert!(verdict.healthy, "200 should be healthy for {tag}");
            assert_eq!(verdict.status, Some(200), "status for {tag}");
        }
    }

    /// ElevenLabs is probed by POSTing an empty body to the STT endpoint, so a
    /// 422 proves the key reaches STT. Every other provider treats 422 as
    /// unhealthy — the one behavioural difference that tells the two arms apart.
    #[test]
    fn elevenlabs_alone_grades_a_rejected_body_as_healthy() {
        let elevenlabs = parse_health_response(HwProvider::Elevenlabs, response(422, "{}"));
        assert!(elevenlabs.healthy);
        assert_eq!(elevenlabs.status, Some(422));

        let openai = parse_health_response(HwProvider::Openai, response(422, "{}"));
        assert!(!openai.healthy);

        let unauthorized = parse_health_response(HwProvider::Elevenlabs, response(401, "{}"));
        assert!(!unauthorized.healthy);
    }

    // -----------------------------------------------------------------------
    // TranscribeParams field fidelity
    // -----------------------------------------------------------------------

    #[test]
    fn openai_request_carries_every_transcribe_param_to_its_own_slot() {
        let request = openai_build_transcribe_request(params()).expect("request");

        assert_eq!(method_tag(&request.method), "POST");
        assert_eq!(request.url, "https://api.openai.com/v1/audio/transcriptions");
        assert_eq!(
            header(&request, "Authorization").as_deref(),
            Some("Bearer key-alpha"),
            "api_key belongs in the bearer header"
        );

        let parts = parts_of(&request.body);
        assert_eq!(field(parts, "model").as_deref(), Some("whisper-1"));
        // "en-US" is cut to the ISO-639-1 code Whisper expects.
        assert_eq!(field(parts, "language").as_deref(), Some("en"));
        assert_eq!(
            field(parts, "prompt").as_deref(),
            Some("Important terms to recognize: Rust, UniFFI. Be terse."),
            "vocabulary and prompt are both carried, framed and in order"
        );
        assert_eq!(field(parts, "response_format").as_deref(), Some("json"));

        let (field_name, path, mime, filename) = file_ref(parts);
        assert_eq!(field_name, "file");
        assert_eq!(path, "/tmp/take-one.wav");
        assert_eq!(mime, "audio/wav");
        assert_eq!(filename, "take-one.wav");

        // `share_anonymous_speed_data` is the one param with NO OpenAI slot, by
        // design: it is a HyperWhisper-backend concern and must never reach a
        // third-party vendor. Asserted here so this test's name stays true as
        // the record grows.
        let mut opted_out = params();
        opted_out.share_anonymous_speed_data = false;
        let private = openai_build_transcribe_request(opted_out).expect("request");
        assert!(
            header(&private, "X-Latency-Opt-Out").is_none(),
            "the latency opt-out must have no OpenAI slot, header or field"
        );
        assert_eq!(
            parts_of(&private.body).len(),
            parts.len(),
            "toggling the flag must not add a multipart field to a direct vendor"
        );
    }

    /// The core never reads audio: a request references the file by path and
    /// the platform streams it. A `Body::Bytes` here would mean audio crossed FFI.
    #[test]
    fn audio_is_referenced_by_path_and_never_carried_as_bytes() {
        let request = openai_build_transcribe_request(params()).expect("request");
        let parts = parts_of(&request.body);
        let (_, path, _, _) = file_ref(parts);
        assert_eq!(path, "/tmp/take-one.wav");

        let cloud = hyperwhisper_cloud_build_transcribe_request(params()).expect("request");
        let (raw_field, cloud_path, cloud_mime, _) = file_ref(parts_of(&cloud.body));
        assert_eq!(raw_field, "@raw", "the raw-stream sentinel field");
        assert_eq!(cloud_path, "/tmp/take-one.wav");
        assert_eq!(cloud_mime, "audio/wav");
    }

    /// The privacy flag survives the FFI record conversion, and reaches the
    /// wire only as an absence-or-presence on the HyperWhisper Cloud builder.
    #[test]
    fn the_latency_opt_out_crosses_the_ffi_boundary_in_both_states() {
        let sharing = hyperwhisper_cloud_build_transcribe_request(params()).expect("request");
        assert!(
            header(&sharing, "X-Latency-Opt-Out").is_none(),
            "share_anonymous_speed_data: true must send no header"
        );

        let mut p = params();
        p.share_anonymous_speed_data = false;
        let opted_out = hyperwhisper_cloud_build_transcribe_request(p).expect("request");
        assert_eq!(
            header(&opted_out, "X-Latency-Opt-Out").as_deref(),
            Some("1"),
            "share_anonymous_speed_data: false must send the opt-out header"
        );
    }

    // -----------------------------------------------------------------------
    // normalize_vocabulary_terms
    // -----------------------------------------------------------------------

    #[test]
    fn normalize_vocabulary_terms_sanitizes_dedupes_and_caps() {
        let words = vec![
            "  API  ".to_string(),
            String::new(),
            "api".to_string(),
            "Rust<script>".to_string(),
            "multi\n word".to_string(),
        ];

        assert_eq!(
            normalize_vocabulary_terms(words.clone(), None),
            vec!["API", "Rustscript", "multi word"],
            "None means no cap"
        );
        assert_eq!(
            normalize_vocabulary_terms(words.clone(), Some(2)),
            vec!["API", "Rustscript"]
        );
        assert_eq!(
            normalize_vocabulary_terms(words, Some(0)),
            Vec::<String>::new(),
            "Some(0) means zero terms, not uncapped"
        );
    }

    /// The three `routed_*` params land in three different headers. A crossed
    /// pair in the record conversion would swap two values here.
    #[test]
    fn hyperwhisper_cloud_request_keeps_the_routed_params_in_their_own_headers() {
        let request = hyperwhisper_cloud_build_transcribe_request(params()).expect("request");

        assert_eq!(
            header(&request, "X-STT-Provider").as_deref(),
            Some("provider-delta")
        );
        assert_eq!(header(&request, "X-STT-Model").as_deref(), Some("model-echo"));
        assert_eq!(
            header(&request, "X-STT-Domain").as_deref(),
            Some("domain-foxtrot")
        );
        assert_eq!(header(&request, "Content-Type").as_deref(), Some("audio/wav"));
    }

    #[test]
    fn hyperwhisper_cloud_request_identifies_the_caller_by_licence_key() {
        let request = hyperwhisper_cloud_build_transcribe_request(params()).expect("request");

        assert!(
            request.url.starts_with("https://cloud.example.test/transcribe?"),
            "base_url is used and its trailing slash trimmed: {}",
            request.url
        );
        let query = query_of(&request.url);
        // license_key wins over device_id, and only one identifier is sent.
        assert!(query.contains("license_key=licence-bravo"), "query: {query}");
        assert!(!query.contains("device_id"), "query: {query}");
        // The cloud path sends the language verbatim (lowercased), not cut to 2
        // characters — region-sensitive upstreams need the subtag.
        assert!(query.contains("language=en-us"), "query: {query}");
        assert!(query.contains("initial_prompt=Rust,UniFFI"), "query: {query}");
    }

    #[test]
    fn hyperwhisper_cloud_falls_back_to_the_device_id_on_trial() {
        let mut trial = params();
        trial.license_key = None;
        let request = hyperwhisper_cloud_build_transcribe_request(trial).expect("request");

        let query = query_of(&request.url);
        assert!(query.contains("device_id=device-charlie"), "query: {query}");
        assert!(!query.contains("license_key"), "query: {query}");
    }

    /// Entitlement is a server concern, but the client must not be able to call
    /// the paid endpoint with no identifier at all.
    #[test]
    fn hyperwhisper_cloud_request_without_an_identifier_is_rejected() {
        let mut anonymous = params();
        anonymous.license_key = None;
        anonymous.device_id = None;

        let error = expect_error(hyperwhisper_cloud_build_transcribe_request(anonymous), "no identifier must not build a request");
        match error {
            HwTranscriptionError::BadRequest { status, message } => {
                assert_eq!(status, 0);
                assert!(message.contains("license_key or device_id"), "{message}");
            }
            other => panic!("expected BadRequest, got {other:?}"),
        }
    }

    #[test]
    fn hyperwhisper_cloud_request_without_a_base_url_is_rejected() {
        let mut no_base = params();
        no_base.base_url = None;

        let error = expect_error(hyperwhisper_cloud_build_transcribe_request(no_base), "no base_url must not build a request");
        assert!(matches!(
            error,
            HwTranscriptionError::BadRequest { status: 0, .. }
        ));
    }

    // -----------------------------------------------------------------------
    // Response + transcript conversion
    // -----------------------------------------------------------------------

    #[test]
    fn hyperwhisper_cloud_transcript_carries_text_cost_and_served_provider() {
        let body = r#"{"text":"hello there","cost":{"usd":0.0007,"credits":4.5},"credits_remaining":95.5}"#;
        let transcript = hyperwhisper_cloud_parse_transcribe_response(response_with_headers(
            200,
            &[("X-STT-Provider", "azure-mai")],
            body,
        ))
        .expect("transcript");

        assert_eq!(transcript.text, "hello there");
        assert_eq!(transcript.cost, Some(4.5));
        assert_eq!(transcript.credits_remaining, Some(95.5));
        assert_eq!(transcript.raw_provider.as_deref(), Some("azure-mai"));
    }

    /// Reading these proves response headers survive the FFI conversion — the
    /// body-only path would return `None`.
    #[test]
    fn hyperwhisper_cloud_reads_the_credit_headers() {
        let resp = response_with_headers(
            200,
            &[
                ("X-Device-Credits-Remaining", "12.5"),
                ("X-Credits-Used", "2.5"),
            ],
            "{}",
        );
        assert_eq!(hyperwhisper_cloud_parse_credits_remaining(resp), Some(12.5));

        let used = response_with_headers(200, &[("X-Credits-Used", " 2.5 ")], "{}");
        assert_eq!(hyperwhisper_cloud_parse_credits_used(used), Some(2.5));

        assert_eq!(
            hyperwhisper_cloud_parse_credits_remaining(response(200, "{}")),
            None,
            "a missing header is None, not 0"
        );
    }

    #[test]
    fn hyperwhisper_cloud_no_speech_is_an_error_not_an_empty_transcript() {
        let error = expect_error(hyperwhisper_cloud_parse_transcribe_response(response(
            200,
            r#"{"text":"","no_speech_detected":true}"#,
        )), "no speech must not be a transcript");
        assert!(matches!(error, HwTranscriptionError::NoSpeech));
    }

    #[test]
    fn provider_parse_failures_surface_as_typed_errors() {
        let unauthorized = expect_error(openai_parse_transcribe_response(response(401, r#"{"error":"nope"}"#)), "401 must fail");
        assert!(matches!(unauthorized, HwTranscriptionError::Unauthorized));

        let too_large = expect_error(openai_parse_transcribe_response(response(413, "{}")), "413 must fail");
        assert!(matches!(too_large, HwTranscriptionError::FileTooLarge));

        let unparseable =
            expect_error(openai_parse_transcribe_response(response(200, "not json")), "must fail");
        assert!(matches!(unparseable, HwTranscriptionError::Parse { .. }));

        let ok = openai_parse_transcribe_response(response(200, r#"{"text":"hi"}"#)).expect("ok");
        assert_eq!(ok.text, "hi");
    }

    // -----------------------------------------------------------------------
    // Error classification + retry
    // -----------------------------------------------------------------------

    #[test]
    fn classify_error_maps_each_status_to_its_own_variant() {
        assert!(matches!(
            classify_error(401, String::new()),
            HwTranscriptionError::Unauthorized
        ));
        assert!(matches!(
            classify_error(403, String::new()),
            HwTranscriptionError::Unauthorized
        ));
        assert!(matches!(
            classify_error(402, String::new()),
            HwTranscriptionError::QuotaExceeded
        ));
        assert!(matches!(
            classify_error(413, String::new()),
            HwTranscriptionError::FileTooLarge
        ));
        assert!(matches!(
            classify_error(408, String::new()),
            HwTranscriptionError::ProviderUnavailable { status: 408 }
        ));
        assert!(matches!(
            classify_error(503, String::new()),
            HwTranscriptionError::ProviderUnavailable { status: 503 }
        ));
        assert!(matches!(
            classify_error(429, String::new()),
            HwTranscriptionError::RateLimited { .. }
        ));

        match classify_error(404, r#"{"error":{"message":"no such model"}}"#.to_string()) {
            HwTranscriptionError::BadRequest { status, message } => {
                assert_eq!(status, 404);
                assert_eq!(message, "no such model");
            }
            other => panic!("expected BadRequest, got {other:?}"),
        }
    }

    #[test]
    fn a_rate_limit_carries_its_retry_after_across_the_boundary() {
        match classify_error(429, r#"{"retry_after":7}"#.to_string()) {
            HwTranscriptionError::RateLimited { retry_after_secs } => {
                assert_eq!(retry_after_secs, Some(7));
            }
            other => panic!("expected RateLimited, got {other:?}"),
        }

        match classify_error(429, "{}".to_string()) {
            HwTranscriptionError::RateLimited { retry_after_secs } => {
                assert_eq!(retry_after_secs, None);
            }
            other => panic!("expected RateLimited, got {other:?}"),
        }
    }

    /// A 429 whose body says the account is out of quota is terminal, not a
    /// rate limit — retrying it just burns the timeout budget.
    #[test]
    fn a_quota_exhausted_429_is_terminal_rather_than_rate_limited() {
        let body = r#"{"error":{"code":"insufficient_quota","message":"out of credit"}}"#;
        assert!(matches!(
            classify_error(429, body.to_string()),
            HwTranscriptionError::QuotaExceeded
        ));
        assert!(!is_retryable(429, body.to_string()));
        assert!(matches!(
            next_retry(1, 429, body.to_string(), None),
            RetryDecision::GiveUp
        ));
    }

    #[test]
    fn is_retryable_follows_the_classified_error_not_the_raw_status() {
        assert!(is_retryable(429, String::new()), "plain 429 is retryable");
        assert!(is_retryable(500, String::new()), "5xx is retryable");
        assert!(is_retryable(408, String::new()), "timeout is retryable");
        assert!(!is_retryable(401, String::new()));
        assert!(!is_retryable(402, String::new()));
        assert!(!is_retryable(413, String::new()));
        assert!(!is_retryable(400, String::new()));
    }

    /// The unified backoff is `2^(attempt-1)` seconds, in milliseconds.
    #[test]
    fn retry_delay_doubles_with_each_attempt() {
        for (attempt, expected_ms) in [(1u32, 1_000u64), (2, 2_000), (3, 4_000), (7, 64_000)] {
            match next_retry(attempt, 503, String::new(), None) {
                RetryDecision::Retry { delay_ms } => {
                    assert_eq!(delay_ms, expected_ms, "delay for attempt {attempt}");
                }
                RetryDecision::GiveUp => panic!("attempt {attempt} should retry"),
            }
        }
    }

    /// A hostile `Retry-After` must not stall the app: it is clamped per sleep.
    #[test]
    fn retry_after_is_honoured_and_clamped() {
        match next_retry(1, 429, String::new(), Some(3)) {
            RetryDecision::Retry { delay_ms } => assert_eq!(delay_ms, 3_000),
            RetryDecision::GiveUp => panic!("should retry"),
        }
        match next_retry(1, 429, String::new(), Some(9_999)) {
            RetryDecision::Retry { delay_ms } => {
                assert_eq!(delay_ms, retry_max_retry_after_secs() * 1_000);
            }
            RetryDecision::GiveUp => panic!("should retry"),
        }
    }

    #[test]
    fn retrying_stops_at_the_attempt_ceiling() {
        let last = retry_max_attempts();
        assert!(matches!(
            next_retry(last - 1, 503, String::new(), None),
            RetryDecision::Retry { .. }
        ));
        assert!(matches!(
            next_retry(last, 503, String::new(), None),
            RetryDecision::GiveUp
        ));
    }

    /// Issue #379 across the FFI boundary: the backoff budget stops a hard-down
    /// provider before the 16/32/64s sleeps, where the attempt ceiling alone
    /// would have burned ~127s.
    #[test]
    fn a_budget_stops_retrying_before_the_long_sleeps() {
        let budget = retry_default_budget_ms();
        assert_eq!(budget, 30_000, "interactive default");
        // 15s of sleep already spent; attempt 5 wants 16s → past the deadline.
        assert!(matches!(
            next_retry_within_budget(5, 500, String::new(), None, 15_000, budget),
            RetryDecision::GiveUp
        ));
        // The same attempt is still a retry with room to spare.
        assert!(matches!(
            next_retry_within_budget(5, 500, String::new(), None, 0, budget),
            RetryDecision::Retry { delay_ms: 16_000 }
        ));
    }

    /// `slept_ms` counts BACKOFF ONLY (review round 1, finding A1), so the number
    /// of attempts a sequence gets is independent of how long each request takes.
    /// A 150 MB import that uploads for four minutes before each 502 gets exactly
    /// as many attempts as a 200 ms dictation failure — an earlier revision fed
    /// the sequence's wall clock in here and gave the big upload only one.
    #[test]
    fn the_attempt_count_is_independent_of_request_duration_across_the_ffi() {
        // Drive the export exactly as a platform driver does: accumulate the
        // returned delays, ignore the request time.
        fn attempts_before_give_up(budget: u64) -> u32 {
            let mut slept = 0u64;
            let mut attempt = 0u32;
            loop {
                attempt += 1;
                match next_retry_within_budget(attempt, 502, String::new(), None, slept, budget) {
                    RetryDecision::Retry { delay_ms } => slept += delay_ms,
                    RetryDecision::GiveUp => return attempt,
                }
            }
        }
        // The driver never adds request time, so a four-minute upload and a
        // 200 ms failure produce the identical call sequence and stop together.
        assert_eq!(attempts_before_give_up(retry_default_budget_ms()), 5);
        assert_eq!(attempts_before_give_up(0), retry_max_attempts());
    }

    /// `budget_ms == 0` is unbounded, so the budgeted export is a strict superset
    /// of `next_retry` — the pre-#379 behaviour is still reachable, unchanged.
    #[test]
    fn a_zero_budget_matches_the_unbudgeted_export() {
        for attempt in 1..=retry_max_attempts() {
            let budgeted = next_retry_within_budget(attempt, 503, String::new(), None, 0, 0);
            let plain = next_retry(attempt, 503, String::new(), None);
            match (budgeted, plain) {
                (RetryDecision::Retry { delay_ms: a }, RetryDecision::Retry { delay_ms: b }) => {
                    assert_eq!(a, b, "attempt {attempt}")
                }
                (RetryDecision::GiveUp, RetryDecision::GiveUp) => {}
                _ => panic!("budget_ms=0 diverged from next_retry on attempt {attempt}"),
            }
        }
    }

    /// The `Display` text is hand-written in this file (not derived from the
    /// leaf's `thiserror` messages), so nothing but a test keeps it honest.
    #[test]
    fn error_messages_name_the_failure_and_its_status() {
        assert_eq!(HwTranscriptionError::Unauthorized.to_string(), "unauthorized");
        assert_eq!(
            HwTranscriptionError::QuotaExceeded.to_string(),
            "quota exceeded"
        );
        assert_eq!(
            HwTranscriptionError::FileTooLarge.to_string(),
            "file too large"
        );
        assert_eq!(
            HwTranscriptionError::RateLimited {
                retry_after_secs: Some(5)
            }
            .to_string(),
            "rate limited"
        );
        assert_eq!(
            HwTranscriptionError::NoSpeech.to_string(),
            "no speech detected"
        );
        assert_eq!(
            HwTranscriptionError::ProviderUnavailable { status: 502 }.to_string(),
            "provider unavailable (status 502)"
        );
        assert_eq!(
            HwTranscriptionError::BadRequest {
                status: 400,
                message: "bad model".to_string()
            }
            .to_string(),
            "bad request (status 400): bad model"
        );
        assert_eq!(
            HwTranscriptionError::Parse {
                message: "invalid JSON".to_string()
            }
            .to_string(),
            "response parse error: invalid JSON"
        );
    }

    // -----------------------------------------------------------------------
    // Multi-step provider outcomes
    // -----------------------------------------------------------------------

    #[test]
    fn assemblyai_poll_reports_pending_until_the_transcript_is_done() {
        let pending = assemblyai_parse_poll_response(response(200, r#"{"status":"queued"}"#))
            .expect("pending");
        assert!(matches!(pending, AssemblyaiPollOutcome::Pending));

        let done = assemblyai_parse_poll_response(response(
            200,
            r#"{"status":"completed","text":"all done"}"#,
        ))
        .expect("done");
        match done {
            AssemblyaiPollOutcome::Done { transcript } => assert_eq!(transcript.text, "all done"),
            AssemblyaiPollOutcome::Pending => panic!("expected Done"),
        }
    }

    /// A transient upstream failure mid-poll keeps the loop alive; an
    /// unauthorized one ends it.
    #[test]
    fn assemblyai_poll_separates_transient_failures_from_terminal_ones() {
        let transient = assemblyai_parse_poll_response(response(503, "gateway down"))
            .expect("5xx keeps polling");
        assert!(matches!(transient, AssemblyaiPollOutcome::Pending));

        let terminal =
            expect_error(assemblyai_parse_poll_response(response(401, "{}")), "401 is terminal");
        assert!(matches!(terminal, HwTranscriptionError::Unauthorized));
    }

    #[test]
    fn assemblyai_completed_but_empty_is_no_speech() {
        let error =
            expect_error(assemblyai_parse_poll_response(response(200, r#"{"status":"completed","text":""}"#)), "empty text must fail");
        assert!(matches!(error, HwTranscriptionError::NoSpeech));
    }

    #[test]
    fn gemini_file_poll_returns_the_whole_file_once_active() {
        let pending = gemini_parse_poll_response(response(
            200,
            r#"{"name":"files/abc","uri":"https://files.example.test/abc","state":"PROCESSING"}"#,
        ))
        .expect("pending");
        assert!(matches!(pending, GeminiFilePollOutcome::Pending));

        let active = gemini_parse_poll_response(response(
            200,
            r#"{"name":"files/abc","uri":"https://files.example.test/abc","mimeType":"audio/wav","state":"ACTIVE"}"#,
        ))
        .expect("active");
        match active {
            GeminiFilePollOutcome::Active { file } => {
                assert_eq!(file.name.as_deref(), Some("files/abc"));
                assert_eq!(file.uri.as_deref(), Some("https://files.example.test/abc"));
                assert_eq!(file.mime_type.as_deref(), Some("audio/wav"));
                assert_eq!(file.state.as_deref(), Some("ACTIVE"));
            }
            GeminiFilePollOutcome::Pending => panic!("expected Active"),
        }
    }

    /// Sends a `GeminiFile` back *into* Rust, which exercises the FFI -> leaf
    /// direction of the record conversion.
    #[test]
    fn gemini_generate_request_needs_the_uri_from_the_uploaded_file() {
        let file = GeminiFile {
            name: Some("files/abc".to_string()),
            uri: Some("https://files.example.test/abc".to_string()),
            mime_type: Some("audio/wav".to_string()),
            state: Some("ACTIVE".to_string()),
        };
        let request = gemini_build_generate_request(params(), file).expect("request");
        let body = match &request.body {
            Body::Bytes { data, .. } => String::from_utf8(data.clone()).expect("utf-8"),
            _ => panic!("expected a bytes body"),
        };
        assert!(
            body.contains("https://files.example.test/abc"),
            "the uri must reach the generate body: {body}"
        );

        let no_uri = GeminiFile {
            name: Some("files/abc".to_string()),
            uri: None,
            mime_type: Some("audio/wav".to_string()),
            state: Some("ACTIVE".to_string()),
        };
        let error = expect_error(gemini_build_generate_request(params(), no_uri), "a file with no uri cannot be transcribed");
        assert!(matches!(error, HwTranscriptionError::Parse { .. }));
    }

    #[test]
    fn gemini_prompt_states_the_language_and_the_vocabulary_terms() {
        let prompt = gemini_build_prompt(params());
        assert!(prompt.contains("Transcribe this audio accurately."), "{prompt}");
        assert!(prompt.contains("The audio is in en-US."), "{prompt}");
        assert!(prompt.contains("Rust, UniFFI"), "{prompt}");
        assert!(prompt.contains("Be terse."), "{prompt}");
    }

    #[test]
    fn soniox_status_separates_pending_completed_and_quota_failures() {
        let pending = soniox_parse_status_response(response(200, r#"{"status":"processing"}"#))
            .expect("pending");
        assert!(matches!(pending, SonioxPollStatus::Pending));

        let completed = soniox_parse_status_response(response(200, r#"{"status":"completed"}"#))
            .expect("completed");
        assert!(matches!(completed, SonioxPollStatus::Completed));

        let quota = expect_error(soniox_parse_status_response(response(
            200,
            r#"{"status":"error","error_message":"insufficient balance"}"#,
        )), "a balance failure must fail");
        assert!(matches!(quota, HwTranscriptionError::QuotaExceeded));
    }

    // -----------------------------------------------------------------------
    // Request conversion, FFI -> contract
    // -----------------------------------------------------------------------

    /// The build functions only exercise contract -> FFI. This drives the
    /// opposite direction, where a crossed `mime` / `filename` field would
    /// otherwise never be seen.
    #[test]
    fn a_request_survives_the_round_trip_back_into_the_contract() {
        let original = HttpRequest {
            method: HttpMethod::Put,
            url: "https://example.test/upload".to_string(),
            headers: vec![Header {
                name: "X-Test".to_string(),
                value: "value".to_string(),
            }],
            body: Body::Multipart {
                boundary: "boundary-1".to_string(),
                parts: vec![
                    HwPart::Field {
                        name: "model".to_string(),
                        value: "whisper-1".to_string(),
                    },
                    HwPart::FileRef {
                        field: "file".to_string(),
                        path: "/tmp/take-one.wav".to_string(),
                        mime: "audio/wav".to_string(),
                        filename: "take-one.wav".to_string(),
                    },
                    HwPart::InlineFile {
                        field: "request".to_string(),
                        filename: "request.json".to_string(),
                        mime: "application/json".to_string(),
                        data: br#"{"mode":"PUSH_TO_TALK"}"#.to_vec(),
                    },
                ],
            },
        };

        let contract: c::HttpRequest = original.into();
        let back: HttpRequest = contract.into();

        assert_eq!(method_tag(&back.method), "PUT");
        assert_eq!(back.url, "https://example.test/upload");
        assert_eq!(header(&back, "X-Test").as_deref(), Some("value"));
        match &back.body {
            Body::Multipart { boundary, parts } => {
                assert_eq!(boundary, "boundary-1");
                assert_eq!(field(parts, "model").as_deref(), Some("whisper-1"));
                let (field_name, path, mime, filename) = file_ref(parts);
                assert_eq!(field_name, "file");
                assert_eq!(path, "/tmp/take-one.wav");
                assert_eq!(mime, "audio/wav");
                assert_eq!(filename, "take-one.wav");
                assert!(parts.iter().any(|part| matches!(
                    part,
                    HwPart::InlineFile { field, filename, mime, data }
                        if field == "request" && filename == "request.json"
                            && mime == "application/json"
                            && data == br#"{"mode":"PUSH_TO_TALK"}"#
                )));
            }
            _ => panic!("expected a multipart body"),
        }
    }

    #[test]
    fn the_other_body_shapes_survive_the_round_trip_too() {
        assert_eq!(c::Body::from(Body::Empty), c::Body::Empty);

        let bytes: Body = c::Body::from(Body::Bytes {
            content_type: "application/json".to_string(),
            data: b"{}".to_vec(),
        })
        .into();
        match bytes {
            Body::Bytes { content_type, data } => {
                assert_eq!(content_type, "application/json");
                assert_eq!(data, b"{}".to_vec());
            }
            _ => panic!("expected Bytes"),
        }

        let stream: Body = c::Body::from(Body::FileStream {
            path: "/tmp/take-one.wav".to_string(),
            content_type: "audio/wav".to_string(),
        })
        .into();
        match stream {
            Body::FileStream { path, content_type } => {
                assert_eq!(path, "/tmp/take-one.wav");
                assert_eq!(content_type, "audio/wav");
            }
            _ => panic!("expected FileStream"),
        }
    }
