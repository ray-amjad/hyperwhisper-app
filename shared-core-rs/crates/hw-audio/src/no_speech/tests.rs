use super::*;

// ===========================================================================
// Rounding
// ===========================================================================

/// The pinned midpoint. Windows' `Math.Round(x, 2)` is banker's rounding, so it
/// answers `0.12` here and `-0.12` below; Swift's `(x * 100).rounded() / 100`
/// and this both answer `0.13`. Away from zero is the shared behaviour — if this
/// test ever changes, every rounded value in the diagnostic moves with it.
#[test]
fn rounds_a_midpoint_away_from_zero() {
    assert_eq!(round_to(0.125, 2), 0.13);
    assert_eq!(round_to(-0.125, 2), -0.13);
}

#[test]
fn rounds_non_midpoints_normally() {
    assert_eq!(round_to(0.1234, 2), 0.12);
    assert_eq!(round_to(0.1284, 2), 0.13);
    assert_eq!(round_to(-6.020599913279624, 2), -6.02);
}

#[test]
fn rounding_passes_non_finite_through() {
    assert!(round_to(f64::NAN, 2).is_nan());
    assert_eq!(round_to(f64::INFINITY, 2), f64::INFINITY);
}

// ===========================================================================
// to_dbfs
// ===========================================================================

#[test]
fn full_scale_is_zero_dbfs() {
    assert_eq!(to_dbfs(1.0), 0.0);
}

#[test]
fn half_scale_is_about_minus_six_dbfs() {
    assert_eq!(to_dbfs(0.5), -6.02);
}

#[test]
fn silence_and_nonsense_fall_to_the_floor() {
    assert_eq!(to_dbfs(0.0), MINIMUM_DBFS);
    assert_eq!(to_dbfs(-1.0), MINIMUM_DBFS);
    assert_eq!(to_dbfs(f64::NAN), MINIMUM_DBFS);
    assert_eq!(to_dbfs(f64::INFINITY), MINIMUM_DBFS);
    assert_eq!(to_dbfs(f64::NEG_INFINITY), MINIMUM_DBFS);
}

// ===========================================================================
// bucket_dbfs — floors, does not truncate
// ===========================================================================

/// Truncation would answer `"-35dbfs"` here. The whole point of the bucket is
/// that a value belongs to the step at or below it, in both directions.
#[test]
fn buckets_negatives_downward() {
    assert_eq!(bucket_dbfs(-38.2), "-40dbfs");
    assert_eq!(bucket_dbfs(-0.5), "-5dbfs");
    assert_eq!(bucket_dbfs(-39.9999), "-40dbfs");
}

#[test]
fn a_value_on_a_step_stays_in_its_own_bucket() {
    assert_eq!(bucket_dbfs(-40.0), "-40dbfs");
    assert_eq!(bucket_dbfs(0.0), "0dbfs");
    assert_eq!(bucket_dbfs(-35.0), "-35dbfs");
}

#[test]
fn the_floor_and_below_bucket_as_silent() {
    assert_eq!(bucket_dbfs(MINIMUM_DBFS), "silent");
    assert_eq!(bucket_dbfs(-125.0), "silent");
    // Never a `"NaNdbfs"` tag — see the module note on non-finite input.
    assert_eq!(bucket_dbfs(f64::NAN), "silent");
    assert_eq!(bucket_dbfs(f64::NEG_INFINITY), "silent");
}

// ===========================================================================
// accumulate / summarize
// ===========================================================================

#[test]
fn an_empty_accumulation_summarizes_to_the_floor_without_dividing() {
    let summary = summarize(SignalAccumulation::default());
    assert_eq!(summary.peak_dbfs, MINIMUM_DBFS);
    assert_eq!(summary.rms_dbfs, MINIMUM_DBFS);
    assert_eq!(summary.non_silent_ratio, 0.0);
}

#[test]
fn accumulate_counts_peak_rms_and_the_silence_threshold() {
    // 0.009 is below the threshold, 0.01 is exactly on it (>= counts it).
    let acc = accumulate(
        SignalAccumulation::default(),
        &[0.0, -0.009, 0.01, -0.5, 0.25],
    );
    assert_eq!(acc.sample_count, 5);
    assert_eq!(acc.non_silent_count, 3);
    assert_eq!(acc.peak, 0.5);

    let summary = summarize(acc);
    assert_eq!(summary.peak_dbfs, -6.02);
    assert_eq!(summary.non_silent_ratio, 0.6);
}

#[test]
fn accumulate_folds_across_buffers_like_a_chunked_decode_loop() {
    let one_pass = accumulate(SignalAccumulation::default(), &[0.1, 0.2, 0.3, 0.4]);
    let chunked = accumulate(
        accumulate(SignalAccumulation::default(), &[0.1, 0.2]),
        &[0.3, 0.4],
    );
    assert_eq!(one_pass, chunked);
}

