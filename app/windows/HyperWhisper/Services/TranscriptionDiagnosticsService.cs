using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Services;

/// <summary>
/// Captures privacy-safe diagnostics for transcription failures.
/// Focuses on audio signal quality and provider metadata rather than transcript content.
///
/// <para>
/// The thresholds, the dBFS maths, the five classification arms and the
/// fingerprint shape all live in the shared core (<c>hw-audio</c>, issue #291)
/// and are reached through <see cref="PortableNoSpeechDiagnostics"/>. Only the
/// NAudio decode loop (<see cref="TranscriptionAudioAnalyzer"/>), the Sentry
/// payload and the platform-distinct name/message/fingerprint-root are Windows'.
/// </para>
/// </summary>
public static class TranscriptionDiagnosticsService
{
    /// <summary>
    /// Duplicated from <see cref="PortableNoSpeechDiagnostics.MinimumDbfs"/>
    /// ONLY because <see cref="AudioAnalysisDiagnostics"/>'s optional parameters
    /// need a compile-time constant. Never compare against this — use the
    /// portable property. A smoke test asserts the two agree.
    /// </summary>
    private const double MinimumDbfs = -120.0;

    /// <summary>
    /// <c>diagnostic_source</c> for an empty recording caught on the device before
    /// any upload (#750). Distinct from <c>provider_no_speech</c> so the two causes
    /// stay separable in Sentry.
    /// </summary>
    public const string LocalEmptyCaptureSource = "local_empty_capture";

    public static void CaptureNoSpeechDiagnostic(
        Guid transcriptId,
        string audioPath,
        double? fallbackDurationSeconds,
        Mode? mode,
        string diagnosticStage,
        string diagnosticSource,
        string? inputDeviceName = null,
        string? transcriptionProviderDisplayName = null,
        TranscriptionProviderDiagnostics? providerDiagnostics = null,
        TranscriptionException? exception = null,
        int? captureDeviceCount = null,
        AudioDeviceSelectionDiagnostics? deviceSelection = null)
    {
        var audioDiagnostics = AnalyzeAudioFile(audioPath, fallbackDurationSeconds);
        var outcome = ClassifyNoSpeechDiagnostic(audioDiagnostics, providerDiagnostics);

        if (outcome == PortableNoSpeechOutcome.Skip)
        {
            LoggingService.Debug(
                "TranscriptionDiagnosticsService: Skipping expected no-speech diagnostic " +
                $"(stage={diagnosticStage}, source={diagnosticSource}, " +
                $"backend_no_speech={providerDiagnostics?.BackendNoSpeechDetected}, " +
                $"empty_without_flag={providerDiagnostics?.EmptyTranscriptWithoutFlag}, " +
                $"audio_analysis_succeeded={audioDiagnostics.AnalysisSucceeded}, " +
                $"audio_rms_dbfs={audioDiagnostics.RmsDbfs}, " +
                $"audio_non_silent_ratio={audioDiagnostics.NonSilentRatio})");
            return;
        }

        // An empty recording (nothing captured at all) is a recorder failure, not a
        // no-speech transcription result - it gets its own name, message and
        // fingerprint root so it stops being reported into, and fragmenting, the
        // no-speech group (HYPERWHISPER-PA/-QB/-RM/-XB/-XR). One lookup resolves all
        // three together, so an outcome can never be reported under another outcome's
        // identity.
        var presentation = ResolveDiagnosticPresentation(outcome);

        var (tags, extras) = BuildDiagnosticPayload(
            transcriptId,
            audioPath,
            audioDiagnostics,
            presentation,
            mode,
            diagnosticStage,
            diagnosticSource,
            inputDeviceName,
            transcriptionProviderDisplayName,
            providerDiagnostics,
            exception,
            captureDeviceCount,
            deviceSelection);

        var fingerprint = BuildDiagnosticFingerprint(presentation.FingerprintRoot, diagnosticStage, diagnosticSource, mode);

        var dedupeKey = $"{transcriptId}:{diagnosticStage}:{diagnosticSource}:{presentation.Name}";

        SentryService.CaptureDiagnosticTransaction(
            message: presentation.Message,
            extras: extras,
            tags: tags,
            fingerprint: fingerprint,
            dedupeKey: dedupeKey);
    }

