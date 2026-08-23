using System.Buffers.Binary;

namespace HyperWhisper.LinuxSpike.Hotkeys;

internal enum EvdevKeyValue
{
    Released = 0,
    Pressed = 1,
    Repeated = 2,
}

internal readonly record struct EvdevInputEvent(ushort Type, ushort Code, int Value)
{
    public const ushort KeyType = 0x01;

    public bool IsKeyEvent => Type == KeyType && Value is >= 0 and <= 2;
}

internal static class EvdevEventParser
{
    // Linux input_event is 24 bytes on the supported x86_64 v1 target:
    // timeval (2 native 64-bit longs), type u16, code u16, value i32.
    public const int X64FrameSize = 24;

    public static bool TryParseX64(ReadOnlySpan<byte> frame, out EvdevInputEvent inputEvent)
    {
        if (frame.Length != X64FrameSize)
        {
            inputEvent = default;
            return false;
        }

        inputEvent = new EvdevInputEvent(
            BinaryPrimitives.ReadUInt16LittleEndian(frame[16..18]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[18..20]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[20..24]));
        return inputEvent.IsKeyEvent;
    }
}

internal interface IEvdevFrameSource : IAsyncDisposable
{
    ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken);
}

internal sealed class FileEvdevFrameSource : IEvdevFrameSource
{
    private readonly FileStream _stream;

    public FileEvdevFrameSource(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        _stream = new FileStream(
            devicePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: EvdevEventParser.X64FrameSize,
            useAsync: true);
    }

    public async ValueTask<bool> ReadFrameAsync(
        Memory<byte> frame,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var count = await _stream.ReadAsync(frame[offset..], cancellationToken);
            if (count == 0)
            {
                return false;
            }

            offset += count;
        }

        return true;
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
