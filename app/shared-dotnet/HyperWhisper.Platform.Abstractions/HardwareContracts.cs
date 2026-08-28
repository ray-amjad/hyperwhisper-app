namespace HyperWhisper.Platform.Abstractions;

public sealed record GpuInfo
{
    public string Name { get; init; } = "Unknown";
    public long DedicatedMemoryBytes { get; init; }
    public long SharedMemoryBytes { get; init; }
    public bool IsDiscrete { get; init; }
    public bool SupportsVulkan { get; init; }
    public bool SupportsCuda { get; init; }

    public bool IsApu => !IsDiscrete
        && DedicatedMemoryBytes < 8L * 1024 * 1024 * 1024
        && SharedMemoryBytes > DedicatedMemoryBytes;
}

public interface IGpuInfoProvider
{
    PlatformResult<GpuInfo?> GetBestGpu();
    void ClearCache();
}

public sealed record AudioInputDevice(
    string Id,
    string Name,
    bool IsDefault = false);

public interface IAudioInputDeviceService : IDisposable
{
    event EventHandler? DevicesChanged;
    PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices();
}

public sealed record AudioRecordingOptions(
    string DeviceId,
    int SampleRate = 16000,
    int BitsPerSample = 16,
    int ChannelCount = 1);

public interface IAudioRecorder : IDisposable
{
    event EventHandler<float>? AudioLevelChanged;

    bool IsRecording { get; }
    TimeSpan Duration { get; }
    PlatformResult Start(AudioRecordingOptions options);
    PlatformResult<string> Stop();
}

public interface IStreamingAudioCapture : IDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? AudioChunkAvailable;
    event EventHandler<float>? AudioLevelChanged;
    event EventHandler<PlatformError?>? CaptureStopped;

    bool IsCapturing { get; }
    TimeSpan Duration { get; }
    PlatformResult Start(AudioRecordingOptions options);
    void Stop();
}

public interface IMicrophoneVolumeService
{
    PlatformResult BoostIfNeeded(string deviceId);
    PlatformResult Restore();
    PlatformResult<float?> ReadLevel(string deviceId);
}

public interface IMicrophoneKeepWarmService : IDisposable
{
    void Configure(bool enabled, string? deviceId);
    void SuspendForRecording();
    void ResumeAfterRecording(string? deviceId);
}

public interface IAudioPlaybackService : IDisposable
{
    event EventHandler? PlaybackEnded;
    event EventHandler<TimeSpan>? PositionChanged;
    event EventHandler<TimeSpan>? DurationReady;
    event EventHandler<PlatformError>? PlaybackFailed;

    bool IsPlaying { get; }
    bool IsLoaded { get; }
    TimeSpan TotalDuration { get; }
    string? LoadedFilePath { get; }
    PlatformResult Load(string audioPath);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
}

public enum SoundEffect
{
    RecordingStarted,
    RecordingStopped
}

public interface ISoundEffectsService : IDisposable
{
    PlatformResult Play(SoundEffect effect);

    /// <summary>Configures playback gain for subsequent effects. Unsupported platforms may ignore it.</summary>
    PlatformResult ConfigureVolume(double volume) =>
        double.IsFinite(volume) && volume is >= 0 and <= 1
            ? PlatformResult.Success()
            : PlatformResult.Failure("sound_effects.invalid_volume", "Sound effect volume must be between zero and one.");
}

public enum AudioEnvironmentPolicy
{
    Unchanged,
    DuckOtherAudio,
    MuteOtherAudio
}

public interface IAudioEnvironmentSession : IAsyncDisposable
{
    ValueTask RestoreAsync(CancellationToken cancellationToken = default);
}

public interface IAudioEnvironmentService
{
    PlatformResult<IAudioEnvironmentSession> PrepareForRecording(
        AudioEnvironmentPolicy policy,
        TimeSpan restoreDelay);
}
