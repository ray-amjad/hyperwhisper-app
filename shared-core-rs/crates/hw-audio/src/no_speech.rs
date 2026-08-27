//! The no-speech diagnostic: measurement, classification and Sentry grouping.
//!
//! # Why pure functions and not an object
//!
//! There is no long-lived Rust-owned accumulator here. UniFFI object methods
//! take `&self`, so a `push(&mut self)` sample sink is not expressible across
//! the boundary at all, and pushing every sample over FFI would be a call per
//! sample anyway. The head therefore runs its own decode loop — it already owns
//! the decoder (NAudio on Windows, `AudioConverter` on macOS) — and reports what
//! it counted as a [`SignalAccumulation`]. Everything downstream of that count
//! is shared: [`summarize`] turns it into dBFS, [`classify`] decides what to
//! report, and [`build_fingerprint`] decides how it groups.
//!
//! The head only needs one constant from here to run its loop:
//! [`SILENCE_THRESHOLD`], exported as an accessor so no head re-declares it.
//!
//! # Rounding
//!
//! Every rounding here is **away from zero** at the midpoint, matching Swift's
//! `(x * 100).rounded() / 100` and Rust's `f64::round`. Windows used
//! `Math.Round(x, 2)`, which is banker's rounding (`MidpointRounding.ToEven`),
//! so a value landing exactly on a midpoint moves by one unit in the last place
//! when Windows adopts this. That is a deliberate choice: two platforms cannot
//! both be right, and the away-from-zero form is the one two of the three
//! implementations already used. The affected quantities are a Sentry extra
//! (`audio_rms_dbfs`) and a bucketing input, never a threshold comparison that
//! could flip an outcome by more than 0.005 dB.
//!
//! # Non-finite input
//!
//! A decoder that emits `NaN` or an infinity is not a real recording, but it
//! must not produce a garbage Sentry tag (`"NaNdbfs"`) or a panic — the
//! workspace builds with `panic = "abort"`. Non-finite readings collapse to the
//! [`MINIMUM_DBFS`] floor and to the `"silent"` bucket. This is a deliberate
//! divergence from the C# original, which would have propagated the `NaN` into
//! the tag.

// ===========================================================================
// Constants
// ===========================================================================

/// Absolute sample amplitude at or above which a sample counts as non-silent.
///
/// `f32` on purpose: both heads compare a `float` sample against a `float`
/// literal (`0.01f` / `Float = 0.01`), and `0.01` is not exactly representable.
/// Widening the comparison to `f64` would move the boundary.
pub const SILENCE_THRESHOLD: f32 = 0.01;

/// The dBFS value reported for digital silence, and the floor of the scale.
pub const MINIMUM_DBFS: f64 = -120.0;

/// Below this peak, with a zero non-silent ratio, the clip is confirmed dead
/// silence and nothing is reported.
pub const CONFIRMED_SILENCE_PEAK_DBFS: f64 = -50.0;

/// Backend-confirmed low-signal skip: BOTH this and
/// [`LOW_SIGNAL_NON_SILENT_RATIO`] must hold. An OR would let a single quiet
/// reading skip capture on its own, suppressing the genuine
/// backend-disagreement anomalies the diagnostic exists to catch.
///
/// Widened from -50.0 dBFS / 0.02 against the real HYPERWHISPER-PA/-QB/-VY
/// samples (RMS -39.64 dBFS at ratio 0.046, backend-confirmed) with a modest
/// margin rather than a doubling, so a soft-spoken user with meaningfully more
/// non-silent signal is still captured.
pub const LOW_SIGNAL_RMS_DBFS: f64 = -38.0;

/// See [`LOW_SIGNAL_RMS_DBFS`].
pub const LOW_SIGNAL_NON_SILENT_RATIO: f64 = 0.06;

// ===========================================================================
// Measurement
// ===========================================================================

/// What the head's decode loop counted. Every field is a running total over the
/// decoded samples, in the head's own decode format.
///
/// `sum_squares` and `peak` are over the **absolute** amplitude, so both are
/// non-negative for any real input.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct SignalAccumulation {
    /// Samples the decoder actually produced. `0` is the only honest "the
    /// recorder captured nothing" signal — see [`NoSpeechInput`].
    pub sample_count: u64,
    /// Samples whose absolute amplitude reached [`SILENCE_THRESHOLD`].
    pub non_silent_count: u64,
    /// Sum of `amplitude * amplitude` over every sample.
    pub sum_squares: f64,
    /// Largest absolute amplitude seen.
    pub peak: f64,
}

