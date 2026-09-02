using System.Diagnostics;
using System.IO;
using HyperWhisper.FileTranscription;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
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
        await ValidateFinalWaveAsync(audioPath, cancellationToken);

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

    internal static async Task ValidateFinalWaveAsync(
        string audioPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await new StreamingFileAudioMetadataSource()
                .ReadAsync(audioPath, cancellationToken);
            switch (MetaMuseAudioContract.ValidateCanonical(metadata))
            {
            case MetaMuseAudioProblem.InvalidFormat:
                throw new TranscriptionException(
                    TranscriptionErrorCode.UnsupportedFormat,
                    "Meta Muse requires mono 16-bit PCM WAV audio at 16 kHz or 24 kHz.",
                    "Meta Muse");
            case MetaMuseAudioProblem.DurationTooLong:
                throw new TranscriptionException(
                    TranscriptionErrorCode.InvalidRequest,
                    "Meta Muse accepts audio up to 10 minutes.",
                    "Meta Muse");
            case MetaMuseAudioProblem.FileTooLarge:
                throw new TranscriptionException(
                    TranscriptionErrorCode.FileTooLarge,
                    "The audio file exceeds Meta Muse's 32 MB limit.",
                    "Meta Muse");
            case MetaMuseAudioProblem.None:
                return;
            }
        }
        catch (TranscriptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.UnsupportedFormat,
                "Meta Muse requires a valid WAV audio file.",
                "Meta Muse",
                ex);
        }
    }
}
