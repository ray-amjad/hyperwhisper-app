//
//  SentryService+Privacy.swift
//  hyperwhisper
//
//  The extras / log-attribute redaction allow-list (issue #805).
//  Part of `SentryService`; see SentryService.swift.
//

import Foundation
#if canImport(Sentry)
@_spi(Private) import Sentry
#endif

extension SentryService {

    // MARK: - Extras redaction (issue #805)

    /// EVERY extra and log-attribute key whose String value may leave this
    /// machine. Matched EXACTLY, case and all. A key that is not here is
    /// redacted: its String, array or dictionary value becomes `[redacted]`.
    ///
    /// This replaced a substring deny-list (`transcript` / `text` / `prompt`).
    /// That list over-matched — `transcriptionState` and `save_context`
    /// ("conTEXT") arrived as `[redacted]`, and so does any key where a word
    /// ending in "t" meets "Extension" — and under-matched:
    /// the user-typed Mode name went out as `modeName` for months (#795)
    /// because nobody had thought to add `mode`. With an allow-list a key
    /// nobody reviewed stays hidden, so a new diagnostic fails safe.
    ///
    /// **To add a key**, decide from the VALUE it holds, never from its
    /// spelling. It may go here only if no value it can ever hold is user
    /// content: nothing spoken or typed, no prompt or transcript, no file name
    /// or path, no Mode name or vocabulary, no device name a person can rename,
    /// and no raw `localizedDescription` (those embed paths and server text).
    /// A key whose value is only ever a Bool or a number does not need to be
    /// here at all — see `redactedValue(forKey:value:)`.
    ///
    /// Sent today but deliberately NOT on the list, so their String values now
    /// arrive as `[redacted]`: raw error text (`errorDescription`,
    /// `errorFailureReason`, `user_message`), microphone names
    /// (`selectedDeviceName`, `activeDeviceName`, `selectedInputDeviceName`,
    /// `activeInputDeviceName`, `availableInputDevices` and the no-speech tag
    /// `selected_input_device_name`) and device UIDs (`selectedDeviceUID`,
    /// `activeDeviceUID`, `systemDefaultDeviceUID`, `selectedInputDeviceUID`,
    /// `activeInputDeviceUID`, `systemDefaultInputDeviceUID`). A value that can
    /// hold a user-renamed device name ("Ray's AirPods"), a hardware address or
    /// raw error text with paths is personal data — Ray's ruling 2026-10-07
    /// (#1364): a hostname or device identifier that carries a person's name is
    /// personal data. The error domain/code and transport-type keys that stay
    /// on the list still identify the failure.
    ///
    /// Tags are on the list too: the Logs path (`mergeLogAttributes`) flattens
    /// tags into the same attribute bag and redacts them by the same rule. The
    /// Event path never redacts tags.
    static let allowedExtraKeys: Set<String> = [
        // Scope tags set by `initialize()`, AppDelegate and GeneralSettings.
        "architecture", "build_number", "cpu_cores", "environment", "macos_version",

        // Shared tag / extra vocabulary used across many call sites.
        "category", "component", "operation", "phase", "severity", "provider",
        "error_domain", "error_code", "error_kind", "error_type",
        "errorDomain", "errorCode", "errorType",

        // `withRecentLogs`. `recent_logs` is `AppLogger.getRecentLogs`, which
        // runs every line through `sanitizeLogLine` before it gets here.
        "recent_logs", "recent_logs_skipped",

        // AppLogger diagnostic-log-state scope extras.
        "diagnostic_logs_state", "diagnostic_logs_on_main_thread", "diagnostic_logs_outcome",
        "diagnostic_logs_fetch_ms", "diagnostic_logs_line_count", "diagnostic_logs_was_slow",
        "diagnostic_logs_exit_code",

        // Core Data save / store-load failures (AppLogger.logCoreData,
        // CoreDataSaveDiagnostics, PersistenceController). Entity and attribute
        // names come from the model, never from a row.
        "coredata_domain", "coredata_code", "coredata_error", "coredata_attribute",
        "coredata_entity", "coredata_detailed_count", "coredata_detailed",
        "save_site", "save_context", "consecutive_failures", "since_first_failure_ms",
        "pending_inserted", "pending_updated", "pending_deleted", "pending_entities",
        "store", "rowCount",

        // AutoDeleteCleanupService.
        "failedDeletions", "pathsAttempted",

        // Transcription failure (TranscriptionPipeline.transcriptionFailureExtras)
        // and its tags. `transcriptionState` is the case name only;
        // `fileExtension` is `pathExtension` alone.
        "actualProvider", "modelString", "useCloud", "modePreset", "modeIsSystemProvided",
        "language", "postProcessingMode", "postProcessingProvider", "shouldRunPostProcessing",
        "isHyperwhisperTranscription", "fileExists", "fileReadable", "fileSizeBytes",
        "fileExtension", "usedCAFFallback", "errorCategory", "errorKind", "errorRetryable",
        "errorStage", "stageElapsedMs", "totalElapsedMs", "stageTimeline",
        "transcriptionState", "recordingSessionID", "errorHttpStatus",
        "error_class", "error_stage", "error_http_status", "post_processing_preflight",

        // No-speech / empty-recording diagnostic
        // (TranscriptionDiagnosticsService.buildPayload), tags then extras.
        "diagnostic_name", "diagnostic_stage", "diagnostic_source", "provider_type",
        "cloud_provider", "local_engine", "provider_attempt_source",
        "backend_no_speech_detected", "audio_analysis_succeeded", "audio_rms_dbfs_bucket",
        "mic_boost_failed",
        // Not `selected_input_device_name`: a person can rename the microphone.
        "audio_file_exists", "audio_file_extension", "audio_file_size_bytes",
        "audio_duration_seconds", "audio_peak_dbfs", "audio_rms_dbfs", "audio_non_silent_ratio",
        "audio_decoded_sample_count", "audio_measured_sample_count", "provider_display_name",
        "backend_request_id", "backend_stt_provider", "backend_stt_model", "backend_http_status",
        "backend_response_latency_ms", "provider_attempt_ms",
        "backend_empty_transcript_without_flag", "audio_sample_rate_hz", "audio_channels",
        "audio_analysis_failure", "audio_analysis_error_domain", "audio_analysis_error_code",

        // Stop-recording stage timings (RecordingTranscriptionFlow.stageExtras).
        "stage_wav_ready_ms", "stage_file_check_ms", "stage_vad_trim_ms", "stage_create_row_ms",
        "stage_transcribe_ms", "stage_core_data_update_ms", "stage_flow_ms",
        "stage_audio_duration_seconds", "stage_duration_allowance_ms",
        "stage_effective_ui_threshold_ms", "stage_reported_at",

        // Recording-start failure (RecordingTranscriptionFlow
        // .recordingStartFailureMetadata).
        "recordingAttemptId", "recordingTriggerSource", "permissionStatus",
        "hasMicrophonePermission", "recordingLifecycleHasPermission",
        "selectedDeviceTransportType", "recordingsFolderIsDefault", "recordingsFolderIsInHome",
        "recordingsFolderWritable", "recordingsFolderExists", "recordingsFolderIsDirectory",
        "recordingsFolderFreeBytes", "isStreamingShortcutTriggered",
        "recordingLifecycleIsRecording", "toggleTaskCancelled", "recordingState",
        "mediaControlMode", "autoIncreaseMicVolume", "activeDeviceIsDefault",
        "activeDeviceTransportType", "inputSampleRateHz", "inputChannelCount", "inputBitDepth",
        "inputIsFloat", "availableInputDeviceCount", "recordingFailureStage",
        // Not the error text, device names or device UIDs: personal data, see
        // above.

        // Recording stop / audio finalization (RecordingLifecycle).
        "recordingDurationMs", "rawExists", "rawReadable", "dstExists", "rawSizeBytes",
        "volumeFreeBytes", "sessionId", "rawFileType", "dstFileType", "rawIsIncompleteName",
        "dstInDefaultRecordingsFolder", "inputVolumeScalar", "selectedInputDeviceTransportType",
        "activeInputDeviceTransportType",
        // Not the device names or hardware UIDs: personal data, see above.

        // RecordingStartTrace (`recording_start_` + fixed suffixes).
        "recording_start_state", "recording_start_attempt", "recording_start_trigger",
        "recording_start_step", "recording_start_step_index", "recording_start_step_ms",
        "recording_start_pending_step", "recording_start_pending_since_ms",
        "recording_start_outcome", "recording_start_elapsed_ms",

        // MainActorHangTrace (`main_actor_flow_` + fixed suffixes).
        "main_actor_flow_state", "main_actor_flow_flow", "main_actor_flow_step",
        "main_actor_flow_operation_id", "main_actor_flow_started_at_ms",
        "main_actor_flow_completed_at_ms", "main_actor_flow_elapsed_ms",
        "main_actor_flow_last_completed_flow", "main_actor_flow_last_completed_step",
        "main_actor_flow_last_completed_operation_id",
        "main_actor_flow_last_completed_started_at_ms", "main_actor_flow_last_completed_at_ms",
        "main_actor_flow_last_completed_elapsed_ms", "main_actor_ui_properties",
        "main_actor_ui_states", "main_actor_ui_requested_at_ms",

        // Auto-paste diagnostics (AccessibilityHelper+PasteDiagnostics). The
        // bundle ids name an app, not anything typed into it.
        "paste_outcome", "paste_target_bundle_id", "paste_had_captured_target",
        "paste_target_resolution", "paste_resolved_target_bundle_id", "paste_is_remote_desktop",
        "paste_used_focus_retry", "paste_focus_retry_succeeded", "paste_character_count",
        "paste_elapsed_ms", "paste_has_accessibility_permission", "paste_delivery_suppressed",
        "paste_reported_at",

        // Streaming client session diagnostics and captures
        // (StreamingTranscriptionClient, RecordingTranscriptionFlow+Streaming).
        "streaming_provider", "streaming_model", "streaming_stage",
        "streaming_session_elapsed_ms", "streaming_connection_elapsed_ms",
        "streaming_audio_elapsed_ms", "streaming_finals_delivered",
        "streaming_partials_delivered", "streaming_reconnect_count",
        "streaming_audio_send_failures", "streaming_close_code",
        "streaming_session_complete_received", "streaming_did_initiate_close",
        "streaming_stop_frame_send_started", "streaming_stop_frame_send_started_elapsed_ms",
        "streaming_stop_frame_sent", "streaming_stop_frame_sent_elapsed_ms",
        "streaming_post_stop_elapsed_ms", "streaming_receive_loop_active",
        "streaming_receive_pending", "streaming_receive_loop_active_at_stop",
        "streaming_receive_pending_at_stop", "streaming_post_stop_receive_attempts",
        "streaming_post_stop_receive_completions", "streaming_post_stop_receive_failures",
        "streaming_post_stop_string_frames", "streaming_post_stop_string_bytes",
        "streaming_post_stop_binary_frames", "streaming_post_stop_binary_bytes",
        "streaming_post_stop_utf8_decode_failures", "streaming_post_stop_event_session_started",
        "streaming_post_stop_event_partial", "streaming_post_stop_event_final",
        "streaming_post_stop_event_final_and_complete",
        "streaming_post_stop_event_session_complete", "streaming_post_stop_event_error",
        "streaming_post_stop_event_warning", "streaming_post_stop_event_metadata",
        "streaming_post_stop_event_ignored",
        "detail", "stopStep", "reconnectCount", "closeCode", "terminal",
        "repeatAfterTerminal", "attemptId",

        // HyperWhisper Cloud auth-recovery trace (`hwcloud_recovery_` + fixed
        // suffixes) and the post-process failure tags.
        "hwcloud_recovery_id", "hwcloud_recovery_state", "hwcloud_recovery_outcome",
        "hwcloud_recovery_send_attempts", "hwcloud_recovery_first_status",
        "hwcloud_recovery_retry_status", "hwcloud_recovery_licensed_at_send",
        "hwcloud_recovery_revalidation_valid", "hwcloud_recovery_reresolved_licensed",
        "hwcloud_recovery_identity_changed", "hwcloud_recovery_elapsed_ms",
        "server_status",

        // Local model runtimes and downloads (Qwen3AsrProvider, ParakeetProvider,
        // TranscriptionModelManager, LocalModelManager). The `qwen3_<label>_*`
        // keys are spelled out for every `Qwen3AsrProvider.label(for:)` case; an
        // `unknown(N)` label falls back to redaction.
        "qwen3_load_duration_ms", "qwen3_load_attempts", "qwen3_loaded_compute_units",
        "qwen3_first_error_domain", "qwen3_first_error_code",
        "qwen3_all_error_domain", "qwen3_all_error_code",
        "qwen3_cpuAndGPU_error_domain", "qwen3_cpuAndGPU_error_code",
        "qwen3_cpuOnly_error_domain", "qwen3_cpuOnly_error_code",
        "qwen3_cpuAndNeuralEngine_error_domain", "qwen3_cpuAndNeuralEngine_error_code",
        "parakeet_load_stage", "parakeet_load_duration_ms", "parakeet_load_generation",
        "parakeet_another_version_was_loading", "parakeet_cache_present_after_failure",
        "parakeet_error_code", "parakeet_model_version", "parakeet_previous_version",
        "parakeet_error_domain", "parakeet_model_id_length", "parakeet_unknown_model_version",
        "parakeet_failure_kind", "parakeet_unknown_model_class",
        // `LocalModelLoadFailureDetail` (#1017): fixed slugs, numbers and NSError
        // domains only, never a path, server text or `localizedDescription`.
        "parakeet_hf_error_case", "parakeet_http_status",
        "parakeet_underlying_error_domain", "parakeet_underlying_error_code",
        "parakeet_last_phase", "parakeet_files_completed", "parakeet_files_total",
        "parakeet_compiling_model",
        // NemotronProvider "Nemotron runtime load failed" (#1980): the variant
        // slug, the NSError domain/code and the same `LocalModelLoadFailureDetail`
        // fields. No phase keys: the Nemotron load passes no progress handler.
        "nemotron_variant", "nemotron_error_domain", "nemotron_error_code",
        "nemotron_hf_error_case", "nemotron_http_status",
        "nemotron_underlying_error_domain", "nemotron_underlying_error_code",
        "modelId", "target", "model_id",
        // Not `user_message`: LocalModelManager's message embeds a raw
        // `localizedDescription`.

        // Licence validation (LicenseNetworkService). `license_reason` is the
        // server's own reason slug.
        "endpoint", "status", "license_reason", "isLaunchValidation",
        "validation_elapsed_ms", "request_timeout_ms", "retry_max_attempts",
        "cache_usable_at_request_start", "reason", "failure_kind", "failure_code",
        "validation_mode", "request_policy"
    ]

