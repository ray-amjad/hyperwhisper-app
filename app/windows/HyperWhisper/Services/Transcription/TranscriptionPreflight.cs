// TRANSCRIPTION PREFLIGHT
//
// The checks and the container→MIME lookup every BYOK cloud STT service runs
// before it builds a request. Nine services (OpenAI, Groq, Mistral, Deepgram,
// ElevenLabs, Grok, Soniox, Gemini, AssemblyAI) each carried their own copy of
// the same three-step gate — API key present, audio file exists, file size
// logged and optionally capped — and six of them carried a byte-for-byte
// identical MIME dictionary. The copies had already drifted in small ways
// (different fallback MIME types, different size-limit wording), so "what a
// provider checks before it calls out" was defined in nine places at once.
//
// This file is the single copy. It emits exactly the same exceptions and the
// same log line the inline blocks emitted, so behaviour is unchanged.

using System.Diagnostics.CodeAnalysis;
using System.IO;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Transcription;

internal static class TranscriptionPreflight
{
    /// <summary>
    /// The container→MIME map the Whisper-style upload providers share
    /// (OpenAI, Groq, Mistral, Deepgram, ElevenLabs, AssemblyAI).
    ///
    /// Keys are file extensions WITH the leading dot, matched case-insensitively
    /// — <see cref="Path.GetExtension(string)"/> returns them in that shape.
    /// Providers that accept a different container set (Grok, Soniox) keep their
    /// own map and pass it to <see cref="MimeTypeFor"/>; Gemini extends this one
    /// via <see cref="StandardMimeTypesPlus"/>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> StandardMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".wav", "audio/wav" },
            { ".mp3", "audio/mpeg" },
            { ".mp4", "audio/mp4" },
            { ".m4a", "audio/mp4" },
            { ".mpeg", "audio/mpeg" },
            { ".mpga", "audio/mpeg" },
            { ".webm", "audio/webm" },
            { ".ogg", "audio/ogg" },
            { ".flac", "audio/flac" }
        };

    /// <summary>
    /// <see cref="StandardMimeTypes"/> plus <paramref name="extra"/>, for a
    /// provider that accepts every standard container and some more. An entry
    /// in <paramref name="extra"/> that repeats a standard extension overrides
    /// it.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> StandardMimeTypesPlus(
        params (string Extension, string MimeType)[] extra)
    {
        var map = new Dictionary<string, string>(StandardMimeTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var (extension, mimeType) in extra)
        {
            map[extension] = mimeType;
        }

        return map;
    }

    /// <summary>
    /// The MIME type to send for <paramref name="audioPath"/>, or
    /// <paramref name="fallback"/> when the extension is not in
    /// <paramref name="mimeTypes"/> (defaults to
    /// <see cref="StandardMimeTypes"/>).
    ///
    /// The fallback is per-provider on purpose: the multipart providers send
    /// <c>audio/wav</c> for an unknown container, the ones whose upstream sniffs
    /// the container itself send <c>application/octet-stream</c>.
    /// </summary>
    internal static string MimeTypeFor(
        string audioPath,
        string fallback,
        IReadOnlyDictionary<string, string>? mimeTypes = null)
    {
        return (mimeTypes ?? StandardMimeTypes).GetValueOrDefault(Path.GetExtension(audioPath), fallback);
    }

    /// <summary>
    /// Run the pre-request gate for <paramref name="providerName"/> and return
    /// the audio file's <see cref="FileInfo"/>.
    ///
    /// In order: rejects a missing API key, rejects a missing file, logs the
    /// file size, then — when <paramref name="maxFileSizeBytes"/> is given —
    /// rejects a file over the cap. <paramref name="maxFileSizeLabel"/> is the
    /// human-readable limit used in that message ("25 MB", "500 MB", "5 GB");
    /// each provider words its own limit, so the caller supplies it.
    ///
    /// <paramref name="apiKey"/> carries <c>[NotNull]</c> because this method
    /// throws when it is null: that keeps the caller's nullable flow analysis
    /// exactly where the inline <c>if (string.IsNullOrEmpty(_apiKey)) throw</c>
    /// left it, so the key still reads as non-null after the call.
    /// </summary>
    /// <exception cref="TranscriptionException">
    /// <see cref="TranscriptionErrorCode.ApiKeyMissing"/>,
    /// <see cref="TranscriptionErrorCode.AudioFileNotFound"/> or
    /// <see cref="TranscriptionErrorCode.FileTooLarge"/>.
    /// </exception>
    internal static FileInfo Validate(
        string providerName,
        [NotNull] string? apiKey,
        string audioPath,
        long? maxFileSizeBytes = null,
        string? maxFileSizeLabel = null)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                $"{providerName} API key not configured",
                providerName);
        }

        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                providerName);
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        if (maxFileSizeBytes is long limit && fileInfo.Length > limit)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.FileTooLarge,
                $"File size ({fileInfo.Length / 1024.0 / 1024.0:F1} MB) exceeds {maxFileSizeLabel} limit",
                providerName);
        }

        return fileInfo;
    }
}
