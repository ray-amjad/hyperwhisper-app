using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using HyperWhisper.SharedCore;
using AudioAnalysisDiagnostics = HyperWhisper.Services.TranscriptionDiagnosticsService.AudioAnalysisDiagnostics;

namespace HyperWhisper.Services;

/// <summary>
/// The NAudio half of the no-speech diagnostic: decodes a recording and measures its
/// signal for <see cref="TranscriptionDiagnosticsService"/>. Split out so the Sentry
/// payload builder no longer carries the NAudio decode loop (#811).
/// </summary>
internal static class TranscriptionAudioAnalyzer
{
    /// <summary>
    /// The measurement basis for <see cref="Analyze"/>: 16 kHz mono,
    /// matching what every transcription path actually sends
    /// (<c>TranscriptionService.ReadAudioChunks</c>,
    /// <c>FileTranscriptionService.ConvertToWhisperFormatAsync</c>, the parakeet
    /// daemon) and what macOS's
    /// <c>AudioConverter</c> already measured on. Measuring the container format
    /// instead made the same recording report a different non-silent ratio on
    /// each platform, which is exactly the cross-platform comparability #291
    /// exists to create.
    /// </summary>
    private const int AnalysisSampleRate = 16000;

    /// <summary>
    /// Measure the recording's signal. Decodes to <see cref="AnalysisSampleRate"/>
    /// mono first, so the numbers mean the same thing here as on macOS and as in
    /// the audio the provider was actually sent.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static AudioAnalysisDiagnostics Analyze(string audioPath, double? fallbackDurationSeconds)
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
            // AudioFileDecoder, not AudioFileReader: a WAVE_FORMAT_EXTENSIBLE WAV (any
            // ffmpeg WAV with more than two channels) otherwise goes to ACM and fails, and
            // the analysis would report a readable file as AnalysisSucceeded: false (#1450).
            using var reader = AudioFileDecoder.Open(audioPath);

            // Same pair the transcription paths already run
            // (TranscriptionService.ToWhisperSamples,
            // FileTranscriptionService.ConvertToWhisperFormatAsync:119-128): fold to
            // mono, then resample to 16 kHz.
            //
            // The fold is AudioFileDecoder.ToMono (MonoFoldSampleProvider) rather than
            // NAudio's ToMono() / StereoToMonoSampleProvider, which handle two channels and throw
            // NotImplementedException on anything else. Throwing here would downgrade a
            // perfectly readable multichannel file to AnalysisSucceeded: false, and NOT
            // folding it would measure the interleaved stream - a 3-channel 48 kHz file
            // would contribute 48,000 samples a second instead of 16,000, and one live
            // channel among two silent ones would report a third of the true non-silent
            // ratio and several dB of extra headroom on RMS. Both change which
            // classification arm fires. Averaging every channel matches what the 2-channel
            // case already did (NAudio 2.2.1's StereoToMonoSampleProvider defaults to
            // 0.5/0.5) and extends it to any channel count.
            ISampleProvider provider = AudioFileDecoder.ToMono(reader.Samples);

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
