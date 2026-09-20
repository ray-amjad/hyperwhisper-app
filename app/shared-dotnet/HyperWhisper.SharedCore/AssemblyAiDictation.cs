using System.Buffers.Binary;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>One-shot Dictation transport shared by the Windows and portable adapters.</summary>
internal static class AssemblyAiDictation
{
    internal static async Task<HwTranscript> TranscribeAsync(
        TranscribeParams parameters, HttpClient client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(parameters.routedDomain))
            throw new InvalidDataException("AssemblyAI Dictation does not support Medical Mode or other domains. Select Dictation without a domain.");
        ValidateWave(parameters.audioPath);
        // Build before starting I/O. The core rejects Auto/unsupported languages.
        uniffi.hyperwhisper_core.HttpRequest request;
        try
        {
            request = HyperwhisperCoreMethods.AssemblyaiBuildDictationRequest(parameters with { audioMime = "audio/wav" });
        }
        catch (HwTranscriptionException.BadRequest error)
        {
            throw new InvalidDataException(error.message, error);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(HyperwhisperCoreMethods.AssemblyaiDictationTimeoutMs()));
        try
        {
            var response = await RustHttpTransport.ExecuteAsync(request, client, timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return HyperwhisperCoreMethods.AssemblyaiParseDictationResponse(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("AssemblyAI Dictation timed out. Try the recording again.");
        }
    }

    // Read actual RIFF chunks, not MIME labels, caller duration hints or file-size estimates.
    // Both desktop recorders already emit PCM16 WAV. Unsupported imports fail explicitly.
    internal static double ValidateWave(string path)
    {
        const string invalid = "AssemblyAI Dictation requires a valid PCM16 WAV. Convert this recording to WAV or select another model.";
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)
                || (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8 != stream.Length)
                throw new InvalidDataException(invalid);
            bool hasFormat = false;
            uint? dataBytes = null;
            ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
            uint sampleRate = 0, byteRate = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (stream.Position < stream.Length)
            {
                stream.ReadExactly(chunk);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var end = stream.Position + size + (size & 1);
                if (end > stream.Length) throw new InvalidDataException(invalid);
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (hasFormat || size < 16) throw new InvalidDataException(invalid);
                    hasFormat = true;
                    stream.ReadExactly(fmt);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    if (dataBytes.HasValue) throw new InvalidDataException(invalid);
                    dataBytes = size;
                }
                stream.Position = end;
            }
            if (!hasFormat || !dataBytes.HasValue || dataBytes == 0 || format != 1 || channels < 1
                || bits != 16 || sampleRate < 1 || blockAlign != (long)channels * 2
                || byteRate != (long)sampleRate * blockAlign || dataBytes.Value % blockAlign != 0)
                throw new InvalidDataException(invalid);
            var duration = (double)dataBytes.Value / byteRate;
            if (duration > HyperwhisperCoreMethods.AssemblyaiDictationMaxDurationSecs())
                throw new InvalidDataException("AssemblyAI Dictation supports recordings up to 120 seconds. Use a shorter recording or select another model.");
            return duration;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(invalid, error);
        }
    }
}
