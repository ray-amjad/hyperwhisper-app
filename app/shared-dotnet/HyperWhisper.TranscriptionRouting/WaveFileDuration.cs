using System.Buffers.Binary;

namespace HyperWhisper.TranscriptionRouting;

/// <summary>
/// Reads the audio length of a RIFF/WAVE file from its header chunks only (#1569).
/// It sizes a timeout, so it is best-effort: any file it cannot read gives null and
/// the caller falls back to its fixed floor. Audio bytes are skipped, never read.
/// </summary>
internal static class WaveFileDuration
{
    private const int MaximumChunks = 256;

    /// <summary>
    /// The duration in seconds (<c>data</c> bytes / the <c>fmt </c> byte rate), or null
    /// for a missing file, a non-WAV file, a missing <c>fmt </c> or <c>data</c> chunk,
    /// a zero byte rate, or any I/O failure.
    /// </summary>
    /// <remarks>
    /// Chunks are walked, not read at fixed offsets, so a <c>LIST</c> or <c>JUNK</c>
    /// chunk before the audio is fine. A <c>data</c> size of zero or one that runs past
    /// the end of the file (a crash-recovered recording, or a streamed header that never
    /// got patched) is taken as "to the end of the file", as the shared recording reader
    /// <c>PcmWaveHeader</c> does.
    /// </remarks>
    internal static double? TryReadSeconds(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            return TryReadSeconds(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static double? TryReadSeconds(Stream stream)
    {
        var length = stream.Length;
        Span<byte> header = stackalloc byte[12];
        if (length < 12 || stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) != 12
            || !header[..4].SequenceEqual("RIFF"u8)
            || !header[8..].SequenceEqual("WAVE"u8))
            return null;

        uint? bytesPerSecond = null;
        long? dataBytes = null;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        for (var count = 0; count < MaximumChunks && stream.Position + 8 <= length; count++)
        {
            if (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) != 8) break;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var remaining = length - stream.Position;
            if (chunk[..4].SequenceEqual("data"u8))
            {
                dataBytes = size == 0 || size > remaining ? remaining : size;
                if (bytesPerSecond is not null) break;
                if (dataBytes == remaining) break;
                stream.Seek(size, SeekOrigin.Current);
            }
            else if (size > remaining) break;
            else if (chunk[..4].SequenceEqual("fmt "u8) && size >= 16)
            {
                if (stream.ReadAtLeast(format, 16, throwOnEndOfStream: false) != 16) break;
                bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                if (dataBytes is not null) break;
                stream.Seek(size - 16, SeekOrigin.Current);
            }
            else stream.Seek(size, SeekOrigin.Current);
            if ((size & 1) != 0 && stream.Position < length) stream.Seek(1, SeekOrigin.Current);
        }

        return bytesPerSecond is > 0 && dataBytes is > 0
            ? (double)dataBytes.Value / bytesPerSecond.Value
            : null;
    }
}
