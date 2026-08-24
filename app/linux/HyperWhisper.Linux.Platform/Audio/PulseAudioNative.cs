using System.Runtime.InteropServices;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

public sealed record PulseAudioCapabilities(
    bool Available,
    string Backend,
    string Detail);

internal interface IPulseAudioApi
{
    PulseAudioCapabilities GetCapabilities();
    PlatformResult<IPulseAudioRecordSession> OpenRecord(AudioRecordingOptions options);
    PlatformResult<IPulseAudioPlaybackSession> OpenPlayback(WaveFormat format);
}

internal interface IPulseAudioRecordSession : IDisposable
{
    PlatformResult<int> Read(byte[] buffer);
}

internal interface IPulseAudioPlaybackSession : IDisposable
{
    PlatformResult Write(byte[] buffer, int count);
    PlatformResult Drain();
}

internal sealed record WaveFormat(int SampleRate, short BitsPerSample, short Channels)
{
    public int BytesPerSecond => SampleRate * Channels * BitsPerSample / 8;
    public short BlockAlign => (short)(Channels * BitsPerSample / 8);
}

internal sealed class PulseAudioApi : IPulseAudioApi
{
    private const string PulseSimpleLibrary = "libpulse-simple.so.0";
    private const string PulseLibrary = "libpulse.so.0";

    public PulseAudioCapabilities GetCapabilities()
    {
        var simple = NativeLibrary.TryLoad(PulseSimpleLibrary, out var simpleHandle);
        if (simple)
        {
            NativeLibrary.Free(simpleHandle);
        }

        var pulse = NativeLibrary.TryLoad(PulseLibrary, out var pulseHandle);
        if (pulse)
        {
            NativeLibrary.Free(pulseHandle);
        }

        return simple && pulse
            ? new PulseAudioCapabilities(true, "libpulse-simple", "pulse-or-pipewire-pulse")
            : new PulseAudioCapabilities(false, "none", "libpulse-simple-unavailable");
    }

    public PlatformResult<IPulseAudioRecordSession> OpenRecord(AudioRecordingOptions options) =>
        Open(options.DeviceId, new WaveFormat(options.SampleRate, (short)options.BitsPerSample, (short)options.ChannelCount), record: true)
            .Map<IPulseAudioRecordSession>(handle => new NativePulseSession(handle));

    public PlatformResult<IPulseAudioPlaybackSession> OpenPlayback(WaveFormat format) =>
        Open(null, format, record: false)
            .Map<IPulseAudioPlaybackSession>(handle => new NativePulseSession(handle));

    private static PlatformResult<IntPtr> Open(string? device, WaveFormat format, bool record)
    {
        if (!OperatingSystem.IsLinux())
        {
            return PlatformResult<IntPtr>.Failure("pulse_platform_unsupported", "PulseAudio requires Linux.");
        }

        var sampleSpec = new PulseSampleSpec
        {
            Format = PulseSampleFormat.Signed16LittleEndian,
            Rate = checked((uint)format.SampleRate),
            Channels = checked((byte)format.Channels),
        };
        var handle = PulseNative.SimpleNew(
            null,
            "HyperWhisper",
            record ? PulseStreamDirection.Record : PulseStreamDirection.Playback,
            string.IsNullOrWhiteSpace(device) ? null : device,
            record ? "Recording" : "Playback",
            ref sampleSpec,
            IntPtr.Zero,
            IntPtr.Zero,
            out var error);
        return handle == IntPtr.Zero
            ? PlatformResult<IntPtr>.Failure("pulse_open_failed", PulseNative.ErrorMessage(error))
            : PlatformResult<IntPtr>.Success(handle);
    }

    private sealed class NativePulseSession(IntPtr handle) : IPulseAudioRecordSession, IPulseAudioPlaybackSession
    {
        private IntPtr _handle = handle;

        public PlatformResult<int> Read(byte[] buffer)
        {
            if (_handle == IntPtr.Zero)
            {
                return PlatformResult<int>.Failure("pulse_session_closed", "The PulseAudio session is closed.");
            }

            var result = PulseNative.SimpleRead(_handle, buffer, (nuint)buffer.Length, out var error);
            return result < 0
                ? PlatformResult<int>.Failure("pulse_read_failed", PulseNative.ErrorMessage(error))
                : PlatformResult<int>.Success(buffer.Length);
        }

        public PlatformResult Write(byte[] buffer, int count)
        {
            if (_handle == IntPtr.Zero)
            {
                return PlatformResult.Failure("pulse_session_closed", "The PulseAudio session is closed.");
            }

            var result = PulseNative.SimpleWrite(_handle, buffer, (nuint)count, out var error);
            return result < 0
                ? PlatformResult.Failure("pulse_write_failed", PulseNative.ErrorMessage(error))
                : PlatformResult.Success();
        }

        public PlatformResult Drain()
        {
            if (_handle == IntPtr.Zero)
            {
                return PlatformResult.Success();
            }

            var result = PulseNative.SimpleDrain(_handle, out var error);
            return result < 0
                ? PlatformResult.Failure("pulse_drain_failed", PulseNative.ErrorMessage(error))
                : PlatformResult.Success();
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (current != IntPtr.Zero)
            {
                PulseNative.SimpleFree(current);
            }
        }
    }
}

internal static class PlatformResultMap
{
    public static PlatformResult<TOut> Map<TOut>(this PlatformResult<IntPtr> result, Func<IntPtr, TOut> map) =>
        result.IsSuccess
            ? PlatformResult<TOut>.Success(map(result.Value))
            : PlatformResult<TOut>.Failure(result.Error!.Code, result.Error.Message);
}

internal enum PulseStreamDirection
{
    Playback = 1,
    Record = 2,
}

internal enum PulseSampleFormat
{
    Signed16LittleEndian = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PulseSampleSpec
{
    public PulseSampleFormat Format;
    public uint Rate;
    public byte Channels;
}

internal static class PulseNative
{
    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_new", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SimpleNew(
        string? server,
        string applicationName,
        PulseStreamDirection direction,
        string? device,
        string streamName,
        ref PulseSampleSpec sampleSpec,
        IntPtr channelMap,
        IntPtr bufferAttributes,
        out int error);

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_read", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SimpleRead(IntPtr stream, byte[] data, nuint bytes, out int error);

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_write", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SimpleWrite(IntPtr stream, byte[] data, nuint bytes, out int error);

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_drain", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SimpleDrain(IntPtr stream, out int error);

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SimpleFree(IntPtr stream);

    [DllImport("libpulse.so.0", EntryPoint = "pa_strerror", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr StrError(int error);

    internal static string ErrorMessage(int error) =>
        Marshal.PtrToStringAnsi(StrError(error)) is { Length: > 0 } message
            ? $"PulseAudio: {message}"
            : "PulseAudio operation failed.";
}
