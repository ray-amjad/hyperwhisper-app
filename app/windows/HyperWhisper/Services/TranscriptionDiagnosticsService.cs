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
    private const double LowSignalRmsDbfs = -50.0;
    private const double LowSignalNonSilentRatio = 0.02;

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
        TranscriptionException? exception = null)
    {
        var audioDiagnostics = AnalyzeAudioFile(audioPath, fallbackDurationSeconds);

        if (!ShouldCaptureNoSpeechDiagnostic(audioDiagnostics, providerDiagnostics))
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

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["component"] = "transcription",
            ["diagnostic_name"] = "no_speech",
            ["diagnostic_stage"] = diagnosticStage,
            ["diagnostic_source"] = diagnosticSource,
            ["provider_type"] = mode?.ProviderType ?? "unknown",
            ["cloud_provider"] = mode?.CloudProvider ?? "none",
            ["cloud_accuracy_tier"] = mode?.CloudAccuracyTier ?? "none",
            ["local_engine"] = mode?.LocalEngine ?? "none",
            ["backend_no_speech_detected"] = (providerDiagnostics?.BackendNoSpeechDetected ?? false).ToString().ToLowerInvariant(),
            ["audio_analysis_succeeded"] = audioDiagnostics.AnalysisSucceeded.ToString().ToLowerInvariant()
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

        var fingerprint = new[]
        {
            "transcription-no-speech",
            diagnosticStage,
            diagnosticSource,
            mode?.ProviderType ?? "unknown",
            mode?.CloudProvider ?? "none"
        };

        var dedupeKey = $"{transcriptId}:{diagnosticStage}:{diagnosticSource}";

        SentryService.CaptureDiagnosticEvent(
            message: "Windows transcription no-speech diagnostic",
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
                NonSilentRatio: Math.Round(nonSilentRatio, 4));
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

    private static bool ShouldCaptureNoSpeechDiagnostic(
        AudioAnalysisDiagnostics audioDiagnostics,
        TranscriptionProviderDiagnostics? providerDiagnostics)
    {
        if (!audioDiagnostics.AnalysisSucceeded)
        {
            return true;
        }

        if (audioDiagnostics.DurationSeconds <= 0 || audioDiagnostics.FileSizeBytes <= 0)
        {
            return true;
        }

        if (providerDiagnostics?.EmptyTranscriptWithoutFlag == true)
        {
            return true;
        }

        if (audioDiagnostics.NonSilentRatio == 0 &&
            audioDiagnostics.PeakDbfs < ConfirmedSilencePeakDbfs)
        {
            return false;
        }

        if (providerDiagnostics?.BackendNoSpeechDetected == true &&
            audioDiagnostics.NonSilentRatio <= LowSignalNonSilentRatio &&
            audioDiagnostics.RmsDbfs <= LowSignalRmsDbfs)
        {
            return false;
        }

        return true;
    }

    private sealed record AudioAnalysisDiagnostics(
        bool AnalysisSucceeded,
        double DurationSeconds,
        long FileSizeBytes,
        int SampleRate = 0,
        int Channels = 0,
        double PeakDbfs = MinimumDbfs,
        double RmsDbfs = MinimumDbfs,
        double NonSilentRatio = 0,
        string? AnalysisError = null
    );
}
