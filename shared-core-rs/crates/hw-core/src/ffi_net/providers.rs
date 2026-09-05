use super::{
    AssemblyaiPollOutcome, GeminiFile, GeminiFilePollOutcome, HwTranscript,
    HwTranscriptionError, HttpRequest, HttpResponse, SonioxPollStatus, TranscribeParams,
};

// ===========================================================================
// Single-shot providers: build_transcribe_request / parse_transcribe_response
// ===========================================================================

macro_rules! single_shot {
    ($build:ident, $parse:ident, $module:path) => {
        #[uniffi::export]
        pub fn $build(params: TranscribeParams) -> Result<HttpRequest, HwTranscriptionError> {
            use $module as m;
            m::build_transcribe_request(&params.into())
                .map(Into::into)
                .map_err(Into::into)
        }

        #[uniffi::export]
        pub fn $parse(resp: HttpResponse) -> Result<HwTranscript, HwTranscriptionError> {
            use $module as m;
            m::parse_transcribe_response(&resp.into())
                .map(Into::into)
                .map_err(Into::into)
        }
    };
}

single_shot!(
    openai_build_transcribe_request,
    openai_parse_transcribe_response,
    hw_net::providers::openai
);
single_shot!(
    groq_build_transcribe_request,
    groq_parse_transcribe_response,
    hw_net::providers::groq
);
single_shot!(
    mistral_build_transcribe_request,
    mistral_parse_transcribe_response,
    hw_net::providers::mistral
);
single_shot!(
    grok_build_transcribe_request,
    grok_parse_transcribe_response,
    hw_net::providers::grok
);
single_shot!(
    deepgram_build_transcribe_request,
    deepgram_parse_transcribe_response,
    hw_net::providers::deepgram
);
single_shot!(
    elevenlabs_build_transcribe_request,
    elevenlabs_parse_transcribe_response,
    hw_net::providers::elevenlabs
);
single_shot!(
    azure_mai_build_transcribe_request,
    azure_mai_parse_transcribe_response,
    hw_net::providers::azure_mai
);
single_shot!(
    google_chirp_build_transcribe_request,
    google_chirp_parse_transcribe_response,
    hw_net::providers::google_chirp
);
// Gemini 3.5 Transcribe is single-shot even though `gemini` is multi-step: the
// interactions endpoint takes the audio inline, so there is no upload/poll
// dance. Do NOT route this model through the `gemini_*` functions above — see
// `hw_net::providers::gemini_transcribe`'s module docs (TRAP 1).
single_shot!(
    gemini_transcribe_build_transcribe_request,
    gemini_transcribe_parse_transcribe_response,
    hw_net::providers::gemini_transcribe
);
single_shot!(
    meta_build_transcribe_request,
    meta_parse_transcribe_response,
    hw_net::providers::meta
);

// ===========================================================================
// HyperWhisper Cloud (single-shot + credit helpers)
// ===========================================================================

#[uniffi::export]
pub fn hyperwhisper_cloud_build_transcribe_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::hyperwhisper_cloud::build_transcribe_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn hyperwhisper_cloud_parse_transcribe_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::hyperwhisper_cloud::parse_transcribe_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn hyperwhisper_cloud_parse_credits_remaining(resp: HttpResponse) -> Option<f64> {
    hw_net::providers::hyperwhisper_cloud::parse_credits_remaining(&resp.into())
}

#[uniffi::export]
pub fn hyperwhisper_cloud_parse_credits_used(resp: HttpResponse) -> Option<f64> {
    hw_net::providers::hyperwhisper_cloud::parse_credits_used(&resp.into())
}

// ===========================================================================
// AssemblyAI (multi-step: upload -> create -> poll)
// ===========================================================================

