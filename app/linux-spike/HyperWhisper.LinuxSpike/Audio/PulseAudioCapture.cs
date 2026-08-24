using System.Runtime.InteropServices;

namespace HyperWhisper.LinuxSpike.Audio;

public sealed record AudioCaptureRequest(
    string? DeviceName,
    int SampleRate = 16_000,
    int Channels = 1);

public sealed record AudioCaptureCapability(bool Available, string Detail);

public interface INativeLibraryProbe
{
    bool CanLoad(string libraryName);
}

public interface IPulseAudioCaptureBackend
{
    Task CaptureAsync(
        AudioCaptureRequest request,
        Stream destination,
        CancellationToken cancellationToken);
}

public sealed class PulseAudioCaptureService
{
    public const string PulseLibrary = "libpulse.so.0";

    private readonly INativeLibraryProbe _libraryProbe;
    private readonly IPulseAudioCaptureBackend _backend;

    public PulseAudioCaptureService(
        INativeLibraryProbe libraryProbe,
        IPulseAudioCaptureBackend backend)
    {
        _libraryProbe = libraryProbe ?? throw new ArgumentNullException(nameof(libraryProbe));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public AudioCaptureCapability GetCapability() => _libraryProbe.CanLoad(PulseLibrary)
        ? new AudioCaptureCapability(true, "libpulse-ready")
        : new AudioCaptureCapability(false, "libpulse-missing");

    public Task CaptureAsync(
        AudioCaptureRequest request,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (request.SampleRate <= 0 || request.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Audio format values must be positive.");
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        if (!GetCapability().Available)
        {
            throw new InvalidOperationException("PulseAudio compatibility library libpulse.so.0 is unavailable.");
        }

        return _backend.CaptureAsync(request, destination, cancellationToken);
    }
}

public sealed class NativeLibraryProbe : INativeLibraryProbe
{
    public bool CanLoad(string libraryName)
    {
        if (!NativeLibrary.TryLoad(libraryName, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }
}