    /// <summary>
    /// The tags and extras of one no-speech event.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="CaptureNoSpeechDiagnostic"/> so the smoke tests can
    /// read the payload this diagnostic actually sends. The specific thing they read
    /// is the extras KEYS: <see cref="SentryService.IsRedactedExtraKey"/> replaces the
    /// String value of any key that is not on
    /// <see cref="SentryService.AllowedExtraKeys"/> with <c>"[redacted]"</c>, so a new
    /// field that is not added to that list arrives empty with nothing at the call
    /// site to say so.
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static (Dictionary<string, string> Tags, Dictionary<string, object> Extras) BuildDiagnosticPayload(
        Guid transcriptId,
        string audioPath,
        AudioAnalysisDiagnostics audioDiagnostics,
        DiagnosticPresentation presentation,
        Mode? mode,
        string diagnosticStage,
        string diagnosticSource,
        string? inputDeviceName,
        string? transcriptionProviderDisplayName,
        TranscriptionProviderDiagnostics? providerDiagnostics,
        TranscriptionException? exception,
        int? captureDeviceCount,
        AudioDeviceSelectionDiagnostics? deviceSelection = null)
    {
        var tags = BuildDiagnosticTags(
            audioDiagnostics,
            presentation,
            mode,
            diagnosticStage,
            diagnosticSource,
            inputDeviceName,
            providerDiagnostics,
            captureDeviceCount,
            deviceSelection);

        var extras = BuildDiagnosticExtras(
            transcriptId,
            audioPath,
            audioDiagnostics,
            mode,
            inputDeviceName,
            transcriptionProviderDisplayName,
            providerDiagnostics,
            exception,
            deviceSelection);

        return (tags, extras);
    }

    /// <summary>
    /// The tags half of <see cref="BuildDiagnosticPayload"/>: the fields Sentry can
    /// facet and segment on.
    /// </summary>
    private static Dictionary<string, string> BuildDiagnosticTags(
        AudioAnalysisDiagnostics audioDiagnostics,
        DiagnosticPresentation presentation,
        Mode? mode,
        string diagnosticStage,
        string diagnosticSource,
        string? inputDeviceName,
        TranscriptionProviderDiagnostics? providerDiagnostics,
        int? captureDeviceCount,
        AudioDeviceSelectionDiagnostics? deviceSelection)
    {
        var modeLanguage = mode?.Language;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["component"] = "transcription",
            ["diagnostic_name"] = presentation.Name,
            ["diagnostic_stage"] = diagnosticStage,
            ["diagnostic_source"] = diagnosticSource,
            ["provider_type"] = mode?.ProviderType ?? "unknown",
            ["cloud_provider"] = ResolveCloudProviderTag(mode),
            ["cloud_accuracy_tier"] = mode?.CloudAccuracyTier ?? "none",
            ["local_engine"] = ResolveLocalEngine(mode),
            ["backend_no_speech_detected"] = (providerDiagnostics?.BackendNoSpeechDetected ?? false).ToString().ToLowerInvariant(),
            ["audio_analysis_succeeded"] = audioDiagnostics.AnalysisSucceeded.ToString().ToLowerInvariant(),
            // Promoted from extras so Sentry can facet/segment on them (extras
            // aren't aggregable - see HYPERWHISPER-PA). RMS is bucketed to 5dBFS
            // steps rather than passed raw: a continuous float as a tag has
            // near-100% cardinality (every event gets its own "bucket" of one),
            // which defeats faceting entirely - the whole point of promoting it.
            ["audio_rms_dbfs_bucket"] = PortableNoSpeechDiagnostics.BucketDbfs(audioDiagnostics.RmsDbfs),
            ["selected_input_device_name"] = inputDeviceName ?? "n/a",
            ["capture_device_count"] = captureDeviceCount?.ToString() ?? "unknown",
            // Fixed slugs only. This answers whether startup, hot-plug handling,
            // onboarding, or an explicit selection chose the input.
            ["capture_device_selection_reason"] = AudioDeviceSelectionReason.Normalize(deviceSelection?.Reason),
            // A tag, not an extra, because the question it answers is a segmentation
            // one: of the events in this group, how many came from a local engine,
            // and how many from a cloud vendor that reports nothing about itself?
            // Extras cannot be faceted on. The slugs are fixed strings, so the
            // cardinality stays at four.
            ["provider_attempt_source"] = providerDiagnostics?.AttemptSource ?? TranscriptionAttemptSource.Unknown,
            // A wrong or unexpected language is a standing cause of an empty
            // transcript. This is the mode's configured code ("en", "ja", "auto"),
            // never anything spoken or detected.
            ["mode_language"] = string.IsNullOrWhiteSpace(modeLanguage) ? "unset" : modeLanguage
        };

