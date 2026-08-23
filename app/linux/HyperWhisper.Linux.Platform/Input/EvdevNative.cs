using System.Buffers.Binary;

namespace HyperWhisper.Linux.Platform.Input;

internal readonly record struct EvdevEvent(ushort Type, ushort Code, int Value)
{
    public const ushort KeyType = 1;
    public bool IsKey => Type == KeyType && Value is >= 0 and <= 2;
}

internal static class EvdevParser
{
    public const int X64FrameSize = 24;

    public static bool TryParse(ReadOnlySpan<byte> frame, out EvdevEvent input)
    {
        if (frame.Length != X64FrameSize)
        {
            input = default;
            return false;
        }

        input = new EvdevEvent(
            BinaryPrimitives.ReadUInt16LittleEndian(frame[16..18]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[18..20]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[20..24]));
        return true;
    }
}

internal interface IEvdevSource : IAsyncDisposable
{
    string Id { get; }
    ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken);
}

internal interface IEvdevSourceFactory
{
    PlatformOpenResult OpenKeyboardSources();
}

internal sealed record PlatformOpenResult(
    IReadOnlyList<IEvdevSource> Sources,
    string? ErrorCode = null,
    string? ErrorMessage = null);

internal sealed class FileEvdevSource(string path) : IEvdevSource
{
    private readonly FileStream _stream = new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        EvdevParser.X64FrameSize,
        useAsync: true);

    public string Id { get; } = Path.GetFileName(path);

    public async ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await _stream.ReadAsync(frame[offset..], cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        // FileStream.DisposeAsync can wait for an outstanding character-device
        // read. Closing the descriptor synchronously lets desktop shutdown
        // continue while the reader observes cancellation or end-of-stream.
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class LinuxKeyboardSourceFactory : IEvdevSourceFactory
{
    public PlatformOpenResult OpenKeyboardSources()
    {
        try
        {
            var paths = Directory.Exists("/dev/input/by-id")
                ? Directory.GetFiles("/dev/input/by-id", "*-event-kbd")
                : [];
            if (paths.Length == 0 && Directory.Exists("/dev/input"))
            {
                paths = Directory.GetFiles("/dev/input", "event*");
            }

            var sources = new List<IEvdevSource>();
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                try
                {
                    sources.Add(new FileEvdevSource(path));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            return sources.Count == 0
                ? new PlatformOpenResult([], "evdev_unavailable", "No readable keyboard event devices were found.")
                : new PlatformOpenResult(sources);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PlatformOpenResult([], "evdev_enumeration_failed", "Keyboard event devices could not be enumerated.");
        }
    }
}
