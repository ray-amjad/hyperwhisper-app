//! UniFFI surface for the no-speech audio diagnostic (`hw_audio::no_speech`,
//! issue #291).
//!
//! Mirrors the leaf crate's records and enums so `hw-audio` stays uniffi-free,
//! the same way `ffi_input` mirrors `hw-input` and `ffi_prompt` mirrors
//! `hw-text`.
//!
//! Everything here is `audio_`- or `no_speech_`-prefixed and stateless. There is
//! no Rust-owned accumulator: a UniFFI object method takes `&self`, so a
//! `push(&mut self)` sample sink cannot cross the boundary at all, and pushing
//! per sample would be one FFI call per sample. The head keeps its own decode
//! loop — it already owns the decoder — and hands over what it counted as an
//! [`HwSignalAccumulation`]. Read [`audio_silence_threshold`] once before the
//! loop; do not call across the boundary inside it.
//!
//! The Sentry message and the fingerprint *root* stay in the head on purpose:
//! Windows reports `transcription-no-speech`, macOS `macos-transcription-no-speech`,
//! and merging them would merge macOS events into Windows' live issues.

use hw_audio::no_speech;

// ===========================================================================
// Types
// ===========================================================================

/// What the head's decode loop counted. Mirrors `no_speech::SignalAccumulation`.
///
/// `sum_squares` and `peak` are over the absolute amplitude, so both are
/// non-negative for any real input.
#[derive(uniffi::Record)]
pub struct HwSignalAccumulation {
    /// Samples the decoder actually produced.
    pub sample_count: u64,
    /// Samples whose absolute amplitude reached [`audio_silence_threshold`].
    pub non_silent_count: u64,
    /// Sum of `amplitude * amplitude` over every sample.
    pub sum_squares: f64,
    /// Largest absolute amplitude seen.
    pub peak: f64,
}

impl From<HwSignalAccumulation> for no_speech::SignalAccumulation {
    fn from(a: HwSignalAccumulation) -> Self {
        no_speech::SignalAccumulation {
            sample_count: a.sample_count,
            non_silent_count: a.non_silent_count,
            sum_squares: a.sum_squares,
            peak: a.peak,
        }
    }
}

impl From<no_speech::SignalAccumulation> for HwSignalAccumulation {
    fn from(a: no_speech::SignalAccumulation) -> Self {
        HwSignalAccumulation {
            sample_count: a.sample_count,
            non_silent_count: a.non_silent_count,
            sum_squares: a.sum_squares,
            peak: a.peak,
        }
    }
}

/// The measurements a diagnostic reports and classifies on. Mirrors
/// `no_speech::AudioSignalSummary`.
#[derive(uniffi::Record)]
pub struct HwAudioSignalSummary {
    pub peak_dbfs: f64,
    pub rms_dbfs: f64,
    /// Fraction of samples that reached the silence threshold, rounded to four
    /// decimal places.
    pub non_silent_ratio: f64,
}

impl From<no_speech::AudioSignalSummary> for HwAudioSignalSummary {
    fn from(s: no_speech::AudioSignalSummary) -> Self {
        HwAudioSignalSummary {
            peak_dbfs: s.peak_dbfs,
            rms_dbfs: s.rms_dbfs,
            non_silent_ratio: s.non_silent_ratio,
        }
    }
}

/// What a no-speech failure is reported as, if anything. Mirrors
/// `no_speech::NoSpeechOutcome`; variant order matches the Windows enum.
#[derive(uniffi::Enum)]
pub enum HwNoSpeechOutcome {
    /// Expected/benign — capture nothing.
    Skip,
    /// Nothing was decoded at all — a recorder failure, reported separately
    /// under its own name, message and fingerprint root.
    EmptyRecording,
    /// Audio exists but produced no transcript — the original diagnostic.
    NoSpeech,
}

impl From<no_speech::NoSpeechOutcome> for HwNoSpeechOutcome {
    fn from(o: no_speech::NoSpeechOutcome) -> Self {
        match o {
            no_speech::NoSpeechOutcome::Skip => HwNoSpeechOutcome::Skip,
            no_speech::NoSpeechOutcome::EmptyRecording => HwNoSpeechOutcome::EmptyRecording,
            no_speech::NoSpeechOutcome::NoSpeech => HwNoSpeechOutcome::NoSpeech,
        }
    }
}

impl From<HwNoSpeechOutcome> for no_speech::NoSpeechOutcome {
    fn from(o: HwNoSpeechOutcome) -> Self {
        match o {
            HwNoSpeechOutcome::Skip => no_speech::NoSpeechOutcome::Skip,
            HwNoSpeechOutcome::EmptyRecording => no_speech::NoSpeechOutcome::EmptyRecording,
            HwNoSpeechOutcome::NoSpeech => no_speech::NoSpeechOutcome::NoSpeech,
        }
    }
}

