using System.Buffers.Binary;
using HyperWhisper.ModelManagement;
using HyperWhisper.SharedCore;

namespace HyperWhisper.FileTranscription;

public enum FileTranscriptionRoute { Local, Cloud }
public enum LocalTranscriptionEngine { Whisper, Parakeet }

public sealed record FileTranscriptionTarget(
    FileTranscriptionRoute Route,
    string Model,
    LocalTranscriptionEngine? LocalEngine = null,
    CloudTranscriptionProvider? CloudProvider = null,
    string? CloudCatalogTier = null);

public sealed record FileAudioMetadata(long LengthBytes, TimeSpan? Duration);

public sealed record FileTranscriptionConstraints(
    long? MaximumBytes,
    TimeSpan? MaximumDuration,
    IReadOnlySet<string> SupportedExtensions);

public enum FileTranscriptionPreflightError
{
    InvalidRequest,
    ProviderUnsupported,
    CredentialMissing,
    CredentialUnavailable,
    BackendUnavailable,
    ModelUnsupported,
    ModelNotInstalled,
    FileNotFound,
    FileUnreadable,
    FileEmpty,
    FormatUnsupported,
    DurationUnavailable,
    DurationInvalid,
    FileTooLarge,
    DurationTooLong,
    Cancelled,
}

public sealed record FileTranscriptionPreflightFailure(
    FileTranscriptionPreflightError Error,
    string Code,
    string Message);

public sealed record FileTranscriptionPreflightResult(
    FileAudioMetadata? Metadata,
    FileTranscriptionConstraints? Constraints,
    string? ResolvedModel,
    FileTranscriptionPreflightFailure? Failure)
{
    public bool IsSuccess => Failure is null && Metadata is not null;
    public static FileTranscriptionPreflightResult Success(
        FileAudioMetadata metadata, FileTranscriptionConstraints constraints, string model) =>
        new(metadata, constraints, model, null);
    public static FileTranscriptionPreflightResult Failed(
        FileTranscriptionPreflightError error, string code, string message) =>
        new(null, null, null, new(error, code, message));
}

