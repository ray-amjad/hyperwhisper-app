using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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
/// NAudio decode loop, the Sentry payload and the platform-distinct
/// name/message/fingerprint-root are Windows'.
/// </para>
/// </summary>
public static class TranscriptionDiagnosticsService
{
    /// <summary>
    /// The measurement basis for <see cref="AnalyzeAudioFile"/>: 16 kHz mono,
    /// matching what every transcription path actually sends
    /// (<c>TranscriptionService.PrepareAudioStream</c>,
    /// <c>FileTranscriptionService.ConvertToWhisperFormatAsync</c>, the parakeet
    /// daemon) and what macOS's
    /// <c>AudioConverter</c> already measured on. Measuring the container format
    /// instead made the same recording report a different non-silent ratio on
    /// each platform, which is exactly the cross-platform comparability #291
    /// exists to create.
    /// </summary>
    private const int AnalysisSampleRate = 16000;

    /// <summary>
    /// Duplicated from <see cref="PortableNoSpeechDiagnostics.MinimumDbfs"/>
    /// ONLY because <see cref="AudioAnalysisDiagnostics"/>'s optional parameters
    /// need a compile-time constant. Never compare against this — use the
    /// portable property. A smoke test asserts the two agree.
    /// </summary>
    private const double MinimumDbfs = -120.0;

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
        int? captureDeviceCount = null)
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
            captureDeviceCount);

        var fingerprint = BuildDiagnosticFingerprint(presentation.FingerprintRoot, diagnosticStage, diagnosticSource, mode);

        var dedupeKey = $"{transcriptId}:{diagnosticStage}:{diagnosticSource}:{presentation.Name}";

        SentryService.CaptureDiagnosticEvent(
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
    /// is the extras KEYS: <see cref="SentryService.IsRedactedExtraKey"/> replaces
    /// the value of any key containing "transcript", "text", "prompt" or "path" with
    /// <c>"[redacted]"</c>, and three of these fields were named that way, so they
    /// arrived empty on every event of HYPERWHISPER-PA/-RM/-XR with nothing at the
    /// call site to say so.
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
        int? captureDeviceCount)
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

        var extras = new Dictionary<string, object>
        {
            // KEY NAMES ARE LEAD-BEARING HERE. SentryService.IsRedactedExtraKey does a
            // substring match on the key and replaces the value with "[redacted]", so
            // an extra whose name contains "transcript", "text", "prompt" or "path"
            // never arrives - silently, with no warning at the call site. This
            // dictionary shipped three such names ("transcript_id",
            // "transcription_provider_display_name",
            // "backend_empty_transcript_without_flag"), and every event of
            // HYPERWHISPER-PA/-RM/-XR carried "[redacted]" for all three. The names
            // below are chosen to clear that filter; a smoke test asserts it for the
            // whole dictionary so the next one fails in CI instead.
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
            // AnalysisSampleRate) but these two must keep reporting what the
            // recorder actually wrote, because that is the fact a device or
            // format regression shows up in.
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
            ["selected_input_device_name"] = inputDeviceName ?? "n/a",
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

        return (tags, extras);
    }

    /// <summary>
    /// Measure the recording's signal. Decodes to <see cref="AnalysisSampleRate"/>
    /// mono first, so the numbers mean the same thing here as on macOS and as in
    /// the audio the provider was actually sent.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static AudioAnalysisDiagnostics AnalyzeAudioFile(string audioPath, double? fallbackDurationSeconds)
    {
        if (!File.Exists(audioPath))
        {
            return new AudioAnalysisDiagnostics(
                AnalysisSucceeded: false,
                DurationSeconds: fallbackDurationSeconds ?? 0,
                FileSizeBytes: 0,
                AnalysisError: "Audio file not found");
        }

        try
        {
            var fileInfo = new FileInfo(audioPath);
            using var reader = new AudioFileReader(audioPath);

            // Same pair the transcription paths already run
            // (TranscriptionService.PrepareAudioStream:820-828,
            // FileTranscriptionService.ConvertToWhisperFormatAsync:119-128): fold to
            // mono, then resample to 16 kHz.
            //
            // The fold is MonoFoldSampleProvider rather than NAudio's ToMono() /
            // StereoToMonoSampleProvider, which handle two channels and throw
            // NotImplementedException on anything else. Throwing here would downgrade a
            // perfectly readable multichannel file to AnalysisSucceeded: false, and NOT
            // folding it would measure the interleaved stream - a 3-channel 48 kHz file
            // would contribute 48,000 samples a second instead of 16,000, and one live
            // channel among two silent ones would report a third of the true non-silent
            // ratio and several dB of extra headroom on RMS. Both change which
            // classification arm fires. Averaging every channel matches what the 2-channel
            // case already did (NAudio 2.2.1's StereoToMonoSampleProvider defaults to
            // 0.5/0.5) and extends it to any channel count.
            ISampleProvider provider = reader;
            if (provider.WaveFormat.Channels > 1)
            {
                provider = new MonoFoldSampleProvider(provider);
            }

            // Counted BEFORE the resampler on purpose - see the extras comment in
            // CaptureNoSpeechDiagnostic. WdlResamplingSampleProvider.Read returns 0 when it
            // cannot fill a whole output frame, so a decodable-but-tiny file would report
            // zero decoded samples and the empty-recording arm would call it "the recorder
            // produced nothing", which is false.
            var decoded = new CountingSampleProvider(provider);
            provider = decoded;

            if (provider.WaveFormat.SampleRate != AnalysisSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, AnalysisSampleRate);
            }

            // Read once, outside the loop: every member of PortableNoSpeechDiagnostics
            // crosses the FFI boundary.
            var silenceThreshold = PortableNoSpeechDiagnostics.SilenceThreshold;

            var buffer = new float[4096];
            ulong sampleCount = 0;
            ulong nonSilentSampleCount = 0;
            double sumSquares = 0;
            double peak = 0;

            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var abs = Math.Abs(buffer[i]);
                    // `>` and not Math.Max: a NaN sample must not become the peak. Math.Max
                    // PROPAGATES NaN, which floors the peak to -120 dBFS and silently
                    // changes which arm fires; `abs > peak` is false for NaN, so the sample
                    // is ignored. This is the rule hw_audio::no_speech::accumulate applies,
                    // and macOS applies the same one - see that function's "Non-finite
                    // input" note.
                    if (abs > peak)
                    {
                        peak = abs;
                    }
                    sumSquares += (double)abs * abs;

                    if (abs >= silenceThreshold)
                    {
                        nonSilentSampleCount++;
                    }
                }

                sampleCount += (ulong)read;
            }

            var summary = PortableNoSpeechDiagnostics.Summarize(new PortableSignalAccumulation(
                SampleCount: sampleCount,
                NonSilentCount: nonSilentSampleCount,
                SumSquares: sumSquares,
                Peak: peak));

            var durationSeconds = reader.TotalTime.TotalSeconds > 0
                ? reader.TotalTime.TotalSeconds
                : fallbackDurationSeconds ?? 0;

            return new AudioAnalysisDiagnostics(
                AnalysisSucceeded: true,
                DurationSeconds: Math.Round(durationSeconds, 3),
                FileSizeBytes: fileInfo.Length,
                // The source container's format. The measurements above are on
                // 16 kHz mono; these two stay the recorder's own facts.
                SampleRate: reader.WaveFormat.SampleRate,
                Channels: reader.WaveFormat.Channels,
                PeakDbfs: summary.PeakDbfs,
                RmsDbfs: summary.RmsDbfs,
                NonSilentRatio: summary.NonSilentRatio,
                DecodedSampleCount: decoded.SampleCount,
                MeasuredSampleCount: (long)sampleCount);
        }
        catch (Exception ex)
        {
            var fileSizeBytes = 0L;
            try
            {
                fileSizeBytes = new FileInfo(audioPath).Length;
            }
            catch
            {
                // Ignore file metadata errors in diagnostics fallback path.
            }

            return new AudioAnalysisDiagnostics(
                AnalysisSucceeded: false,
                DurationSeconds: fallbackDurationSeconds ?? 0,
                FileSizeBytes: fileSizeBytes,
                AnalysisError: DescribeAnalysisError(ex));
        }
    }

    /// <summary>
    /// The error identity, without the error text. This value is reported to
    /// Sentry as <c>audio_analysis_error</c>, and an IO or NAudio message
    /// routinely embeds the full path it failed on — which carries the user's
    /// Windows account name. The type and the HRESULT say which fault it was
    /// without saying whose file it was.
    /// </summary>
    private static string DescribeAnalysisError(Exception ex)
        => $"{ex.GetType().Name} (0x{ex.HResult:X8})";

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

    /// <summary>
    /// Folds any channel count down to mono by averaging across channels, for any channel
    /// count and without throwing.
    /// </summary>
    /// <remarks>
    /// NAudio 2.2.1's <c>ToMono()</c> and <see cref="StereoToMonoSampleProvider"/> handle
    /// exactly two channels and throw <see cref="NotImplementedException"/> on anything
    /// else. A diagnostic must not turn a readable file into an analysis failure, and it
    /// must not measure a multichannel stream interleaved either, so it folds its own.
    /// A plain average is the same 0.5/0.5 mix the 2-channel provider defaults to,
    /// generalized to N channels.
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo, which drives it with a source that returns awkward
    // read counts - see the short-read test.
    internal sealed class MonoFoldSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        /// <summary>
        /// Samples of an incomplete frame held over from the previous <see cref="Read"/>, at the
        /// start of <see cref="_sourceBuffer"/>. A source is free to return any count it likes,
        /// including one that ends mid-frame; dropping the remainder instead of carrying it
        /// would rotate every later frame across the channels by that many samples.
        /// </summary>
        private int _pending;

        internal MonoFoldSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            var required = count * _channels;
            if (_sourceBuffer.Length < required)
            {
                // Resize, not reallocate: the carried partial frame lives at the start of it.
                Array.Resize(ref _sourceBuffer, required);
            }

            // Returning fewer than `count` frames is legal; returning 0 while the source still
            // has audio is not — both the analysis loop and WdlResamplingSampleProvider read a 0
            // as end-of-stream, so a source that returned 0 < read < _channels once would
            // silently truncate the measurement and still report AnalysisSucceeded: true. Keep
            // pulling until a whole frame exists or the source is genuinely exhausted.
            var available = _pending;
            while (available < _channels)
            {
                var read = _source.Read(_sourceBuffer, available, required - available);
                if (read <= 0)
                {
                    break;
                }

                available += read;
            }

            var frames = available / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                var start = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _sourceBuffer[start + channel];
                }

                buffer[offset + frame] = (float)(sum / _channels);
            }

            // Carry whatever did not make up a whole frame to the next call.
            _pending = available - (frames * _channels);
            if (_pending > 0)
            {
                Array.Copy(_sourceBuffer, frames * _channels, _sourceBuffer, 0, _pending);
            }

            return frames;
        }
    }

    /// <summary>
    /// Passes samples straight through and counts them. Inserted before the resampler so
    /// the empty-recording arm reads a count that means "the decoder produced nothing"
    /// rather than "the resampler could not fill an output frame yet".
    /// </summary>
    private sealed class CountingSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public long SampleCount { get; private set; }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read > 0)
            {
                SampleCount += read;
            }

            return read;
        }
    }
}
