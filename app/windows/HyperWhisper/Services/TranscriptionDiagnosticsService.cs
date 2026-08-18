using System.IO;
using NAudio.Wave;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;

namespace HyperWhisper.Services;

/// <summary>
/// Captures privacy-safe diagnostics for transcription failures.
/// Focuses on audio signal quality and provider metadata rather than transcript content.
/// </summary>
public static class TranscriptionDiagnosticsService
{
    private const float SilenceThreshold = 0.01f;
    private const double MinimumDbfs = -120.0;
    private const double ConfirmedSilencePeakDbfs = -50.0;

    // Backend-confirmed low-signal skip: both conditions must hold (kept as && -
    // an OR would let a single quiet reading skip capture on its own, risking
    // suppression of genuine backend-disagreement anomalies this diagnostic exists
    // to catch). Widened from -50.0dBFS / 0.02 (HYPERWHISPER-PA, -QB, -VY: the real
    // no-speech sample RmsDbfs -39.64 / NonSilentRatio 0.046 with
    // BackendNoSpeechDetected true was well within "no speech" territory but was
    // being captured as a full Sentry issue because the old thresholds were too
    // strict) to -38.0dBFS / 0.06 - a modest margin over that sample (~1.6dB /
    // ~0.014 ratio) rather than doubling it, so a soft-spoken-user case with
    // meaningfully more non-silent signal (e.g. NonSilentRatio 0.07, well above
    // the incident sample's 0.046) still gets captured as a potential genuine
    // backend-disagreement anomaly instead of being silently skipped too. These
    // still reject (capture) a clearly loud/anomalous case, e.g. RmsDbfs around
    // -15 to -20dBFS with NonSilentRatio ~0.3+.
    private const double LowSignalRmsDbfs = -38.0;
    private const double LowSignalNonSilentRatio = 0.06;

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