public interface IFileAudioMetadataSource
{
    ValueTask<FileAudioMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

public interface ILocalFileTranscriptionReadiness
{
    ValueTask<bool> IsBackendAvailableAsync(
        LocalTranscriptionEngine engine, CancellationToken cancellationToken = default);
    ValueTask<bool> IsModelInstalledAsync(
        ManagedModel model, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates an imported audio file and its selected transcription target before
/// conversion, normalization, persistence, or network upload. Failures contain
/// stable codes and never include paths, credentials, file names, or media data.
/// </summary>
public sealed class PortableFileTranscriptionPreflight
{
    // Exact Linux import set. Local routes normalize these containers to WAV;
    // cloud routes retain the selected container for provider-native upload.
    private static readonly IReadOnlySet<string> PortableImportExtensions =
        new HashSet<string>(["wav", "mp3", "m4a", "flac", "ogg", "webm"], StringComparer.OrdinalIgnoreCase);

    private readonly IFileAudioMetadataSource _metadata;
    private readonly ILocalFileTranscriptionReadiness _localReadiness;
    private readonly ICloudCredentialSource _credentials;

    public PortableFileTranscriptionPreflight(
        IFileAudioMetadataSource metadata,
        ILocalFileTranscriptionReadiness localReadiness,
        ICloudCredentialSource credentials)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _localReadiness = localReadiness ?? throw new ArgumentNullException(nameof(localReadiness));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async ValueTask<FileTranscriptionPreflightResult> ValidateAsync(
        string path,
        FileTranscriptionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return Failure(
                FileTranscriptionPreflightError.InvalidRequest, "file_preflight.request_invalid",
                "An audio file is required.");

            var resolved = await ResolveTargetAsync(target, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;

            var extension = Path.GetExtension(path).TrimStart('.');
            if (!resolved.Constraints!.SupportedExtensions.Contains(extension)) return Failure(
                FileTranscriptionPreflightError.FormatUnsupported, "file_preflight.format_unsupported",
                "The selected transcription provider does not accept this audio format.");

            FileAudioMetadata? metadata;
            try { metadata = await _metadata.ReadAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (FileNotFoundException) { return Failure(
                FileTranscriptionPreflightError.FileNotFound, "file_preflight.file_not_found",
                "The selected audio file no longer exists."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { return Failure(FileTranscriptionPreflightError.FileUnreadable, "file_preflight.file_unreadable",
                "The selected audio file could not be read."); }

            if (metadata is null) return Failure(
                FileTranscriptionPreflightError.FileNotFound, "file_preflight.file_not_found",
                "The selected audio file no longer exists.");
            if (metadata.LengthBytes <= 0) return Failure(
                FileTranscriptionPreflightError.FileEmpty, "file_preflight.file_empty",
                "The selected audio file is empty.");
            if (resolved.Constraints.MaximumBytes is long bytes && metadata.LengthBytes > bytes) return Failure(
                FileTranscriptionPreflightError.FileTooLarge, "file_preflight.file_too_large",
                "The audio file exceeds the selected provider's upload limit.");
            if (metadata.Duration is { } duration &&
                (duration <= TimeSpan.Zero || double.IsNaN(duration.TotalSeconds) || double.IsInfinity(duration.TotalSeconds)))
                return Failure(FileTranscriptionPreflightError.DurationInvalid, "file_preflight.duration_invalid",
                    "The audio duration is invalid.");
            if (resolved.Constraints.MaximumDuration is { } maximumDuration)
            {
                if (metadata.Duration is null) return Failure(
                    FileTranscriptionPreflightError.DurationUnavailable, "file_preflight.duration_unavailable",
                    "The audio duration could not be established for this provider.");
                if (metadata.Duration > maximumDuration) return Failure(
                    FileTranscriptionPreflightError.DurationTooLong, "file_preflight.duration_too_long",
                    "The audio exceeds the selected provider's duration limit.");
            }
            return FileTranscriptionPreflightResult.Success(metadata, resolved.Constraints, resolved.Model!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(FileTranscriptionPreflightError.Cancelled, "file_preflight.cancelled",
                "File transcription preflight was cancelled.");
        }
    }

    private async ValueTask<TargetResolution> ResolveTargetAsync(
        FileTranscriptionTarget target, CancellationToken cancellationToken)
    {
        if (target.Route == FileTranscriptionRoute.Local)
        {
            if (target.LocalEngine is not { } engine) return TargetFailure(
                FileTranscriptionPreflightError.InvalidRequest, "file_preflight.local_engine_missing",
                "A local transcription engine is required.");
            var catalog = engine == LocalTranscriptionEngine.Parakeet
                ? PortableModelCatalog.Parakeet : PortableModelCatalog.Whisper;
            var model = catalog.FirstOrDefault(value =>
                string.Equals(value.Id, target.Model?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (model is null) return TargetFailure(
                FileTranscriptionPreflightError.ModelUnsupported, "file_preflight.model_unsupported",
                "The selected transcription model is not supported.");
            if (!await _localReadiness.IsModelInstalledAsync(model, cancellationToken).ConfigureAwait(false))
                return TargetFailure(FileTranscriptionPreflightError.ModelNotInstalled,
                    "file_preflight.model_not_installed", "The selected local transcription model is not installed.");
            if (!await _localReadiness.IsBackendAvailableAsync(engine, cancellationToken).ConfigureAwait(false))
                return TargetFailure(FileTranscriptionPreflightError.BackendUnavailable,
                    "file_preflight.backend_unavailable", "The selected local transcription engine is unavailable.");
            return new(model.Id, LocalConstraints(), null);
        }

        if (target.Route != FileTranscriptionRoute.Cloud || target.CloudProvider is not { } provider
            || !CloudCatalog.TryGetValue(provider, out var descriptor))
            return TargetFailure(FileTranscriptionPreflightError.ProviderUnsupported,
                "file_preflight.provider_unsupported", "The selected cloud transcription provider is not supported.");

        var requestedModel = target.Model?.Trim() ?? string.Empty;
        string modelId;
        if (provider == CloudTranscriptionProvider.HyperWhisperCloud)
        {
            var tier = SharedCoreBridge.CanonicalCloudSttTier(target.CloudCatalogTier);
            modelId = requestedModel.Length != 0 && SharedCoreBridge.CloudSttContainsModel(tier, requestedModel)
                ? requestedModel
                : SharedCoreBridge.CloudSttDefaultModel(tier) ?? string.Empty;
            if (modelId.Length == 0)
                return TargetFailure(FileTranscriptionPreflightError.ModelUnsupported,
                    "file_preflight.model_unsupported", "The selected transcription model is not supported by this provider.");
        }
        else
        {
            modelId = requestedModel.Length == 0 ? descriptor.DefaultModel : requestedModel;
            if (!descriptor.Models.Contains(modelId)) return TargetFailure(
                FileTranscriptionPreflightError.ModelUnsupported, "file_preflight.model_unsupported",
                "The selected transcription model is not supported by this provider.");
        }
        CloudCredential? credential;
        try { credential = await _credentials.GetCredentialAsync(provider, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return TargetFailure(FileTranscriptionPreflightError.CredentialUnavailable,
            "file_preflight.credential_unavailable", "The provider credential could not be read securely."); }
        var present = provider == CloudTranscriptionProvider.HyperWhisperCloud
            ? !string.IsNullOrWhiteSpace(credential?.LicenseKey) || !string.IsNullOrWhiteSpace(credential?.DeviceId)
            : descriptor.UsesAccountCredential
                ? !string.IsNullOrWhiteSpace(credential?.LicenseKey)
                : !string.IsNullOrWhiteSpace(credential?.ApiKey);
        if (!present) return TargetFailure(FileTranscriptionPreflightError.CredentialMissing,
            "file_preflight.credential_missing", "A credential is required for the selected cloud provider.");
        return new(modelId, descriptor.Constraints, null);
    }

    private static FileTranscriptionConstraints LocalConstraints() =>
        new(null, null, PortableImportExtensions);

    private static FileTranscriptionPreflightResult Failure(
        FileTranscriptionPreflightError error, string code, string message) =>
        FileTranscriptionPreflightResult.Failed(error, code, message);

    private static TargetResolution TargetFailure(
        FileTranscriptionPreflightError error, string code, string message) =>
        new(null, null, Failure(error, code, message));

    private sealed record TargetResolution(
        string? Model,
        FileTranscriptionConstraints? Constraints,
        FileTranscriptionPreflightResult? Failure);

    private sealed record CloudTargetDescriptor(
        string DefaultModel,
        IReadOnlySet<string> Models,
        FileTranscriptionConstraints Constraints,
        bool UsesAccountCredential = false);

    private static CloudTargetDescriptor Cloud(
        long maximumBytes, string defaultModel, string[] models,
        TimeSpan? maximumDuration = null, bool account = false) =>
        new(defaultModel, new HashSet<string>(models, StringComparer.Ordinal),
            new(maximumBytes, maximumDuration, PortableImportExtensions), account);

    // Byte caps mirror Windows CloudTranscriptionProvider.GetMaxFileSizeBytes.
    // Windows does not impose a hard file-import duration cap for these routes.
    // Duration is still validated when a metadata source supplies it; a future
    // authoritative provider bound can be added without changing this API.
    private static readonly IReadOnlyDictionary<CloudTranscriptionProvider, CloudTargetDescriptor> CloudCatalog =
        new Dictionary<CloudTranscriptionProvider, CloudTargetDescriptor>
        {
            [CloudTranscriptionProvider.OpenAi] = Cloud(25L * 1024 * 1024, "whisper-1",
                ["gpt-4o-mini-transcribe-2025-12-15", "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1", "gpt-transcribe"]),
            [CloudTranscriptionProvider.Groq] = Cloud(25L * 1024 * 1024, "whisper-large-v3-turbo",
                ["whisper-large-v3-turbo", "whisper-large-v3"]),
            [CloudTranscriptionProvider.Deepgram] = Cloud(2L * 1024 * 1024 * 1024, "nova-3-general",
                ["nova-3-general", "nova-3-medical", "nova-2-general", "nova-2-medical"]),
            [CloudTranscriptionProvider.AssemblyAi] = Cloud(5L * 1024 * 1024 * 1024, "universal-3-5-pro",
                ["universal-2", "universal-3-5-pro", "universal-2-medical", "universal-3-5-pro-medical"]),
            [CloudTranscriptionProvider.ElevenLabs] = Cloud(3L * 1024 * 1024 * 1024, "scribe_v2", ["scribe_v2"]),
            [CloudTranscriptionProvider.Mistral] = Cloud(100L * 1024 * 1024, "voxtral-mini-latest", ["voxtral-mini-latest"]),
            [CloudTranscriptionProvider.Soniox] = Cloud(1L * 1024 * 1024 * 1024, "stt-async-v5", ["stt-async-v5"]),
            [CloudTranscriptionProvider.Gemini] = Cloud(2L * 1024 * 1024 * 1024, "gemini-2.5-flash",
                ["gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-pro", "gemini-3.1-flash-lite", "gemini-3.6-flash", "gemini-3-flash-preview", "gemini-3.1-pro-preview"]),
            [CloudTranscriptionProvider.Grok] = Cloud(500L * 1024 * 1024, "", [""]),
            [CloudTranscriptionProvider.AzureMai] = Cloud(300L * 1024 * 1024, "mai-transcribe-1.5", ["mai-transcribe-1.5"], account: true),
            [CloudTranscriptionProvider.GoogleChirp] = Cloud(9_500_000L, "chirp_3", ["chirp_3"], account: true),
            [CloudTranscriptionProvider.HyperWhisperCloud] = Cloud(2L * 1024 * 1024 * 1024, "default", ["default"], account: true),
        };
}

/// <summary>
/// Reads file length from the stream and WAV duration from RIFF chunk metadata.
/// Payload chunks are skipped with seeking; audio bytes are never buffered.
/// Other containers report an unknown duration for a platform probe to supply.
/// </summary>
public sealed class StreamingFileAudioMetadataSource : IFileAudioMetadataSource
{
    private const int MaximumChunks = 256;

    public async ValueTask<FileAudioMetadata?> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) || length < 12)
            return new(length, null);

        var header = new byte[12];
        if (!await TryReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)
            || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return new(length, null);

        uint? bytesPerSecond = null;
        uint? dataBytes = null;
        var chunkHeader = new byte[8];
        for (var count = 0; count < MaximumChunks && stream.Position + 8 <= length; count++)
        {
            if (!await TryReadExactlyAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false)) break;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            var remaining = length - stream.Position;
            if (size > remaining) break;
            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8) && size >= 16)
            {
                var format = new byte[16];
                if (!await TryReadExactlyAsync(stream, format, cancellationToken).ConfigureAwait(false)) break;
                bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                stream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                dataBytes = size;
                stream.Seek(size, SeekOrigin.Current);
            }
            else stream.Seek(size, SeekOrigin.Current);
            if ((size & 1) != 0 && stream.Position < length) stream.Seek(1, SeekOrigin.Current);
            if (bytesPerSecond is > 0 && dataBytes is not null) break;
        }
        TimeSpan? duration = bytesPerSecond is > 0 && dataBytes is { } data
            ? TimeSpan.FromSeconds((double)data / bytesPerSecond.Value) : null;
        return new(length, duration);
    }

    private static async ValueTask<bool> TryReadExactlyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (current == 0) return false;
            read += current;
        }
        return true;
    }
}
