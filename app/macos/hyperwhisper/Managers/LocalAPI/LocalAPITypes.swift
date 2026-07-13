//
//  LocalAPITypes.swift
//  hyperwhisper
//
//  Codable request/response shapes for the in-app Local HTTP API
//  (Settings → API Server). Phase 1 surface: /health, /models, /modes, /transcribe.
//

import Foundation

// MARK: - API versioning

enum LocalAPIVersion {
    static let current: Int = 1
}

// MARK: - Error codes

/// Closed set of machine-readable error codes the API can return.
/// MCP wrappers map these 1:1 to client-side errors, so adding a new
/// code is a contract change — pick from this list when possible.
enum LocalAPIErrorCode: String, Codable, Sendable {
    case modelNotInstalled = "MODEL_NOT_INSTALLED"
    case modelNotFound = "MODEL_NOT_FOUND"
    case engineUnavailable = "ENGINE_UNAVAILABLE"
    case missingAPIKey = "MISSING_API_KEY"
    case fileNotFound = "FILE_NOT_FOUND"
    case fileAccessDenied = "FILE_ACCESS_DENIED"
    // Returned by Windows /transcribe when a caller-supplied `file` path resolves
    // outside HyperWhisper's recording folders (path-containment guard, #740).
    // Declared here so cross-platform clients decoding this closed enum accept the
    // code even on builds that do not yet emit it.
    case fileNotAllowed = "FILE_NOT_ALLOWED"
    case audioDecodeFailed = "AUDIO_DECODE_FAILED"
    case transcriptionFailed = "TRANSCRIPTION_FAILED"
    case modeNotFound = "MODE_NOT_FOUND"
    case modeNameTaken = "MODE_NAME_TAKEN"
    case invalidRequest = "INVALID_REQUEST"
    case rateLimited = "RATE_LIMITED"
    case timeout = "TIMEOUT"
}

// MARK: - Envelope

struct APIError: Codable, Sendable {
    let code: String
    let message: String
    let hint: String?
}

/// Universal failure envelope: `{ok:false, error:{...}}` returned with HTTP 200
/// so MCP wrappers can surface the error text via `isError:true`.
struct APIFailureEnvelope: Codable, Sendable {
    let ok: Bool
    let error: APIError

    init(code: LocalAPIErrorCode, message: String, hint: String? = nil) {
        self.ok = false
        self.error = APIError(code: code.rawValue, message: message, hint: hint)
    }
}

// MARK: - /health

struct HealthProviderStatus: Codable, Sendable {
    let id: String
    /// Whether a usable API key is present (for providers that require one).
    /// Always `true` for keyless providers like `hyperwhisper`.
    let key_present: Bool
    /// Whether the most recent health probe succeeded.
    let reachable: Bool
    /// Raw health status string (e.g. "healthy", "unauthorized", "unreachable", "unknown", "checking", "notInstalled").
    let status: String
}

struct HealthLocalModelEntry: Codable, Sendable {
    let id: String
    let displayName: String
    let installed: Bool
}

struct HealthLocalModels: Codable, Sendable {
    let whisper: [HealthLocalModelEntry]
    let parakeet: [HealthLocalModelEntry]
    let qwen3_asr: [HealthLocalModelEntry]
    let apple_speech: [HealthLocalModelEntry]
    let local_llm: [HealthLocalModelEntry]
}

struct HealthResponse: Codable, Sendable {
    let ok: Bool
    let app_version: String
    let api_version: Int
    let port: UInt16
    let pid: Int32
    let providers: [HealthProviderStatus]
    let post_processing_providers: [HealthProviderStatus]
    let local_models: HealthLocalModels
}

// MARK: - /models

struct ModelEntry: Codable, Sendable {
    let id: String
    let kind: String          // "voice" | "text"
    let provider: String      // "openai", "local", etc.
    let displayName: String
    let installed: Bool
    let size_mb: Double?
}

struct ModelsListResponse: Codable, Sendable {
    let ok: Bool
    let models: [ModelEntry]
}

// MARK: - /modes

/// Full Mode JSON shape — every attribute on the Mode Core Data entity (v26).
/// Keys are camelCase, matching the entity attribute names. Used for both
/// list/get responses and create/update request bodies.
struct ModeDTO: Codable, Sendable {
    let id: String?
    let name: String
    let preset: String
    let language: String
    let model: String
    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String?
    let userSystemPrompt: String?
    let isDefault: Bool?
    let isSystemProvided: Bool?
    let sortOrder: Int?
    let createdDate: Date?
    let modifiedDate: Date?
    let languageModel: String?
    let cloudTranscriptionModel: String?
    let cloudTranscriptionDomain: String?
    let cloudProvider: String?
    let postProcessingMode: Int?
    let postProcessingProvider: String?
    let englishSpelling: String?
    let useStreamingTranscription: Bool?
    let cloudAccuracyTier: String?
    let removeTrailingPeriod: Bool?
    let enableScreenOCR: Bool?
    let geminiCustomPrompt: String?
    let cloudPostProcessingModel: String?
}

/// Partial Mode body used by `PATCH /modes/{id}`. All fields optional —
/// any present key replaces the stored value; absent keys are left untouched.
struct ModePatchDTO: Codable, Sendable {
    let name: String?
    let preset: String?
    let language: String?
    let model: String?
    let punctuation: Bool?
    let capitalization: Bool?
    let profanityFilter: Bool?
    let customInstructions: String?
    let userSystemPrompt: String?
    let isDefault: Bool?
    let sortOrder: Int?
    let languageModel: String?
    let cloudTranscriptionModel: String?
    let cloudTranscriptionDomain: String?
    let cloudProvider: String?
    let postProcessingMode: Int?
    let postProcessingProvider: String?
    let englishSpelling: String?
    let useStreamingTranscription: Bool?
    let cloudAccuracyTier: String?
    let removeTrailingPeriod: Bool?
    let enableScreenOCR: Bool?
    let geminiCustomPrompt: String?
    let cloudPostProcessingModel: String?
}