        if (outcome == NoSpeechDiagnosticOutcome.Skip)
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
            ["audio_rms_dbfs_bucket"] = BucketDbfs(audioDiagnostics.RmsDbfs),
            ["selected_input_device_name"] = inputDeviceName ?? "n/a",
            ["capture_device_count"] = captureDeviceCount?.ToString() ?? "unknown"
        };

        var extras = new Dictionary<string, object>
        {
            ["transcript_id"] = transcriptId.ToString(),
            ["audio_path"] = audioPath,
            ["audio_file_exists"] = File.Exists(audioPath),
            ["audio_file_extension"] = Path.GetExtension(audioPath),
            ["audio_file_size_bytes"] = audioDiagnostics.FileSizeBytes,
            ["audio_duration_seconds"] = audioDiagnostics.DurationSeconds,
            ["audio_sample_rate_hz"] = audioDiagnostics.SampleRate,
            ["audio_channels"] = audioDiagnostics.Channels,
            ["audio_peak_dbfs"] = audioDiagnostics.PeakDbfs,
            ["audio_rms_dbfs"] = audioDiagnostics.RmsDbfs,
            ["audio_non_silent_ratio"] = audioDiagnostics.NonSilentRatio,
            // The honest "was anything captured" signal - audio_duration_seconds above
            // falls back to the caller's wall-clock value when the container has none.
            ["audio_decoded_sample_count"] = (object?)audioDiagnostics.DecodedSampleCount ?? "unknown",
            ["mode_name"] = mode?.Name ?? "unknown",
            ["mode_preset"] = mode?.Preset ?? "unknown",
            ["transcription_provider_display_name"] = transcriptionProviderDisplayName ?? providerDiagnostics?.ProviderDisplayName ?? exception?.ProviderName ?? "unknown",
            ["selected_input_device_name"] = inputDeviceName ?? "n/a",
            ["backend_request_id"] = providerDiagnostics?.BackendRequestId ?? "n/a",
            ["backend_stt_provider"] = providerDiagnostics?.BackendSttProvider ?? "n/a",
            ["backend_http_status"] = providerDiagnostics?.HttpStatusCode ?? 0,
            ["backend_response_latency_ms"] = providerDiagnostics?.ResponseLatencyMs ?? 0.0,
            ["backend_empty_transcript_without_flag"] = providerDiagnostics?.EmptyTranscriptWithoutFlag ?? false
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

        var fingerprint = BuildDiagnosticFingerprint(presentation.FingerprintRoot, diagnosticStage, diagnosticSource, mode);

        var dedupeKey = $"{transcriptId}:{diagnosticStage}:{diagnosticSource}:{presentation.Name}";

        SentryService.CaptureDiagnosticEvent(
            message: presentation.Message,
            extras: extras,
            tags: tags,
            fingerprint: fingerprint,
            dedupeKey: dedupeKey);
    }

    private static AudioAnalysisDiagnostics AnalyzeAudioFile(string audioPath, double? fallbackDurationSeconds)
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

            var buffer = new float[4096];
            long sampleCount = 0;
            long nonSilentSampleCount = 0;
            double sumSquares = 0;
            double peak = 0;

            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var abs = Math.Abs(buffer[i]);
                    peak = Math.Max(peak, abs);
                    sumSquares += abs * abs;

                    if (abs >= SilenceThreshold)
                    {
                        nonSilentSampleCount++;
                    }
                }

                sampleCount += read;
            }

            var rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
            var nonSilentRatio = sampleCount > 0 ? (double)nonSilentSampleCount / sampleCount : 0;
            var durationSeconds = reader.TotalTime.TotalSeconds > 0
                ? reader.TotalTime.TotalSeconds
                : fallbackDurationSeconds ?? 0;

            return new AudioAnalysisDiagnostics(
                AnalysisSucceeded: true,
                DurationSeconds: Math.Round(durationSeconds, 3),
                FileSizeBytes: fileInfo.Length,
                SampleRate: reader.WaveFormat.SampleRate,
                Channels: reader.WaveFormat.Channels,
                PeakDbfs: ToDbfs(peak),
                RmsDbfs: ToDbfs(rms),
                NonSilentRatio: Math.Round(nonSilentRatio, 4),
                DecodedSampleCount: sampleCount);
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
                AnalysisError: ex.Message);
        }
    }

    private static double ToDbfs(double linear)
    {
        if (linear <= 0)
        {
            return MinimumDbfs;
        }

        return Math.Round(20 * Math.Log10(linear), 2);
    }

    /// <summary>
    /// Buckets a dBFS value to the nearest 5dB step (e.g. -38.2 -> "-40dbfs") for
    /// use as a low-cardinality Sentry tag. A raw float would give every event its
    /// own effectively-unique value, making it useless for faceting/segmenting.
    /// </summary>
    private static string BucketDbfs(double dbfs)
    {
        if (dbfs <= MinimumDbfs)
        {
            return "silent";
        }

        var bucket = (int)(Math.Floor(dbfs / 5.0) * 5.0);
        return $"{bucket}dbfs";
    }

    /// <summary>
    /// Builds the Sentry grouping fingerprint. The last element is the honest
    /// provider axis: <c>Mode.CloudProvider</c> and <c>Mode.ProviderType</c> are
    /// independent persisted fields, so a mode switched from cloud to local keeps a
    /// stale vendor value forever. Grouping on it unconditionally split ONE local-mode
    /// condition across four Sentry issues (HYPERWHISPER-QB local+hyperwhisper,
    /// -RM local+none, -XB local+gemini, -XR local+groq). Local modes therefore group
    /// on their local engine; cloud modes keep grouping per vendor.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string[] BuildDiagnosticFingerprint(
        string fingerprintRoot,
        string diagnosticStage,
        string diagnosticSource,
        Mode? mode)
    {
        return new[]
        {
            fingerprintRoot,
            diagnosticStage,
            diagnosticSource,
            mode?.ProviderType ?? "unknown",
            IsLocalMode(mode) ? ResolveLocalEngine(mode) : (mode?.CloudProvider ?? "none")
        };
    }

    /// <summary>
    /// The <c>cloud_provider</c> tag with the same staleness masked off, so faceting
    /// on it doesn't attribute local-mode events to a cloud vendor the mode no longer
    /// uses.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string ResolveCloudProviderTag(Mode? mode)
        => IsLocalMode(mode) ? "none" : (mode?.CloudProvider ?? "none");

    private static bool IsLocalMode(Mode? mode)
        => string.Equals(mode?.ProviderType, "local", StringComparison.OrdinalIgnoreCase);

    private static string ResolveLocalEngine(Mode? mode)
        => string.IsNullOrWhiteSpace(mode?.LocalEngine) ? "none" : mode!.LocalEngine;

    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal enum NoSpeechDiagnosticOutcome
    {
        /// <summary>Expected/benign - capture nothing.</summary>
        Skip,

        /// <summary>Nothing was recorded at all - a recorder failure, reported separately.</summary>
        EmptyRecording,

        /// <summary>Audio exists but produced no transcript - the original diagnostic.</summary>
        NoSpeech
    }

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
    internal static DiagnosticPresentation ResolveDiagnosticPresentation(NoSpeechDiagnosticOutcome outcome)
        => outcome switch
        {
            // The message is the Sentry group identity for eight live issues
            // (HYPERWHISPER-PA/-QB/-RM/-T6/-VY/-XB/-XR/-W7). It must stay
            // character-identical - editing it starts a new group and orphans them.
            NoSpeechDiagnosticOutcome.NoSpeech => new DiagnosticPresentation(
                Name: "no_speech",
                Message: "Windows transcription no-speech diagnostic",
                FingerprintRoot: "transcription-no-speech"),

            NoSpeechDiagnosticOutcome.EmptyRecording => new DiagnosticPresentation(
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
    internal static NoSpeechDiagnosticOutcome ClassifyNoSpeechDiagnostic(
        AudioAnalysisDiagnostics audioDiagnostics,
        TranscriptionProviderDiagnostics? providerDiagnostics)
    {
        // MUST stay first: with no usable analysis we can't tell an empty recording
        // from a quiet one, so fall back to the full no-speech report.
        if (!audioDiagnostics.AnalysisSucceeded)
        {
            return NoSpeechDiagnosticOutcome.NoSpeech;
        }

        // A header-only / zero-frame file means the recorder produced nothing, which
        // is a different fault from "we recorded audio and got no words back". It is
        // still reported - just under its own name and fingerprint - so a real
        // recorder failure never gets silently dropped.
        //
        // The discriminator is the decoded sample count, NOT DurationSeconds or
        // FileSizeBytes, both of which lie here:
        //  - DurationSeconds falls back to the caller's value when the container
        //    reports none, so a header-only WAV from a 5-second recording arrives as
        //    5.0 (false negative: it would fall through to the dead-silence rule and
        //    be reported as nothing at all), while a decodable file whose container
        //    reports no duration arrives as 0 on the file-transcription path, where no
        //    recorder ever ran (false positive).
        //  - FileSizeBytes <= 0 cannot co-occur with AnalysisSucceeded: AudioFileReader
        //    has already parsed a header by then, and a zero-byte file throws out to
        //    the catch as AnalysisSucceeded: false.
        // Zero decoded samples is true in both directions and needs no fallback.
        // Null (unknown - no read loop ran) is deliberately not empty.
        if (audioDiagnostics.DecodedSampleCount == 0)
        {
            return NoSpeechDiagnosticOutcome.EmptyRecording;
        }

        if (providerDiagnostics?.EmptyTranscriptWithoutFlag == true)
        {
            return NoSpeechDiagnosticOutcome.NoSpeech;
        }

        if (audioDiagnostics.NonSilentRatio == 0 &&
            audioDiagnostics.PeakDbfs < ConfirmedSilencePeakDbfs)
        {
            return NoSpeechDiagnosticOutcome.Skip;
        }

        if (providerDiagnostics?.BackendNoSpeechDetected == true &&
            audioDiagnostics.NonSilentRatio <= LowSignalNonSilentRatio &&
            audioDiagnostics.RmsDbfs <= LowSignalRmsDbfs)
        {
            return NoSpeechDiagnosticOutcome.Skip;
        }

        return NoSpeechDiagnosticOutcome.NoSpeech;
    }

    /// <summary>
    /// True when the input is reported <i>as a no-speech diagnostic</i>. This is NOT
    /// "is anything captured": an <see cref="NoSpeechDiagnosticOutcome.EmptyRecording"/>
    /// is also captured, under its own name and fingerprint, and returns false here.
    /// Only <see cref="NoSpeechDiagnosticOutcome.Skip"/> means nothing is reported.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static bool ShouldCaptureAsNoSpeech(
        AudioAnalysisDiagnostics audioDiagnostics,
        TranscriptionProviderDiagnostics? providerDiagnostics)
        => ClassifyNoSpeechDiagnostic(audioDiagnostics, providerDiagnostics) == NoSpeechDiagnosticOutcome.NoSpeech;

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
    /// Samples the decoder actually produced while reading the file, or <c>null</c> when
    /// no read loop ran (analysis failed, or a synthetic record in a test). Zero is the
    /// only honest "the recorder captured nothing" signal - see
    /// <see cref="ClassifyNoSpeechDiagnostic"/>. Null means unknown, never empty.
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
        long? DecodedSampleCount = null
    );
}
