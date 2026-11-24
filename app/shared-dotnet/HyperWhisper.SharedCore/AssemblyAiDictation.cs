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
        ValidateWave(parameters.audioPath, out var unpatchedData);
        // An unpatched header is sent as a corrected temp copy; the user's file is never edited.
        var repairedDirectory = unpatchedData is null ? null : WriteRepairedCopy(parameters.audioPath, unpatchedData.Value);
        try
        {
            var audioPath = repairedDirectory is null ? parameters.audioPath : Path.Combine(repairedDirectory, Path.GetFileName(parameters.audioPath));
            // Build before starting I/O. The core rejects Auto/unsupported languages.
            uniffi.hyperwhisper_core.HttpRequest request;
            try
            {
                request = HyperwhisperCoreMethods.AssemblyaiBuildDictationRequest(parameters with { audioPath = audioPath, audioMime = "audio/wav" });
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
        finally
        {
            if (repairedDirectory is not null)
            {
                try { Directory.Delete(repairedDirectory, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Where an unpatched <c>data</c> chunk starts and how many whole frames it holds.</summary>
    internal readonly record struct UnpatchedData(long Offset, long Length);

    // Copies the RIFF chunks before the audio plus the whole frames of the audio, then writes the
    // real RIFF and data lengths. The copy keeps the original file name, because the multipart
    // upload names the audio part after it.
    private static string WriteRepairedCopy(string path, UnpatchedData data)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-assemblyai-{Guid.NewGuid():N}");
        try
        {
            // The copy is the user's audio: owner-only on Unix, where the temp folder is shared.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var source = File.OpenRead(path))
            using (var target = new FileStream(Path.Combine(directory, Path.GetFileName(path)), FileMode.CreateNew, FileAccess.Write))
            {
                var end = data.Offset + data.Length;
                var buffer = new byte[81920];
                while (target.Position < end)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, end - target.Position));
                    if (read == 0) throw new EndOfStreamException();
                    target.Write(buffer, 0, read);
                }
                Span<byte> length = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)(end - 8)));
                target.Position = 4;
                target.Write(length);
                BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)data.Length));
                target.Position = data.Offset - 4;
                target.Write(length);
            }
            return directory;
        }
        catch (Exception error)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            // The recording passed validation, so a failure here is the temp folder (full, read-only)
            // or the file changing under us: say so, rather than calling the recording invalid.
            if (error is IOException or UnauthorizedAccessException)
                throw new InvalidDataException("AssemblyAI Dictation could not prepare this recording for upload. Check there is free disk space and try again.", error);
            throw;
        }
    }

    // Read actual RIFF chunks, not MIME labels, caller duration hints or file-size estimates.
    // Both desktop recorders already emit PCM16 WAV. Unsupported imports fail explicitly.
    // A data length that was never patched (#2654: a crash-interrupted recorder writes RIFF 36
    // and data 0; a piped ffmpeg writes 0xFFFFFFFF for both) is read to the end of the file, as
    // PcmWaveHeader and WaveFileDuration do, and unpatchedData says where, so the caller can
    // upload a corrected copy. Unpatched means zero, 0xFFFFFFFF (the streaming placeholder) or
    // less than one whole frame, or past the end of the file when the RIFF size is wrong too.
    // Any other past-the-end length in a header whose RIFF size matches the file is a corrupt
    // field, not a placeholder, and stays refused.
    internal static double ValidateWave(string path, out UnpatchedData? unpatchedData)
    {
        unpatchedData = null;
        const string invalid = "AssemblyAI Dictation requires a valid PCM16 WAV. Convert this recording to WAV or select another model.";
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                throw new InvalidDataException(invalid);
            var riffMatches = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8 == stream.Length;
            long dataOffset = 0;
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
                if (chunk[..4].SequenceEqual("data"u8) && !dataBytes.HasValue
                    && (size is 0 or uint.MaxValue || (hasFormat && size < blockAlign)
                        || (!riffMatches && stream.Position + size > stream.Length)))
                {
                    // Unpatched: the audio runs to the end of the file, so this is the last chunk.
                    dataOffset = stream.Position;
                    dataBytes = (uint)Math.Min(stream.Length - stream.Position, uint.MaxValue);
                    break;
                }
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
            if (!riffMatches && dataOffset == 0) throw new InvalidDataException(invalid);
            if (dataOffset != 0 && blockAlign != 0) dataBytes -= dataBytes.GetValueOrDefault() % blockAlign; // drop a torn last frame
            if (!hasFormat || !dataBytes.HasValue || dataBytes == 0 || format != 1 || channels < 1
                || bits != 16 || sampleRate < 1 || blockAlign != (long)channels * 2
                || byteRate != (long)sampleRate * blockAlign || dataBytes.Value % blockAlign != 0)
                throw new InvalidDataException(invalid);
            var duration = (double)dataBytes.Value / byteRate;
            if (duration > HyperwhisperCoreMethods.AssemblyaiDictationMaxDurationSecs())
                throw new InvalidDataException("AssemblyAI Dictation supports recordings up to 120 seconds. Use a shorter recording or select another model.");
            if (dataOffset != 0) unpatchedData = new UnpatchedData(dataOffset, dataBytes.Value);
            return duration;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(invalid, error);
        }
    }
}