#[uniffi::export]
pub fn assemblyai_build_upload_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_upload_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_upload_response(
    resp: HttpResponse,
) -> Result<String, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_upload_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_build_create_request(
    params: TranscribeParams,
    audio_url: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_create_request(&params.into(), &audio_url)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_create_response(
    resp: HttpResponse,
) -> Result<String, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_create_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_build_poll_request(
    params: TranscribeParams,
    id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_poll_request(&params.into(), &id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_poll_response(
    resp: HttpResponse,
) -> Result<AssemblyaiPollOutcome, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_poll_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

// ---------------------------------------------------------------------------
// AssemblyAI sync (one blocking request; platform gates on duration < 120s
// and falls back to the upload -> create -> poll flow above on any error)
// ---------------------------------------------------------------------------

#[uniffi::export]
pub fn assemblyai_build_sync_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_sync_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_sync_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_sync_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

/// Sync API duration ceiling, in seconds. Platforms should gate on this (real
/// duration when known, else a conservative estimate) before calling
/// `assemblyaiBuildSyncRequest`, falling back to the async
/// upload/create/poll flow when the duration is unknown or `>=` this value.
/// Mirrors `hw_net::providers::assemblyai::SYNC_MAX_DURATION_SECS`.
#[uniffi::export]
pub fn assemblyai_sync_max_duration_secs() -> f64 {
    hw_net::providers::assemblyai::SYNC_MAX_DURATION_SECS
}

/// Sync API HTTP call timeout, in milliseconds. Platforms should use this
/// (via a linked/derived cancellation deadline — `CancellationTokenSource` on
/// Windows, `URLSessionConfiguration.timeoutIntervalForRequest` on macOS) for
/// the sync HTTP call itself, instead of each hardcoding its own copy-pasted
/// literal. Mirrors `hw_net::providers::assemblyai::SYNC_TIMEOUT_MS`.
#[uniffi::export]
pub fn assemblyai_sync_timeout_ms() -> u64 {
    hw_net::providers::assemblyai::SYNC_TIMEOUT_MS
}

// ===========================================================================
// Gemini (multi-step: upload-start -> upload-bytes -> poll -> generate -> delete)
// ===========================================================================

#[uniffi::export]
pub fn gemini_build_upload_start_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_upload_start_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_upload_start_response(
    resp: HttpResponse,
) -> Result<String, HwTranscriptionError> {
    hw_net::providers::gemini::parse_upload_start_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_upload_bytes_request(
    params: TranscribeParams,
    upload_url: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_upload_bytes_request(&params.into(), &upload_url)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_upload_bytes_response(
    resp: HttpResponse,
) -> Result<GeminiFile, HwTranscriptionError> {
    hw_net::providers::gemini::parse_upload_bytes_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_poll_request(
    params: TranscribeParams,
    name: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_poll_request(&params.into(), &name)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_poll_response(
    resp: HttpResponse,
) -> Result<GeminiFilePollOutcome, HwTranscriptionError> {
    hw_net::providers::gemini::parse_poll_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_generate_request(
    params: TranscribeParams,
    file: GeminiFile,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_generate_request(&params.into(), &file.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_generate_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::gemini::parse_generate_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_delete_request(
    params: TranscribeParams,
    name: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_delete_request(&params.into(), &name)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_prompt(params: TranscribeParams) -> String {
    hw_net::providers::gemini::build_prompt(&params.into())
}

// ===========================================================================
// Soniox (multi-step: upload -> create -> status -> transcript -> delete)
// ===========================================================================

#[uniffi::export]
pub fn soniox_build_upload_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_upload_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_upload_response(resp: HttpResponse) -> Result<String, HwTranscriptionError> {
    hw_net::providers::soniox::parse_upload_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_create_request(
    params: TranscribeParams,
    file_id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_create_request(&params.into(), &file_id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_create_response(resp: HttpResponse) -> Result<String, HwTranscriptionError> {
    hw_net::providers::soniox::parse_create_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_status_request(
    params: TranscribeParams,
    transcription_id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_status_request(&params.into(), &transcription_id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_status_response(
    resp: HttpResponse,
) -> Result<SonioxPollStatus, HwTranscriptionError> {
    hw_net::providers::soniox::parse_status_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_transcript_request(
    params: TranscribeParams,
    transcription_id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_transcript_request(&params.into(), &transcription_id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_transcript_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::soniox::parse_transcript_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_delete_transcription_request(
    params: TranscribeParams,
    transcription_id: String,
) -> HttpRequest {
    hw_net::providers::soniox::build_delete_transcription_request(&params.into(), &transcription_id)
        .into()
}

#[uniffi::export]
pub fn soniox_build_delete_file_request(
    params: TranscribeParams,
    file_id: String,
) -> HttpRequest {
    hw_net::providers::soniox::build_delete_file_request(&params.into(), &file_id).into()
}
