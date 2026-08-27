using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// What a no-speech failure is reported as, if anything. Mirrors the core's
/// <c>HwNoSpeechOutcome</c> (issue #291).
/// </summary>
public enum PortableNoSpeechOutcome
{
    /// <summary>Expected/benign — capture nothing.</summary>
    Skip,

    /// <summary>
    /// Nothing was decoded at all — a recorder failure, reported separately
    /// under its own name, message and fingerprint root.
    /// </summary>
    EmptyRecording,

    /// <summary>Audio exists but produced no transcript — the original diagnostic.</summary>
    NoSpeech
}

/// <summary>
/// What the head's decode loop counted, in the head's own decode format.
/// <paramref name="SumSquares"/> and <paramref name="Peak"/> are over the
/// <i>absolute</i> amplitude, so both are non-negative for any real input.
/// </summary>
public readonly record struct PortableSignalAccumulation(
    ulong SampleCount,
    ulong NonSilentCount,
    double SumSquares,
    double Peak);

/// <summary>The measurements a diagnostic reports and classifies on.</summary>
public readonly record struct PortableAudioSignalSummary(
    double PeakDbfs,
    double RmsDbfs,
    double NonSilentRatio);

/// <summary>
/// Everything <see cref="PortableNoSpeechDiagnostics.Classify"/> decides on: the
/// audio measurements plus what the provider said.
/// </summary>
/// <param name="DecodedSampleCount">
/// Samples the decoder produced, or <c>null</c> when no decode loop ran
/// (analysis failed, or a synthetic record in a test). <c>0</c> means the
/// recorder captured nothing; <c>null</c> means unknown and is deliberately NOT
/// treated as empty. A negative value cannot come from a decode loop and is
/// carried across as <c>null</c> rather than wrapping into a huge count.
/// </param>
public readonly record struct PortableNoSpeechInput(
    bool AnalysisSucceeded,
    long? DecodedSampleCount,
    bool EmptyTranscriptWithoutFlag,
    bool BackendNoSpeechDetected,
    double PeakDbfs,
    double RmsDbfs,
    double NonSilentRatio);

/// <summary>
/// The three persisted mode fields the diagnostic groups and facets on. Passed
/// as a whole, and as a nullable, so that "no mode at all" stays distinguishable
/// from "a mode whose provider type was never written" — the two produce
/// different fingerprints.
/// </summary>
public sealed record PortableModeIdentity(
    string? ProviderType,
    string? CloudProvider,
    string? LocalEngine);

/// <summary>
/// The shared no-speech diagnostic (issue #291): measurement, classification and
/// Sentry grouping, held in <c>hw-audio</c> and reached through the UniFFI core.
///
/// <para>
/// The head keeps its own decode loop — it already owns the decoder — and hands
/// over what it counted as a <see cref="PortableSignalAccumulation"/>. Read
/// <see cref="SilenceThreshold"/> into a local ONCE before that loop; every
/// member here crosses the FFI boundary, so calling one per sample would be a
/// P/Invoke per sample.
/// </para>
///
/// <para>
/// The Sentry message and the fingerprint <i>root</i> stay in the head on
/// purpose: Windows reports <c>transcription-no-speech</c>, macOS
/// <c>macos-transcription-no-speech</c>, and merging them would merge macOS
/// events into Windows' live issues. Only the <i>shape</i> is shared.
/// </para>
/// </summary>
public static class PortableNoSpeechDiagnostics
{
    /// <summary>
    /// Absolute sample amplitude at or above which a sample counts as
    /// non-silent. Compare in <see cref="float"/>, which is where the heads make
    /// the comparison — widening it to <see cref="double"/> moves the boundary,
    /// because <c>0.01</c> is not exactly representable.
    /// </summary>
    public static float SilenceThreshold => HyperwhisperCoreMethods.AudioSilenceThreshold();

    /// <summary>The dBFS value reported for digital silence, and the floor of the scale.</summary>
    public static double MinimumDbfs => HyperwhisperCoreMethods.AudioMinimumDbfs();

    /// <summary>
    /// Below this peak, with a zero non-silent ratio, the clip is confirmed dead
    /// silence and nothing is reported.
    /// </summary>
    public static double ConfirmedSilencePeakDbfs => HyperwhisperCoreMethods.NoSpeechConfirmedSilencePeakDbfs();

    /// <summary>
    /// Backend-confirmed low-signal skip: this and
    /// <see cref="LowSignalNonSilentRatio"/> must BOTH hold.
    /// </summary>
    public static double LowSignalRmsDbfs => HyperwhisperCoreMethods.NoSpeechLowSignalRmsDbfs();

    /// <summary>See <see cref="LowSignalRmsDbfs"/>.</summary>
    public static double LowSignalNonSilentRatio => HyperwhisperCoreMethods.NoSpeechLowSignalNonSilentRatio();