/// The measurements a diagnostic reports and classifies on.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AudioSignalSummary {
    pub peak_dbfs: f64,
    pub rms_dbfs: f64,
    /// Fraction of samples that reached [`SILENCE_THRESHOLD`], rounded to four
    /// decimal places.
    pub non_silent_ratio: f64,
}

impl Default for AudioSignalSummary {
    /// The "nothing measured" summary: both levels at the floor, no signal.
    fn default() -> Self {
        AudioSignalSummary {
            peak_dbfs: MINIMUM_DBFS,
            rms_dbfs: MINIMUM_DBFS,
            non_silent_ratio: 0.0,
        }
    }
}

/// Fold one buffer of samples into a running accumulation.
///
/// This is the canonical loop both heads run inline — it is not exported over
/// UniFFI, because crossing the boundary once per decode chunk to copy the
/// samples would cost more than the arithmetic saves. It exists so the loop has
/// exactly one definition to test against, and so a head can be checked for
/// drift by comparing against it. Nothing calls it in production, by design; a
/// head that diverges from it is a bug in the head.
///
/// # The peak rule is part of the contract
///
/// The peak takes a sample only when `amplitude > peak`, which is **false** for
/// `NaN` — so a non-finite sample is ignored and cannot poison the peak. A head
/// that writes this as a max-of-two helper instead does NOT match: C#
/// `Math.Max` and Swift `max` both PROPAGATE `NaN`, which would floor the peak
/// to [`MINIMUM_DBFS`] and silently change which arm of [`classify`] fires. Both
/// heads therefore write the comparison, not the helper —
/// `TranscriptionDiagnosticsService.AnalyzeAudioFile` on Windows and
/// `analyzeAudioFile` on macOS.
///
/// `sum_squares` is a running sum and is deliberately left to poison, because
/// [`summarize`] floors it — see the module note on non-finite input.
pub fn accumulate(acc: SignalAccumulation, samples: &[f32]) -> SignalAccumulation {
    let mut acc = acc;
    for sample in samples {
        let amplitude = sample.abs();
        // The silence test stays in `f32`, where both heads make it — see
        // `SILENCE_THRESHOLD`. Everything else widens, as both heads do.
        if amplitude >= SILENCE_THRESHOLD {
            acc.non_silent_count += 1;
        }
        let amplitude = f64::from(amplitude);
        if amplitude > acc.peak {
            acc.peak = amplitude;
        }
        acc.sum_squares += amplitude * amplitude;
        acc.sample_count += 1;
    }
    acc
}

/// Turn a raw count into the reported measurements.
///
/// Guards `sample_count == 0` before dividing: an empty accumulation summarizes
/// to the silent floor rather than to `NaN`.
pub fn summarize(acc: SignalAccumulation) -> AudioSignalSummary {
    if acc.sample_count == 0 {
        return AudioSignalSummary {
            peak_dbfs: to_dbfs(acc.peak),
            ..AudioSignalSummary::default()
        };
    }

    let count = acc.sample_count as f64;
    // `f64::max` returns the non-NaN operand, so a poisoned `sum_squares`
    // floors to 0 here rather than reaching `sqrt` and propagating.
    let mean_square = (acc.sum_squares / count).max(0.0);
    let rms = mean_square.sqrt();
    let ratio = acc.non_silent_count as f64 / count;

    AudioSignalSummary {
        peak_dbfs: to_dbfs(acc.peak),
        rms_dbfs: to_dbfs(rms),
        non_silent_ratio: round_to(ratio, 4),
    }
}

/// Convert a linear amplitude (0..=1) to dBFS, rounded to two decimals.
///
/// Zero, negative and non-finite input return the [`MINIMUM_DBFS`] floor.
pub fn to_dbfs(linear: f64) -> f64 {
    if !linear.is_finite() || linear <= 0.0 {
        return MINIMUM_DBFS;
    }

    round_to(20.0 * linear.log10(), 2)
}

