namespace HyperWhisper.Linux.Overlay;

public enum LinuxRecordingOverlayState
{
    Hidden,
    Recording,
    Transcribing,
    Error,
    ModeChanged,
    Cancelled,
}

public enum LinuxRecordingOverlayError
{
    MicrophoneUnavailable,
    RecordingFailed,
    TranscriptionFailed,
    NoSpeechDetected,
    ProviderUnavailable,
    PermissionDenied,
    Unknown,
}

/// <summary>A bounded display-only mode label, never transcript or audio content.</summary>
public readonly record struct LinuxOverlayModeLabel
{
    public const int MaximumCharacters = 64;
    private LinuxOverlayModeLabel(string value) => Value = value;
    public string Value { get; }

    public static LinuxOverlayModeLabel Create(string? value)
    {
        var sanitized = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(MaximumCharacters)
            .ToArray()).Trim();
        return new(sanitized.Length == 0 ? "Default" : sanitized);
    }

    public override string ToString() => Value;
}

public sealed record LinuxRecordingOverlaySnapshot(
    LinuxRecordingOverlayState State,
    bool IsVisible,
    string StatusText,
    string ModeText,
    string DurationText);

internal interface ILinuxRecordingOverlaySurface : IDisposable
{
    void ShowBestEffort();
    void HideBestEffort();
}

internal interface ILinuxOverlayDispatcher
{
    void Post(Action action);
}

internal interface ILinuxOverlayDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemLinuxOverlayDelay : ILinuxOverlayDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