#[test]
fn a_full_scale_square_wave_measures_zero_dbfs_rms() {
    let acc = accumulate(SignalAccumulation::default(), &[1.0, -1.0, 1.0, -1.0]);
    let summary = summarize(acc);
    assert_eq!(summary.rms_dbfs, 0.0);
    assert_eq!(summary.peak_dbfs, 0.0);
    assert_eq!(summary.non_silent_ratio, 1.0);
}

/// 1/32 is exactly representable, so `ratio * 10000` is exactly `312.5` — the
/// midpoint. Windows would report `0.0312`.
#[test]
fn the_non_silent_ratio_rounds_away_from_zero_at_a_midpoint() {
    let summary = summarize(SignalAccumulation {
        sample_count: 32,
        non_silent_count: 1,
        sum_squares: 0.0,
        peak: 0.0,
    });
    assert_eq!(summary.non_silent_ratio, 0.0313);
}

/// A `NaN` sample poisons `sum_squares`; the summary must still be a reportable
/// number rather than a `NaN` extra and a `"NaNdbfs"` tag.
#[test]
fn a_poisoned_sum_of_squares_floors_instead_of_propagating() {
    let acc = accumulate(SignalAccumulation::default(), &[f32::NAN, 0.5]);
    assert!(acc.sum_squares.is_nan());
    let summary = summarize(acc);
    assert_eq!(summary.rms_dbfs, MINIMUM_DBFS);
    assert_eq!(summary.peak_dbfs, -6.02);
}

// ===========================================================================
// classify — arm order is the contract
// ===========================================================================

/// Arm 1 before arm 2: a failed analysis reports the full no-speech diagnostic
/// even when the (meaningless) sample count is zero.
#[test]
fn a_failed_analysis_reports_no_speech_even_with_zero_samples() {
    assert_eq!(
        classify(NoSpeechInput {
            analysis_succeeded: false,
            decoded_sample_count: Some(0),
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::NoSpeech
    );
}

/// Arm 2 before arms 3-5: an empty recording keeps its own identity whatever the
/// provider said.
#[test]
fn zero_decoded_samples_is_an_empty_recording() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(0),
            empty_transcript_without_flag: true,
            backend_no_speech_detected: true,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::EmptyRecording
    );
}

/// `None` is unknown, never empty — it must fall through to the signal arms.
#[test]
fn an_unknown_sample_count_is_not_an_empty_recording() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: None,
            peak_dbfs: -60.0,
            non_silent_ratio: 0.0,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::Skip
    );
}

/// Arm 3 before arm 4: a provider that returned nothing without saying so is an
/// anomaly even when the audio is dead silent.
#[test]
fn an_empty_transcript_without_the_flag_beats_confirmed_silence() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(16_000),
            empty_transcript_without_flag: true,
            peak_dbfs: -90.0,
            non_silent_ratio: 0.0,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::NoSpeech
    );
}

#[test]
fn confirmed_dead_silence_is_skipped() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(16_000),
            peak_dbfs: -60.0,
            non_silent_ratio: 0.0,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::Skip
    );
}

/// The peak comparison is strict, so a clip exactly on the threshold is still
/// reported.
#[test]
fn a_peak_exactly_on_the_confirmed_silence_threshold_is_reported() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(16_000),
            peak_dbfs: CONFIRMED_SILENCE_PEAK_DBFS,
            non_silent_ratio: 0.0,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::NoSpeech
    );
}

/// Both low-signal comparisons are inclusive.
#[test]
fn a_backend_confirmed_clip_exactly_on_both_thresholds_is_skipped() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(16_000),
            backend_no_speech_detected: true,
            peak_dbfs: -20.0,
            rms_dbfs: LOW_SIGNAL_RMS_DBFS,
            non_silent_ratio: LOW_SIGNAL_NON_SILENT_RATIO,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::Skip
    );
}

/// The real HYPERWHISPER-PA/-QB/-VY sample the thresholds were widened for.
#[test]
fn the_incident_sample_is_skipped() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(48_000),
            backend_no_speech_detected: true,
            peak_dbfs: -25.0,
            rms_dbfs: -39.64,
            non_silent_ratio: 0.046,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::Skip
    );
}

/// Both conditions must hold — the arm is an AND, not an OR. Either one alone
/// leaves a potential backend-disagreement anomaly reportable.
#[test]
fn one_low_signal_condition_alone_does_not_skip() {
    let quiet_but_busy = NoSpeechInput {
        decoded_sample_count: Some(16_000),
        backend_no_speech_detected: true,
        peak_dbfs: -20.0,
        rms_dbfs: -45.0,
        non_silent_ratio: 0.07,
        ..NoSpeechInput::default()
    };
    assert_eq!(classify(quiet_but_busy), NoSpeechOutcome::NoSpeech);

    let sparse_but_loud = NoSpeechInput {
        rms_dbfs: -30.0,
        non_silent_ratio: 0.02,
        ..quiet_but_busy
    };
    assert_eq!(classify(sparse_but_loud), NoSpeechOutcome::NoSpeech);
}