/// Bucket a dBFS value to the 5 dB step at or below it (`-38.2` -> `"-40dbfs"`)
/// for use as a low-cardinality Sentry tag.
///
/// A raw float as a tag has near-100% cardinality — every event gets its own
/// bucket of one — which defeats the faceting that promoting it from an extra
/// was for. Note this floors, it does not truncate: negatives bucket *downward*.
pub fn bucket_dbfs(dbfs: f64) -> String {
    if !dbfs.is_finite() || dbfs <= MINIMUM_DBFS {
        return "silent".to_string();
    }

    // `as i64` saturates in Rust rather than wrapping, and the input is finite
    // and above the floor here, so this cannot produce a surprise.
    let bucket = ((dbfs / 5.0).floor() * 5.0) as i64;
    format!("{bucket}dbfs")
}

/// Round half away from zero at `digits` decimal places — see the module note
/// on rounding.
fn round_to(value: f64, digits: u32) -> f64 {
    if !value.is_finite() {
        return value;
    }
    let factor = 10f64.powi(digits as i32);
    (value * factor).round() / factor
}

// ===========================================================================
// Classification
// ===========================================================================

/// What a no-speech failure is reported as, if anything.
///
/// Discriminant order matches the Windows `NoSpeechDiagnosticOutcome` enum, so
/// a head that persists or logs the numeric value keeps meaning the same thing.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NoSpeechOutcome {
    /// Expected/benign — capture nothing.
    Skip,
    /// Nothing was decoded at all — a recorder failure, reported separately
    /// under its own name, message and fingerprint root so it stops fragmenting
    /// the no-speech group.
    EmptyRecording,
    /// Audio exists but produced no transcript — the original diagnostic.
    NoSpeech,
}

/// Everything [`classify`] decides on: the audio measurements plus what the
/// provider said.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NoSpeechInput {
    /// `false` when the file could not be read or decoded at all.
    pub analysis_succeeded: bool,
    /// Samples the decoder produced, or `None` when no decode loop ran
    /// (analysis failed, or a synthetic record in a test). `Some(0)` means the
    /// recorder captured nothing; `None` means unknown, and is deliberately NOT
    /// treated as empty.
    pub decoded_sample_count: Option<u64>,
    /// The provider returned an empty transcript without setting its no-speech
    /// flag — an anomaly, always reported.
    pub empty_transcript_without_flag: bool,
    /// The provider explicitly reported no speech.
    pub backend_no_speech_detected: bool,
    pub peak_dbfs: f64,
    pub rms_dbfs: f64,
    pub non_silent_ratio: f64,
}

impl Default for NoSpeechInput {
    fn default() -> Self {
        NoSpeechInput {
            analysis_succeeded: true,
            decoded_sample_count: None,
            empty_transcript_without_flag: false,
            backend_no_speech_detected: false,
            peak_dbfs: MINIMUM_DBFS,
            rms_dbfs: MINIMUM_DBFS,
            non_silent_ratio: 0.0,
        }
    }
}

/// Decide what to report. **Arm order is load-bearing** — see the inline notes.
///
/// This is the five-arm Windows shape. macOS ran four of them: it had no
/// `empty_transcript_without_flag` arm, because its error transport did not
/// carry the distinction.
pub fn classify(input: NoSpeechInput) -> NoSpeechOutcome {
    // 1. MUST stay first: with no usable analysis we cannot tell an empty
    //    recording from a quiet one, so fall back to the full no-speech report.
    if !input.analysis_succeeded {
        return NoSpeechOutcome::NoSpeech;
    }

    // 2. A header-only / zero-frame file means the recorder produced nothing,
    //    which is a different fault from "we recorded audio and got no words
    //    back". Still reported, just under its own identity.
    //
    //    The discriminator is the decoded sample count, NOT a duration or a file
    //    size, both of which lie here: duration falls back to the caller's
    //    wall-clock value when the container reports none, so a header-only file
    //    from a 5-second recording arrives as 5.0; and a zero-byte file cannot
    //    co-occur with `analysis_succeeded` at all.
    if input.decoded_sample_count == Some(0) {
        return NoSpeechOutcome::EmptyRecording;
    }

    // 3. An empty transcript with no flag is a provider anomaly, whatever the
    //    signal looks like.
    if input.empty_transcript_without_flag {
        return NoSpeechOutcome::NoSpeech;
    }

    // 4. Confirmed dead silence — the user recorded nothing. Always benign.
    if input.non_silent_ratio == 0.0 && input.peak_dbfs < CONFIRMED_SILENCE_PEAK_DBFS {
        return NoSpeechOutcome::Skip;
    }

    // 5. The provider said "no speech" and the signal agrees. Benign.
    if input.backend_no_speech_detected
        && input.non_silent_ratio <= LOW_SIGNAL_NON_SILENT_RATIO
        && input.rms_dbfs <= LOW_SIGNAL_RMS_DBFS
    {
        return NoSpeechOutcome::Skip;
    }

    NoSpeechOutcome::NoSpeech
}