    /// <summary>
    /// Convert a linear amplitude (0..=1) to dBFS, rounded to two decimals. Zero,
    /// negative and non-finite input return <see cref="MinimumDbfs"/>.
    /// <para>
    /// Rounding is away from zero at the midpoint — the Swift/Rust behaviour, not
    /// <c>Math.Round(x, 2)</c>'s banker's rounding. A value landing exactly on a
    /// midpoint therefore moves by one unit in the last place relative to the
    /// pre-#291 Windows implementation.
    /// </para>
    /// </summary>
    public static double ToDbfs(double linear) => HyperwhisperCoreMethods.AudioToDbfs(linear);

    /// <summary>
    /// Bucket a dBFS value to the 5 dB step at or below it (<c>-38.2</c> →
    /// <c>"-40dbfs"</c>) for use as a low-cardinality Sentry tag. This floors, it
    /// does not truncate: negatives bucket <i>downward</i>. At or below the floor,
    /// and for non-finite input, the bucket is <c>"silent"</c>.
    /// </summary>
    public static string BucketDbfs(double dbfs) => HyperwhisperCoreMethods.AudioBucketDbfs(dbfs);

    /// <summary>
    /// Turn the head's raw counts into the reported measurements. An empty
    /// accumulation summarizes to the silent floor rather than dividing by zero.
    /// </summary>
    public static PortableAudioSignalSummary Summarize(PortableSignalAccumulation accumulation)
    {
        var summary = HyperwhisperCoreMethods.AudioSummarizeSignal(new HwSignalAccumulation(
            accumulation.SampleCount,
            accumulation.NonSilentCount,
            accumulation.SumSquares,
            accumulation.Peak));
        return new PortableAudioSignalSummary(
            summary.peakDbfs,
            summary.rmsDbfs,
            summary.nonSilentRatio);
    }

    /// <summary>
    /// Decide what to report. The five arms are evaluated in a fixed order — see
    /// <c>hw_audio::no_speech::classify</c>.
    /// </summary>
    public static PortableNoSpeechOutcome Classify(PortableNoSpeechInput input)
    {
        var outcome = HyperwhisperCoreMethods.NoSpeechClassify(new HwNoSpeechInput(
            input.AnalysisSucceeded,
            // A negative count is not something a decode loop can produce; an
            // unchecked cast would turn it into an enormous positive one, so it
            // takes the "unknown" answer instead.
            input.DecodedSampleCount is { } count && count >= 0 ? (ulong)count : null,
            input.EmptyTranscriptWithoutFlag,
            input.BackendNoSpeechDetected,
            input.PeakDbfs,
            input.RmsDbfs,
            input.NonSilentRatio));

        return outcome switch
        {
            HwNoSpeechOutcome.Skip => PortableNoSpeechOutcome.Skip,
            HwNoSpeechOutcome.EmptyRecording => PortableNoSpeechOutcome.EmptyRecording,
            HwNoSpeechOutcome.NoSpeech => PortableNoSpeechOutcome.NoSpeech,
            _ => throw new ArgumentOutOfRangeException(nameof(input), outcome, null),
        };
    }

    /// <summary>
    /// Build the five-element Sentry grouping fingerprint.
    /// <paramref name="fingerprintRoot"/> stays the caller's — it is the one part
    /// that is deliberately platform-distinct.
    /// </summary>
    public static string[] BuildFingerprint(
        string fingerprintRoot,
        string diagnosticStage,
        string diagnosticSource,
        PortableModeIdentity? mode)
    {
        ArgumentNullException.ThrowIfNull(fingerprintRoot);
        ArgumentNullException.ThrowIfNull(diagnosticStage);
        ArgumentNullException.ThrowIfNull(diagnosticSource);
        return [.. HyperwhisperCoreMethods.NoSpeechFingerprint(
            fingerprintRoot,
            diagnosticStage,
            diagnosticSource,
            ToCore(mode))];
    }

    /// <summary>
    /// The <c>cloud_provider</c> tag with the staleness masked off, so faceting on
    /// it does not attribute local-mode events to a cloud vendor the mode no
    /// longer uses.
    /// </summary>
    public static string CloudProviderTag(PortableModeIdentity? mode) =>
        HyperwhisperCoreMethods.NoSpeechCloudProviderTag(ToCore(mode));

    /// <summary>
    /// The <c>local_engine</c> tag: the mode's engine, or <c>"none"</c> when it is
    /// absent or blank. Values are reported as written, never normalized.
    /// </summary>
    public static string LocalEngineTag(PortableModeIdentity? mode) =>
        HyperwhisperCoreMethods.NoSpeechLocalEngineTag(ToCore(mode));

    private static HwModeIdentity? ToCore(PortableModeIdentity? mode) =>
        mode is null
            ? null
            : new HwModeIdentity(mode.ProviderType, mode.CloudProvider, mode.LocalEngine);
}