        return tags;
    }

    /// <summary>
    /// The extras half of <see cref="BuildDiagnosticPayload"/>. Every key except
    /// <c>selected_input_device_name</c> must be on
    /// <see cref="SentryService.AllowedExtraKeys"/> - see the comment at the top of
    /// the dictionary.
    /// </summary>
    private static Dictionary<string, object> BuildDiagnosticExtras(
        Guid transcriptId,
        string audioPath,
        AudioAnalysisDiagnostics audioDiagnostics,
        Mode? mode,
        string? inputDeviceName,
        string? transcriptionProviderDisplayName,
        TranscriptionProviderDiagnostics? providerDiagnostics,
        TranscriptionException? exception,
        AudioDeviceSelectionDiagnostics? deviceSelection)
    {
        var extras = new Dictionary<string, object>
        {
            // EVERY KEY HERE MUST BE ON SentryService.AllowedExtraKeys. Sentry's
            // filter is an exact key allow-list (#1890): a String value under any
            // other key arrives as "[redacted]" - silently, with no warning at the
            // call site. Only selected_input_device_name is deliberately off it (a
            // person can rename the microphone). A smoke test asserts this for the
            // whole dictionary so a new key that was not added fails in CI instead.
            //
            // History: the filter used to be a substring deny-list on "transcript",
            // "text", "prompt" and "path", and this dictionary shipped three names
            // it caught ("transcript_id", "transcription_provider_display_name",
            // "backend_empty_transcript_without_flag"), so every event of
            // HYPERWHISPER-PA/-RM/-XR carried "[redacted]" for all three. They were
            // renamed to the names below, which stay as they are so Sentry queries
            // keep working.
            //
            // This is the local record id, so a support thread can be joined to the
            // event. It is a GUID the app minted - it is not the transcript.
            ["diagnostic_record_id"] = transcriptId.ToString(),
            // audio_path is NOT reported. The recordings directory sits under the
            // user's profile, so the full path carries their Windows account name
            // (and, on the file-transcription path, the document name too). Every
            // question the path was there to answer is answered by the extension,
            // the existence flag and the size below.
            ["audio_file_exists"] = File.Exists(audioPath),
            ["audio_file_extension"] = Path.GetExtension(audioPath),
            ["audio_file_size_bytes"] = audioDiagnostics.FileSizeBytes,
            ["audio_duration_seconds"] = audioDiagnostics.DurationSeconds,
            // The SOURCE container's format, not the measurement basis. The
            // signal figures below are measured on 16 kHz mono (see
            // TranscriptionAudioAnalyzer.AnalysisSampleRate) but these two must
            // keep reporting what the recorder actually wrote, because that is
            // the fact a device or format regression shows up in.
            ["audio_sample_rate_hz"] = audioDiagnostics.SampleRate,
            ["audio_channels"] = audioDiagnostics.Channels,
            ["audio_peak_dbfs"] = audioDiagnostics.PeakDbfs,
            ["audio_rms_dbfs"] = audioDiagnostics.RmsDbfs,
            ["audio_non_silent_ratio"] = audioDiagnostics.NonSilentRatio,
            // The honest "was anything captured" signal - audio_duration_seconds above
            // falls back to the caller's wall-clock value when the container has none.
            //
            // The two counts mean different things and are both reported (#291):
            //   audio_decoded_sample_count  - mono frames the DECODER produced, counted
            //     before the 16 kHz resampler. Zero means exactly "the recorder produced
            //     nothing", which is all the empty-recording arm reads it for. Since the
            //     fold to mono it is a frame count, not an interleaved-sample count, so it
            //     no longer scales with the source channel count.
            //   audio_measured_sample_count - 16 kHz mono samples the dBFS figures below
            //     were actually measured over. This one CAN be zero for a decodable but
            //     very short file, because the resampler emits nothing until it can fill a
            //     whole output frame - which is why it is not the count the arm reads.
            // Neither is comparable with values emitted before #291.
            ["audio_decoded_sample_count"] = (object?)audioDiagnostics.DecodedSampleCount ?? "unknown",
            ["audio_measured_sample_count"] = (object?)audioDiagnostics.MeasuredSampleCount ?? "unknown",
            // mode_name is NOT reported. A mode's Name is free text the user typed
            // when they made a custom mode, so it is user content, not metadata -
            // preset modes only looked safe because nobody had renamed one yet.
            // mode_preset below is the enum, and it answers the same question
            // ("which mode was this") without carrying anything they wrote.
            ["mode_preset"] = mode?.Preset ?? "unknown",
            ["provider_display_name"] = transcriptionProviderDisplayName ?? providerDiagnostics?.ProviderDisplayName ?? exception?.ProviderName ?? "unknown",
            // Deliberately NOT on SentryService.AllowedExtraKeys, so this arrives as
            // "[redacted]" in the transaction data. It is still sent as a TAG above,
            // where only the account-identifier rewrite applies.
            ["selected_input_device_name"] = inputDeviceName ?? "n/a",
            // This value describes the device-list shape. It does not include
            // another device name or any other user content.
            ["capture_device_list_position"] = (object?)deviceSelection?.SelectedListPosition ?? "unknown",
            // Which arm produced the record, and how long that arm took. Filled for
            // every provider - local engines and BYOK cloud vendors included -
            // whereas the backend_* fields below can only ever be filled by a
            // provider that instruments itself.
            ["provider_attempt_ms"] = (object?)providerDiagnostics?.AttemptElapsedMs ?? "unknown",
            // 0 means the provider returned an empty string; a small non-zero count
            // means it returned whitespace. Those are different faults and they were
            // indistinguishable. It is a COUNT of the raw result, never the result.
            ["raw_result_length"] = (object?)providerDiagnostics?.RawResultLength ?? "unknown",
            ["backend_request_id"] = providerDiagnostics?.BackendRequestId ?? "n/a",
            ["backend_stt_provider"] = providerDiagnostics?.BackendSttProvider ?? "n/a",
            // "unknown" rather than 0: a local engine makes no HTTP request at all,
            // and reporting that as status 0 / 0 ms reads like a failed request.
            ["backend_http_status"] = (object?)providerDiagnostics?.HttpStatusCode ?? "unknown",
            ["backend_response_latency_ms"] = (object?)providerDiagnostics?.ResponseLatencyMs ?? "unknown",
            // Renamed from backend_empty_transcript_without_flag, which never
            // arrived. This is the discriminator the whole diagnostic turns on: true
            // means the provider returned nothing while claiming it heard speech.
            ["backend_empty_without_flag"] = (object?)providerDiagnostics?.EmptyTranscriptWithoutFlag ?? "unknown"
        };

        if (!string.IsNullOrWhiteSpace(audioDiagnostics.AnalysisError))
        {
            extras["audio_analysis_error"] = audioDiagnostics.AnalysisError!;
        }

        if (exception != null)
        {
            extras["exception_type"] = exception.GetType().Name;
            extras["exception_code"] = exception.Code.ToString();
            extras["exception_provider"] = exception.ProviderName ?? "unknown";
            extras["exception_http_status"] = exception.HttpStatusCode ?? 0;
        }

        return extras;
    }

    /// <summary>
    /// Describes which app path selected the current input without putting
    /// another device name in Sentry.
    /// </summary>
    internal static AudioDeviceSelectionDiagnostics DescribeAudioDeviceSelection(
        AudioDeviceService.AudioDevice? selectedDevice,
        IReadOnlyList<AudioDeviceService.AudioDevice> availableDevices,
        string reason)
    {
        var selectedPosition = selectedDevice == null
            ? -1
            : availableDevices.ToList().FindIndex(device =>
                device.DeviceNumber == selectedDevice.DeviceNumber);
        return new AudioDeviceSelectionDiagnostics(
            Reason: AudioDeviceSelectionReason.Normalize(reason),
            SelectedListPosition: selectedPosition >= 0 ? selectedPosition : null);
    }

    public sealed record AudioDeviceSelectionDiagnostics(
        string Reason,
        int? SelectedListPosition);

    /// <summary>
    /// Measure the recording's signal. The decode loop lives in
    /// <see cref="TranscriptionAudioAnalyzer.Analyze"/>; this forwarder keeps the
    /// existing surface.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static AudioAnalysisDiagnostics AnalyzeAudioFile(string audioPath, double? fallbackDurationSeconds)
        => TranscriptionAudioAnalyzer.Analyze(audioPath, fallbackDurationSeconds);

    /// <summary>
    /// Builds the Sentry grouping fingerprint. The last two elements are the honest
    /// provider axis: <c>Mode.CloudProvider</c> and <c>Mode.ProviderType</c> are
    /// independent persisted fields, so a mode switched from cloud to local keeps a
    /// stale vendor value forever. Grouping on it unconditionally split ONE local-mode
    /// condition across four Sentry issues (HYPERWHISPER-QB local+hyperwhisper,
    /// -RM local+none, -XB local+gemini, -XR local+groq). Local modes therefore group
    /// on their local engine; cloud modes keep grouping per vendor.
    /// <para>
    /// The provider-type element is canonicalized through the core's local-mode
    /// predicate rather than emitted raw, because <c>ProviderType</c> is nullable
    /// and non-canonical values route local all the same: a raw value would
    /// re-split the cohort the engine element just merged (<c>"local"</c> vs
    /// <c>null</c> vs <c>""</c> = three groups for one condition). Values are not
    /// normalized, only bucketed into local/cloud. A genuinely absent mode stays
    /// <c>"unknown"</c> - "no mode at all" is a different fact from "a mode whose
    /// ProviderType was never written".
    /// </para>
    /// <para>
    /// The element ORDER and the five-element shape are the core's since #291 and
    /// are byte-identical to what this method emitted before, so the existing
    /// Windows Sentry groups survive. Only <paramref name="fingerprintRoot"/> is
    /// per-platform.
    /// </para>
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string[] BuildDiagnosticFingerprint(
        string fingerprintRoot,
        string diagnosticStage,
        string diagnosticSource,
        Mode? mode)
        => PortableNoSpeechDiagnostics.BuildFingerprint(
            fingerprintRoot,
            diagnosticStage,
            diagnosticSource,
            ToModeIdentity(mode));

    /// <summary>
    /// The <c>cloud_provider</c> tag with the same staleness masked off, so faceting
    /// on it doesn't attribute local-mode events to a cloud vendor the mode no longer
    /// uses.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string ResolveCloudProviderTag(Mode? mode)
        => PortableNoSpeechDiagnostics.CloudProviderTag(ToModeIdentity(mode));

    private static string ResolveLocalEngine(Mode? mode)
        => PortableNoSpeechDiagnostics.LocalEngineTag(ToModeIdentity(mode));

    /// <summary>
    /// The three persisted mode fields the core groups and facets on. Passed as a
    /// whole, and as a nullable, because "no mode at all" is a different fact from
    /// "a mode whose ProviderType was never written" - the two produce different
    /// fingerprints. The values are handed over raw; the core owns the
    /// local-vs-cloud predicate and the <c>"none"</c> fallbacks, so Windows,
    /// macOS and Linux cannot drift on them.
    /// </summary>
    private static PortableModeIdentity? ToModeIdentity(Mode? mode)
        => mode is null
            ? null
            : new PortableModeIdentity(mode.ProviderType, mode.CloudProvider, mode.LocalEngine);

    /// <summary>
    /// Everything a reportable outcome is published as: the <c>diagnostic_name</c> tag
    /// (which also keys the dedupe), the Sentry message and the fingerprint root. They
    /// are one value because they must stay in step - the mislabelling this diagnostic
    /// exists to fix was three of them being derived separately.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal readonly record struct DiagnosticPresentation(
        string Name,
        string Message,
        string FingerprintRoot);

    /// <summary>
    /// The single outcome -> presentation mapping. Adding an outcome without adding an
    /// arm here throws rather than silently reporting under another outcome's name,
    /// message and fingerprint root; the smoke tests walk every enum value so that
    /// fails in CI, not in production.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static DiagnosticPresentation ResolveDiagnosticPresentation(PortableNoSpeechOutcome outcome)
        => outcome switch
        {
            // The message is the Sentry group identity for eight live issues
            // (HYPERWHISPER-PA/-QB/-RM/-T6/-VY/-XB/-XR/-W7). It must stay
            // character-identical - editing it starts a new group and orphans them.
            //
            // The fingerprint ROOT is deliberately NOT shared with macOS either
            // (macOS uses "macos-transcription-*"): only the shape comes from the
            // core, because unifying the roots would merge macOS events into these
            // same live Windows issues.
            PortableNoSpeechOutcome.NoSpeech => new DiagnosticPresentation(
                Name: "no_speech",
                Message: "Windows transcription no-speech diagnostic",
                FingerprintRoot: "transcription-no-speech"),

            PortableNoSpeechOutcome.EmptyRecording => new DiagnosticPresentation(
                Name: "empty_recording",
                Message: "Windows transcription empty recording diagnostic",
                FingerprintRoot: "transcription-empty-recording"),

            // Skip is filtered out before this point, and an unmapped outcome is a
            // programmer error: there is no honest identity to report it under.
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "No diagnostic presentation is defined for this outcome.")
        };

    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static PortableNoSpeechOutcome ClassifyNoSpeechDiagnostic(
        AudioAnalysisDiagnostics audioDiagnostics,
        TranscriptionProviderDiagnostics? providerDiagnostics)
        // The five arms, their order and their thresholds are the core's
        // (hw_audio::no_speech::classify) - this shape was Windows' and is now
        // shared, so macOS cannot drift off it. The reasoning for each arm lives
        // with the code, in the Rust.
        => PortableNoSpeechDiagnostics.Classify(new PortableNoSpeechInput(
            AnalysisSucceeded: audioDiagnostics.AnalysisSucceeded,
            DecodedSampleCount: audioDiagnostics.DecodedSampleCount,
            EmptyTranscriptWithoutFlag: providerDiagnostics?.EmptyTranscriptWithoutFlag ?? false,
            BackendNoSpeechDetected: providerDiagnostics?.BackendNoSpeechDetected ?? false,
            PeakDbfs: audioDiagnostics.PeakDbfs,
            RmsDbfs: audioDiagnostics.RmsDbfs,
            NonSilentRatio: audioDiagnostics.NonSilentRatio));

    /// <summary>
    /// True when the input is reported <i>as a no-speech diagnostic</i>. This is NOT
    /// "is anything captured": an <see cref="PortableNoSpeechOutcome.EmptyRecording"/>
    /// is also captured, under its own name and fingerprint, and returns false here.
    /// Only <see cref="PortableNoSpeechOutcome.Skip"/> means nothing is reported.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static bool ShouldCaptureAsNoSpeech(
        AudioAnalysisDiagnostics audioDiagnostics,
        TranscriptionProviderDiagnostics? providerDiagnostics)
        => ClassifyNoSpeechDiagnostic(audioDiagnostics, providerDiagnostics) == PortableNoSpeechOutcome.NoSpeech;

    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    /// <param name="DurationSeconds">
    /// The container's duration when it reports one, otherwise the caller's fallback
    /// (wall-clock recording length on the live path, an already-probed duration on
    /// the file path). Because of that substitution it does NOT tell you whether any
    /// audio was actually decoded - use <paramref name="DecodedSampleCount"/> for that.
    /// </param>
    /// <param name="DecodedSampleCount">
    /// Mono sample frames the decoder actually produced while reading the file, counted
    /// BEFORE the 16 kHz resampler, or <c>null</c> when no read loop ran (analysis failed,
    /// or a synthetic record in a test). Zero is the only honest "the recorder captured
    /// nothing" signal - see <see cref="ClassifyNoSpeechDiagnostic"/>. Null means unknown,
    /// never empty.
    /// <para>
    /// It is deliberately not the post-resample count.
    /// <c>WdlResamplingSampleProvider.Read</c> returns 0 until it can fill a whole output
    /// frame, so a decodable file with fewer frames than the sinc window needs measures
    /// zero 16 kHz samples while the decoder produced plenty - and the empty-recording arm
    /// would report "the recorder produced nothing", which is false.
    /// </para>
    /// </param>
    /// <param name="MeasuredSampleCount">
    /// 16 kHz mono samples the dBFS figures were measured over, counted AFTER the
    /// resampler. Reported for context only; nothing classifies on it.
    /// </param>
    internal sealed record AudioAnalysisDiagnostics(
        bool AnalysisSucceeded,
        double DurationSeconds,
        long FileSizeBytes,
        int SampleRate = 0,
        int Channels = 0,
        double PeakDbfs = MinimumDbfs,
        double RmsDbfs = MinimumDbfs,
        double NonSilentRatio = 0,
        string? AnalysisError = null,
        long? DecodedSampleCount = null,
        long? MeasuredSampleCount = null
    );
}