/// Everything [`no_speech_classify`] decides on. Mirrors
/// `no_speech::NoSpeechInput`.
#[derive(uniffi::Record)]
pub struct HwNoSpeechInput {
    /// `false` when the file could not be read or decoded at all.
    pub analysis_succeeded: bool,
    /// Samples the decoder produced, or `null` when no decode loop ran. `0`
    /// means the recorder captured nothing; `null` means unknown, and is
    /// deliberately NOT treated as empty.
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

impl From<HwNoSpeechInput> for no_speech::NoSpeechInput {
    fn from(i: HwNoSpeechInput) -> Self {
        no_speech::NoSpeechInput {
            analysis_succeeded: i.analysis_succeeded,
            decoded_sample_count: i.decoded_sample_count,
            empty_transcript_without_flag: i.empty_transcript_without_flag,
            backend_no_speech_detected: i.backend_no_speech_detected,
            peak_dbfs: i.peak_dbfs,
            rms_dbfs: i.rms_dbfs,
            non_silent_ratio: i.non_silent_ratio,
        }
    }
}

impl From<no_speech::NoSpeechInput> for HwNoSpeechInput {
    fn from(i: no_speech::NoSpeechInput) -> Self {
        HwNoSpeechInput {
            analysis_succeeded: i.analysis_succeeded,
            decoded_sample_count: i.decoded_sample_count,
            empty_transcript_without_flag: i.empty_transcript_without_flag,
            backend_no_speech_detected: i.backend_no_speech_detected,
            peak_dbfs: i.peak_dbfs,
            rms_dbfs: i.rms_dbfs,
            non_silent_ratio: i.non_silent_ratio,
        }
    }
}

/// The three persisted mode fields the diagnostic groups and facets on. Mirrors
/// `no_speech::ModeIdentity`.
///
/// Passed as a whole, and as an `Option`, so that "no mode at all" stays
/// distinguishable from "a mode whose `provider_type` was never written" — the
/// two produce different fingerprints. Do not flatten it into three loose
/// arguments.
#[derive(uniffi::Record)]
pub struct HwModeIdentity {
    pub provider_type: Option<String>,
    pub cloud_provider: Option<String>,
    pub local_engine: Option<String>,
}

impl From<HwModeIdentity> for no_speech::ModeIdentity {
    fn from(m: HwModeIdentity) -> Self {
        no_speech::ModeIdentity {
            provider_type: m.provider_type,
            cloud_provider: m.cloud_provider,
            local_engine: m.local_engine,
        }
    }
}

impl From<no_speech::ModeIdentity> for HwModeIdentity {
    fn from(m: no_speech::ModeIdentity) -> Self {
        HwModeIdentity {
            provider_type: m.provider_type,
            cloud_provider: m.cloud_provider,
            local_engine: m.local_engine,
        }
    }
}

// ===========================================================================
// Constants
// ===========================================================================

/// Absolute sample amplitude at or above which a sample counts as non-silent.
///
/// Read once before the decode loop and compare in 32-bit float, which is where
/// both heads make the comparison. Widening it to 64-bit moves the boundary,
/// because `0.01` is not exactly representable.
#[uniffi::export]
pub fn audio_silence_threshold() -> f32 {
    no_speech::SILENCE_THRESHOLD
}

/// The dBFS value reported for digital silence, and the floor of the scale.
#[uniffi::export]
pub fn audio_minimum_dbfs() -> f64 {
    no_speech::MINIMUM_DBFS
}

/// Below this peak, with a zero non-silent ratio, the clip is confirmed dead
/// silence.
#[uniffi::export]
pub fn no_speech_confirmed_silence_peak_dbfs() -> f64 {
    no_speech::CONFIRMED_SILENCE_PEAK_DBFS
}

/// Backend-confirmed low-signal skip: this and
/// [`no_speech_low_signal_non_silent_ratio`] must BOTH hold.
#[uniffi::export]
pub fn no_speech_low_signal_rms_dbfs() -> f64 {
    no_speech::LOW_SIGNAL_RMS_DBFS
}

/// See [`no_speech_low_signal_rms_dbfs`].
#[uniffi::export]
pub fn no_speech_low_signal_non_silent_ratio() -> f64 {
    no_speech::LOW_SIGNAL_NON_SILENT_RATIO
}

// ===========================================================================
// Functions
// ===========================================================================

/// Convert a linear amplitude (0..=1) to dBFS, rounded to two decimals.
///
/// Zero, negative and non-finite input return [`audio_minimum_dbfs`]. Rounding
/// is away from zero at the midpoint — the Swift behaviour, not the C#
/// `Math.Round(x, 2)` banker's behaviour.
#[uniffi::export]
pub fn audio_to_dbfs(linear: f64) -> f64 {
    no_speech::to_dbfs(linear)
}

/// Bucket a dBFS value to the 5 dB step at or below it (`-38.2` -> `"-40dbfs"`)
/// for use as a low-cardinality Sentry tag. Floors, does not truncate: negatives
/// bucket downward. At or below the floor, and for non-finite input, the bucket
/// is `"silent"`.
#[uniffi::export]
pub fn audio_bucket_dbfs(dbfs: f64) -> String {
    no_speech::bucket_dbfs(dbfs)
}

/// Turn the head's raw counts into the reported measurements. An empty
/// accumulation summarizes to the silent floor rather than dividing by zero.
#[uniffi::export]
pub fn audio_summarize_signal(accumulation: HwSignalAccumulation) -> HwAudioSignalSummary {
    no_speech::summarize(accumulation.into()).into()
}

/// Decide what to report. The five arms are evaluated in a fixed order — see
/// `hw_audio::no_speech::classify`.
#[uniffi::export]
pub fn no_speech_classify(input: HwNoSpeechInput) -> HwNoSpeechOutcome {
    no_speech::classify(input.into()).into()
}

/// Build the five-element Sentry grouping fingerprint.
///
/// `fingerprint_root` stays the caller's — it is the one part that is
/// deliberately platform-distinct.
#[uniffi::export]
pub fn no_speech_fingerprint(
    fingerprint_root: String,
    diagnostic_stage: String,
    diagnostic_source: String,
    mode: Option<HwModeIdentity>,
) -> Vec<String> {
    let mode = mode.map(no_speech::ModeIdentity::from);
    no_speech::build_fingerprint(
        &fingerprint_root,
        &diagnostic_stage,
        &diagnostic_source,
        mode.as_ref(),
    )
}

/// The `cloud_provider` tag with the staleness masked off, so faceting on it
/// does not attribute local-mode events to a cloud vendor the mode no longer
/// uses.
#[uniffi::export]
pub fn no_speech_cloud_provider_tag(mode: Option<HwModeIdentity>) -> String {
    let mode = mode.map(no_speech::ModeIdentity::from);
    no_speech::resolve_cloud_provider_tag(mode.as_ref())
}

/// The `local_engine` tag: the mode's engine, or `"none"` when it is absent or
/// blank. Values are reported as written, never normalized.
#[uniffi::export]
pub fn no_speech_local_engine_tag(mode: Option<HwModeIdentity>) -> String {
    let mode = mode.map(no_speech::ModeIdentity::from);
    no_speech::resolve_local_engine(mode.as_ref())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The mirror must not reorder or drop a field on the way across: measure,
    /// summarize and classify one accumulation end to end.
    #[test]
    fn mirrors_a_full_measurement_and_classification() {
        let summary = audio_summarize_signal(HwSignalAccumulation {
            sample_count: 4,
            non_silent_count: 4,
            sum_squares: 4.0,
            peak: 1.0,
        });
        assert_eq!(summary.peak_dbfs, 0.0);
        assert_eq!(summary.rms_dbfs, 0.0);
        assert_eq!(summary.non_silent_ratio, 1.0);

        // Loud audio the provider called empty is the cohort this exists to
        // catch, so it must survive every skip arm.
        let outcome = no_speech_classify(HwNoSpeechInput {
            analysis_succeeded: true,
            decoded_sample_count: Some(4),
            empty_transcript_without_flag: false,
            backend_no_speech_detected: true,
            peak_dbfs: summary.peak_dbfs,
            rms_dbfs: summary.rms_dbfs,
            non_silent_ratio: summary.non_silent_ratio,
        });
        assert!(matches!(outcome, HwNoSpeechOutcome::NoSpeech));
    }

    #[test]
    fn constants_cross_unchanged() {
        assert_eq!(audio_silence_threshold(), 0.01_f32);
        assert_eq!(audio_minimum_dbfs(), -120.0);
        assert_eq!(no_speech_confirmed_silence_peak_dbfs(), -50.0);
        assert_eq!(no_speech_low_signal_rms_dbfs(), -38.0);
        assert_eq!(no_speech_low_signal_non_silent_ratio(), 0.06);
        assert_eq!(audio_to_dbfs(0.0), audio_minimum_dbfs());
        assert_eq!(audio_bucket_dbfs(-38.2), "-40dbfs");
    }

    /// The `Option<Record>` argument is what keeps "no mode" distinguishable
    /// from "a mode with nothing written on it".
    #[test]
    fn an_absent_mode_crosses_as_none_not_as_a_blank_record() {
        assert_eq!(
            no_speech_fingerprint("root".into(), "stop".into(), "flow".into(), None),
            vec!["root", "stop", "flow", "unknown", "none"]
        );

        let blank = HwModeIdentity {
            provider_type: None,
            cloud_provider: None,
            local_engine: None,
        };
        assert_eq!(
            no_speech_fingerprint("root".into(), "stop".into(), "flow".into(), Some(blank)),
            vec!["root", "stop", "flow", "local", "none"]
        );

        let cloud = HwModeIdentity {
            provider_type: Some("cloud".into()),
            cloud_provider: Some("deepgram".into()),
            local_engine: Some("parakeet".into()),
        };
        assert_eq!(
            no_speech_cloud_provider_tag(Some(cloud)),
            "deepgram".to_string()
        );

        let local = HwModeIdentity {
            provider_type: Some("local".into()),
            cloud_provider: Some("groq".into()),
            local_engine: Some("parakeet".into()),
        };
        assert_eq!(
            no_speech_local_engine_tag(Some(local)),
            "parakeet".to_string()
        );
    }
}