struct ModesListResponse: Codable, Sendable {
    let ok: Bool
    let modes: [ModeDTO]
}

struct ModeResponse: Codable, Sendable {
    let ok: Bool
    let mode: ModeDTO
}

struct OKResponse: Codable, Sendable {
    let ok: Bool
}

// MARK: - /transcribe

struct TranscribeRequest: Codable, Sendable {
    /// Absolute path to a readable audio file. Mutually exclusive with
    /// `audio_base64`.
    let file: String?
    /// Base64-encoded audio payload (≤ ~25 MiB recommended). Mutually exclusive
    /// with `file`. When set, `mime_type` should also be set so the server
    /// can pick a sensible extension for the temporary file it writes.
    let audio_base64: String?
    /// MIME type of `audio_base64` (e.g. "audio/wav", "audio/m4a", "audio/mpeg").
    let mime_type: String?
    /// Saved mode used as the baseline. Engine/model/language fields below
    /// (if present) OVERRIDE the corresponding mode fields per-call — this is
    /// the "mixed" form: pick a mode, tweak one knob.
    let mode_id: String?
    let engine: String?
    let model: String?
    let language: String?
    /// Opt-in caption timestamps (local Whisper only). `["segment"]`, `["word"]`,
    /// or both. Absent = text only (zero change to existing callers / latency).
    /// Mirrors OpenAI `whisper-1` `verbose_json`'s `timestamp_granularities`.
    let timestamp_granularities: [String]?
}

struct TranscribeTimings: Codable, Sendable {
    let load_ms: Int
    let decode_ms: Int
}

/// One segment, shaped to match OpenAI `verbose_json` `segments[]` (subset).
/// Times are float seconds; t=0 = start of the audio whisper transcribed.
struct TranscribeSegment: Codable, Sendable {
    let id: Int
    let start: Double
    let end: Double
    let text: String
}

/// One word, shaped to match OpenAI `verbose_json` `words[]`. Approximate
/// (non-DTW) timing; seconds.
struct TranscribeWord: Codable, Sendable {
    let word: String
    let start: Double
    let end: Double
}

struct TranscribeResponse: Codable, Sendable {
    let ok: Bool
    let text: String
    let engine: String
    let model: String
    let language: String?
    let timings: TranscribeTimings
    let latency_ms: Int
    /// The string the timestamps align to (uncleaned). Present only when
    /// timestamps are returned; absent (nil → omitted) otherwise.
    let raw_text: String?
    /// Present only when segment timestamps were requested AND the engine
    /// produced them (local Whisper). Graceful omission otherwise.
    let segments: [TranscribeSegment]?
    /// Present only when word timestamps were requested AND produced.
    let words: [TranscribeWord]?
}

// MARK: - /post-process

struct PostProcessRequest: Codable, Sendable {
    let text: String
    /// Saved mode used as the baseline (preset/provider/model/customInstructions
    /// are pulled from it). Optional — if absent, the request must supply at
    /// least `preset` or `prompt`.
    let mode_id: String?
    /// Preset name: "hyper", "note", "email", "commit". Mutually exclusive
    /// with `prompt` — passing both is an INVALID_REQUEST.
    let preset: String?
    /// Free-form system prompt; sets the synthesized mode's `customInstructions`.
    let prompt: String?
    /// Provider id (PostProcessingProvider rawValue). Optional override.
    let provider: String?
    /// Model id within the provider (e.g. "gpt-4o-mini"). Optional override.
    let model: String?
}

struct PostProcessResponse: Codable, Sendable {
    let ok: Bool
    let text: String
    let provider: String
    let model: String
    let preset: String
    let latency_ms: Int
    /// `true` when an LLM actually ran and produced post-processed text.
    /// `false` when a failure path (bad key, model/network error, offline)
    /// returned the raw input unchanged — `ok` stays `true` for graceful
    /// degradation, so callers must read this to know post-processing happened.
    let post_processed: Bool
}

// MARK: - /recordings

struct RecordingDTO: Codable, Sendable {
    let id: String
    let text: String
    let postProcessedText: String?
    let transcribedText: String?
    let date: Date
    let duration: Double
    let mode: String?
    let transcriptionProvider: String?
    let postProcessingProvider: String?
    let status: String?
    let audioFilePath: String?
}

struct RecordingsListResponse: Codable, Sendable {
    let ok: Bool
    let total: Int
    let returned: Int
    let recordings: [RecordingDTO]
}

struct RecordingResponse: Codable, Sendable {
    let ok: Bool
    let recording: RecordingDTO
}

// MARK: - Port file

/// Written to ~/Library/Application Support/HyperWhisper/local-api.json
/// with chmod 600. Clients (curl, future MCP wrapper) read this file to
/// find the ephemeral port and bearer token for the server.
struct LocalAPIPortFile: Codable, Sendable {
    let port: UInt16
    let pid: Int32
    let started_at: String
    let api_version: Int
    let app_version: String
    /// Bearer token required on every endpoint except `/health`. base64-url
    /// encoded, 32 random bytes (43 chars after stripping `=` padding).
    let token: String
}
