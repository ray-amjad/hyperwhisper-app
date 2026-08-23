namespace HyperWhisper.Platform.Abstractions;

public enum TextInjectionOutcome
{
    Pasted,
    CopiedToClipboard,
    SecureFieldSkipped,
    Failed
}

/// <summary>
/// The sole capability through which transcribed text enters another application.
/// Clipboard capture and restoration remain private implementation details.
/// </summary>
public interface ITextInjectionService : IDisposable
{
    void CaptureTarget();
    bool IsCapturedTargetAvailable { get; }
    void StartSession();
    void EndSession();
    void CancelPendingClipboardRestore();
    void ScheduleClipboardRestore(TimeSpan delay);
    ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(
        CancellationToken cancellationToken = default);
    ValueTask<PlatformResult> CopyToClipboardAsync(
        string text,
        CancellationToken cancellationToken = default);
    ValueTask<TextInjectionOutcome> InjectTranscriptAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public enum InsertionCursorContext
{
    Unknown,
    StartOfSentence,
    MidSentence,
}

/// <summary>
/// Optional, privacy-minimal caret classification for insertion formatting.
/// Implementations must not return, retain, or log surrounding application text.
/// </summary>
public interface IInsertionContextProvider
{
    ValueTask<InsertionCursorContext> GetCursorContextAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ApplicationContextSnapshot
{
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string? BrowserTabTitle { get; init; }
    public string? BrowserHost { get; init; }
    public string? FocusedElementType { get; init; }
    public string? FocusedContent { get; init; }
    public string? TextFormat { get; init; }
    public string AppType { get; init; } = "other";
    public string AppTypeConfidence { get; init; } = "unknown";
    public string AppTypeSource { get; init; } = "default";
    public string? ScreenOcrText { get; init; }
}

public interface IApplicationContextProvider : IDisposable
{
    ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// User-triggered screen capture and OCR. Implementations must not retain or log
/// captured pixels or recognized text.
/// </summary>
public interface IScreenOcrService
{
    ValueTask<PlatformResult<string?>> CaptureAndRecognizeAsync(
        int maxCharacters = 2000,
        CancellationToken cancellationToken = default);
}

public interface ISingleInstanceCoordinator : IDisposable
{
    event EventHandler? ActivationRequested;

    PlatformResult<bool> TryAcquire();
    PlatformResult SignalExistingInstance();
    void Release();
}

public interface IAutostartService
{
    PlatformResult<bool> IsEnabled();
    PlatformResult Enable();
    PlatformResult Disable();
}

/// <summary>Framework-neutral dispatch onto the desktop UI thread.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    ValueTask InvokeAsync(
        Func<ValueTask> action,
        CancellationToken cancellationToken = default);
}