    /// Whether `beforeSend` and the Logs path redact this key's content.
    /// True for every key that is not EXACTLY on `allowedExtraKeys`.
    static func isRedactedExtraKey(_ key: String) -> Bool {
        !allowedExtraKeys.contains(key)
    }

    /// Redact a key that is not on the allow-list ONLY when its value could
    /// carry content. A Bool or a number is a measurement and cannot hold
    /// speech, so it survives under any key; a String, an array, a dictionary,
    /// NSNull or anything else becomes `[redacted]`. Issue #684: a Bool once
    /// shipped as `[redacted]`.
    static func redactedValue(forKey key: String, value: Any) -> Any {
        guard isRedactedExtraKey(key) else { return value }
        switch value {
        case is Bool, is NSNumber: return value
        default: return "[redacted]"
        }
    }

    #if canImport(Sentry)
    // MARK: - The last gate before the network

    /// `options.beforeSend`, set by `initialize(dsn:environment:)`. Every event
    /// the SDK sends passes through here on its way out, including the ones it
    /// builds for itself (a failed request, an app hang, a recovered crash).
    /// Internal (not private) so it stays reachable the way
    /// `isRedactedExtraKey` is.
    static func sanitize(_ event: Event) -> Event? {
        // LAST GATE BEFORE THE NETWORK
        // The user can turn error logging off at any moment, including
        // while an event the SDK built for itself is already in flight —
        // a failed request, an app hang, a crash report recovered from
        // the last session. None of those pass through capture(),
        // and shutdown() cannot reach one that is mid-air. beforeSend
        // runs on the way out of every event, so it is the last place to
        // drop it. Issue #551.
        guard AppLogger.isErrorLoggingEnabled else { return nil }

        // Remove breadcrumbs to avoid leaking text content via logs
        // Note: We still collect breadcrumbs locally for debugging flow,
        // but strip them before sending to Sentry for privacy
        event.breadcrumbs = nil
        // Drop any suspicious extras
        var sanitized = event.extra ?? [:]
        for (key, value) in sanitized {
            sanitized[key] = Self.redactedValue(forKey: key, value: value)
        }
        event.extra = sanitized

        // APP HANG GROUPING (issue #683)
        // An SDK-raised AppHang never reaches capture(error:), so this is
        // the only hook that can fingerprint it. Without one, every hang
        // with no HyperWhisper frame below HyperWhisperApp.$main merges
        // into HYPERWHISPER-F7 — 191 events, 7 unrelated blocking sites.
        // Both values are code identifiers read out of a stack frame, so
        // neither can hold anything the user typed, said or named.
        //
        // EVERY decision is `hangGrouping`'s, including whether to write
        // anything at all — it returns nil for an event that is not an
        // app hang. The guard is an argument, not an `if` wrapped round
        // the call, so a unit test can execute it; this function takes the
        // Sentry package's `Event`, which `hyperwhisperTests` does not
        // link, so no test can execute this. What is left here is two
        // reads and two writes.
        let hangFrames: [(image: String, symbol: String)] =
            (event.exceptions?.first?.stacktrace?.frames ?? []).map {
                (image: $0.package ?? "", symbol: $0.function ?? "")
            }
        if let grouping = Self.hangGrouping(
            mechanismType: event.exceptions?.first?.mechanism?.type,
            imagesAndSymbols: hangFrames,
            existingTags: event.tags
        ) {
            event.fingerprint = grouping.fingerprint
            event.tags = grouping.tags
        }

        return event
    }

    /// `options.beforeSendLog`, set by `initialize(dsn:environment:)`. The Logs
    /// pipeline does not use `beforeSend`, so the opt-out is checked again here.
    static func sanitize(_ log: SentryLog) -> SentryLog? {
        guard AppLogger.isErrorLoggingEnabled else { return nil }
        return log
    }
    #endif
}