// ===========================================================================
// Sentry grouping
// ===========================================================================

/// The three persisted mode fields the diagnostic groups and facets on.
///
/// Modelled as a whole so "no mode at all" stays distinguishable from "a mode
/// whose `provider_type` was never written" — see [`build_fingerprint`], where
/// the two produce different fingerprints.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct ModeIdentity {
    pub provider_type: Option<String>,
    pub cloud_provider: Option<String>,
    pub local_engine: Option<String>,
}

/// Mirrors how the app actually dispatches: `provider_type` is nullable with no
/// initializer and nothing backfills it, and every routing site treats `"cloud"`
/// as the special case and everything else — including null and empty — as
/// local. Matching only the literal `"local"` here would leave every mode with a
/// null or non-canonical provider type grouping on its stale cloud vendor.
///
/// Case-insensitive, matching the routing sites that compare that way. Values
/// are not normalized on write and this does not normalize them either.
pub fn is_local_mode(mode: Option<&ModeIdentity>) -> bool {
    !mode
        .and_then(|m| m.provider_type.as_deref())
        .is_some_and(|value| value.eq_ignore_ascii_case("cloud"))
}

/// The `local_engine` tag: the mode's engine, or `"none"` when it is absent or
/// blank. The value is returned as written, never trimmed or normalized.
pub fn resolve_local_engine(mode: Option<&ModeIdentity>) -> String {
    mode.and_then(|m| m.local_engine.as_deref())
        .filter(|value| !value.trim().is_empty())
        .unwrap_or("none")
        .to_string()
}

/// The `cloud_provider` tag with the staleness masked off, so faceting on it
/// does not attribute local-mode events to a cloud vendor the mode no longer
/// uses.
pub fn resolve_cloud_provider_tag(mode: Option<&ModeIdentity>) -> String {
    if is_local_mode(mode) {
        return "none".to_string();
    }
    mode.and_then(|m| m.cloud_provider.as_deref())
        .unwrap_or("none")
        .to_string()
}

/// Build the Sentry grouping fingerprint: five elements, in this order.
///
/// `fingerprint_root` stays the caller's: Windows reports
/// `transcription-no-speech`, macOS `macos-transcription-no-speech`. Unifying
/// the roots would merge macOS events into Windows' live issues, which is the
/// opposite of what a shared classifier is for. Only the *shape* is shared.
///
/// The last two elements are the honest provider axis. `cloud_provider` and
/// `provider_type` are independent persisted fields, so a mode switched from
/// cloud to local keeps a stale vendor value forever; grouping on it
/// unconditionally split ONE local-mode condition across four Sentry issues.
/// Local modes therefore group on their local engine and cloud modes keep
/// grouping per vendor.
///
/// The provider-type element is canonicalized through [`is_local_mode`] rather
/// than emitted raw, because a raw value would re-split the cohort the engine
/// element just merged (`"local"` vs null vs `""` = three groups for one
/// condition). A genuinely absent mode stays `"unknown"`.
pub fn build_fingerprint(
    fingerprint_root: &str,
    diagnostic_stage: &str,
    diagnostic_source: &str,
    mode: Option<&ModeIdentity>,
) -> Vec<String> {
    let local = is_local_mode(mode);
    vec![
        fingerprint_root.to_string(),
        diagnostic_stage.to_string(),
        diagnostic_source.to_string(),
        match mode {
            None => "unknown".to_string(),
            Some(_) if local => "local".to_string(),
            Some(_) => "cloud".to_string(),
        },
        if local {
            resolve_local_engine(mode)
        } else {
            mode.and_then(|m| m.cloud_provider.as_deref())
                .unwrap_or("none")
                .to_string()
        },
    ]
}

#[cfg(test)]
mod tests;
