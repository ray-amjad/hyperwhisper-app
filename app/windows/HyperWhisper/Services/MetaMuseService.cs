using System.Diagnostics;
using System.IO;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using NAudio.Wave;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Direct Meta Model API batch transcription. The shared Rust core owns the
/// multipart request and response parser; this class owns only host I/O.
/// </summary>
public sealed class MetaMuseService : ApiKeyTranscriptionServiceBase
{
    private const string DefaultModelId = "muse-voice-transcribe-1.0";

    public MetaMuseService() : base(TimeSpan.FromSeconds(300), DefaultModelId) { }

    public override string Name =>
        $"Meta {CloudTranscriptionModels.GetById(ModelId, CloudTranscriptionProvider.Meta)?.DisplayName ?? ModelId}";

    public override void Configure(string apiKey, string modelId = DefaultModelId)
    {
        ApiKey = apiKey?.Trim();
        ModelId = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId;
        LoggingService.Info($"MetaMuseService: Configured with model {ModelId}");
    }

    public override async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        var maxBytes = CloudTranscriptionProvider.Meta.GetMaxFileSizeBytes();
        TranscriptionPreflight.Validate("Meta Muse", ApiKey, audioPath, maxBytes, "32 MB");
        ValidateFinalWave(audioPath, maxBytes);

        var coreParams = BuildDirectVendorParams(
            audioPath,
            "audio/wav",
            language,
            vocabulary);

        return await RustSingleShot.TranscribeAsync(
            Http,
            "Meta Muse",
            buildRequest: () => HyperwhisperCoreMethods.MetaBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.MetaParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }

    internal static void ValidateFinalWave(string audioPath, long maxBytes)
    {
        try
        {
            using var reader = new WaveFileReader(audioPath);
            var format = reader.WaveFormat;
            var isSupported = format.Encoding == WaveFormatEncoding.Pcm
                              && format.BitsPerSample == 16
                              && format.Channels == 1
                              && format.SampleRate is 16000 or 24000;
            if (!isSupported)
            {
                throw new TranscriptionException(
                    TranscriptionErrorCode.UnsupportedFormat,
                    "Meta Muse requires mono 16-bit PCM WAV audio at 16 kHz or 24 kHz.",
                    "Meta Muse");
            }

            if (reader.TotalTime > TimeSpan.FromMinutes(10))
            {
                throw new TranscriptionException(
                    TranscriptionErrorCode.InvalidRequest,
                    "Meta Muse accepts audio up to 10 minutes.",
                    "Meta Muse");
            }

            // Re-read the final artifact metadata immediately before the Rust
            // request builder reads it. Never trust an earlier conversion stat.
            if (new FileInfo(audioPath).Length > maxBytes)
            {
                throw new TranscriptionException(
                    TranscriptionErrorCode.FileTooLarge,
                    "The audio file exceeds Meta Muse's 32 MB limit.",
                    "Meta Muse");
            }
        }
        catch (TranscriptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.UnsupportedFormat,
                "Meta Muse requires a valid WAV audio file.",
                "Meta Muse",
                ex);
        }
    }
}