/// The cohort the diagnostic exists to catch: healthy speech energy, provider
/// says no speech.
#[test]
fn a_loud_clip_the_backend_called_empty_is_reported() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(48_000),
            backend_no_speech_detected: true,
            peak_dbfs: -18.47,
            rms_dbfs: -20.0,
            non_silent_ratio: 0.3,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::NoSpeech
    );
}

/// Without the backend flag, arm 5 cannot fire at all — a quiet clip the
/// provider never called empty stays reportable.
#[test]
fn a_quiet_clip_without_the_backend_flag_is_reported() {
    assert_eq!(
        classify(NoSpeechInput {
            decoded_sample_count: Some(16_000),
            backend_no_speech_detected: false,
            peak_dbfs: -20.0,
            rms_dbfs: -45.0,
            non_silent_ratio: 0.01,
            ..NoSpeechInput::default()
        }),
        NoSpeechOutcome::NoSpeech
    );
}

// ===========================================================================
// Grouping
// ===========================================================================

fn mode(provider_type: Option<&str>, cloud: Option<&str>, engine: Option<&str>) -> ModeIdentity {
    ModeIdentity {
        provider_type: provider_type.map(str::to_string),
        cloud_provider: cloud.map(str::to_string),
        local_engine: engine.map(str::to_string),
    }
}

/// "No mode at all" is a different fact from "a mode whose provider type was
/// never written", and the fingerprints have to say so.
#[test]
fn an_absent_mode_is_unknown_not_local() {
    assert_eq!(
        build_fingerprint("transcription-no-speech", "stop", "pipeline", None),
        vec!["transcription-no-speech", "stop", "pipeline", "unknown", "none"]
    );

    let never_written = mode(None, None, Some("parakeet"));
    assert_eq!(
        build_fingerprint(
            "transcription-no-speech",
            "stop",
            "pipeline",
            Some(&never_written)
        ),
        vec![
            "transcription-no-speech",
            "stop",
            "pipeline",
            "local",
            "parakeet"
        ]
    );
}

/// A local mode's stale cloud vendor must not reach the fingerprint or the tag —
/// that staleness split one condition across four Sentry issues.
#[test]
fn a_local_mode_masks_its_stale_cloud_vendor() {
    let stale = mode(Some("local"), Some("groq"), Some("whisper"));
    assert_eq!(
        build_fingerprint("transcription-no-speech", "stop", "pipeline", Some(&stale))[3..],
        ["local", "whisper"]
    );
    assert_eq!(resolve_cloud_provider_tag(Some(&stale)), "none");
}

#[test]
fn a_cloud_mode_groups_per_vendor() {
    let cloud = mode(Some("cloud"), Some("deepgram"), None);
    assert_eq!(
        build_fingerprint("transcription-no-speech", "stop", "pipeline", Some(&cloud))[3..],
        ["cloud", "deepgram"]
    );
    assert_eq!(resolve_cloud_provider_tag(Some(&cloud)), "deepgram");

    // A cloud mode with no vendor written still has to say something.
    let vendorless = mode(Some("cloud"), None, None);
    assert_eq!(
        build_fingerprint("transcription-no-speech", "stop", "pipeline", Some(&vendorless))[3..],
        ["cloud", "none"]
    );
}

/// The routing sites compare case-insensitively, so this does too. Anything that
/// is not "cloud" — including the empty string — routes local.
#[test]
fn only_cloud_is_cloud_and_the_match_is_case_insensitive() {
    assert!(!is_local_mode(Some(&mode(Some("CLOUD"), None, None))));
    assert!(!is_local_mode(Some(&mode(Some("Cloud"), None, None))));
    assert!(is_local_mode(Some(&mode(Some(""), None, None))));
    assert!(is_local_mode(Some(&mode(Some("cloudy"), None, None))));
    assert!(is_local_mode(Some(&mode(None, None, None))));
    assert!(is_local_mode(None));
}

#[test]
fn a_blank_local_engine_reads_as_none() {
    assert_eq!(resolve_local_engine(Some(&mode(None, None, None))), "none");
    assert_eq!(
        resolve_local_engine(Some(&mode(None, None, Some("   ")))),
        "none"
    );
    assert_eq!(resolve_local_engine(None), "none");
    // Written values are reported as written, never normalized.
    assert_eq!(
        resolve_local_engine(Some(&mode(None, None, Some("Parakeet V2")))),
        "Parakeet V2"
    );
}

/// The root is the caller's and travels through untouched: Windows and macOS
/// deliberately report different ones so their issues never merge.
#[test]
fn the_fingerprint_root_is_never_rewritten() {
    assert_eq!(
        build_fingerprint("macos-transcription-empty-recording", "stop", "flow", None)[0],
        "macos-transcription-empty-recording"
    );
}
