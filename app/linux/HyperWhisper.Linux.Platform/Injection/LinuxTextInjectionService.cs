using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

public sealed record LinuxTextInjectionCapabilities(bool ClipboardAvailable, string ClipboardBackend,
    bool UInputAvailable, bool PreservesAllClipboardFormats, bool SecureFieldGuardAvailable,
    bool CapturedTargetFocusAvailable,
    ClipboardHistoryPrivacyCapability ClipboardHistoryPrivacy = ClipboardHistoryPrivacyCapability.Unsupported);

internal sealed record ClipboardSnapshot(IReadOnlyDictionary<string, byte[]> Formats);
internal sealed record CapturedTarget(string OpaqueId);
internal enum SecureFieldState { NotSecure, Secure, Unknown }
internal enum TargetFocusState { Ready, Lost, Changed, Unavailable }

internal interface ILinuxClipboardBackend
{
    LinuxTextInjectionCapabilities GetCapabilities();
    ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken cancellationToken);
    ValueTask<PlatformResult> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
    ValueTask<PlatformResult> SetTextAsync(string text, ClipboardHistoryPrivacyPolicy privacyPolicy,
        CancellationToken cancellationToken);
}
internal interface ISecureFieldGuard
{
    bool IsAvailable { get; }
    ValueTask<SecureFieldState> GetFocusedFieldStateAsync(CancellationToken cancellationToken);
}
internal interface ICapturedTargetService
{
    bool CanRestoreFocus { get; }
    PlatformResult<CapturedTarget?> Capture();
    ValueTask<TargetFocusState> ValidateAndFocusAsync(CapturedTarget target, CancellationToken cancellationToken);
}
internal interface IUInputPasteBackend { bool IsAvailable { get; } PlatformResult Paste(); }

public sealed class LinuxTextInjectionService : ITextInjectionService
{
    private readonly object _gate = new();
    private readonly ILinuxClipboardBackend _clipboard;
    private readonly IUInputPasteBackend _uinput;
    private readonly ISecureFieldGuard _secureFieldGuard;
    private readonly ICapturedTargetService _targets;
    private ClipboardSnapshot? _snapshot;
    private CapturedTarget? _capturedTarget;
    private CancellationTokenSource? _restoreCancellation;
    private int _clipboardHistoryPrivacyPolicy;
    private bool _disposed;

    public LinuxTextInjectionService() : this(new CommandClipboardBackend(), new UInputPasteBackend(),
        new AtSpiSecureFieldGuard(), CreateTargetService()) { }

    private static ICapturedTargetService CreateTargetService() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? new AtSpiCapturedTargetService() : new X11CapturedTargetService();

    internal LinuxTextInjectionService(ILinuxClipboardBackend clipboard, IUInputPasteBackend uinput,
        ISecureFieldGuard secureFieldGuard, ICapturedTargetService targets)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _uinput = uinput ?? throw new ArgumentNullException(nameof(uinput));
        _secureFieldGuard = secureFieldGuard ?? throw new ArgumentNullException(nameof(secureFieldGuard));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public bool IsCapturedTargetAvailable => _capturedTarget is not null && !_disposed;
    public ClipboardHistoryPrivacyCapability ClipboardHistoryPrivacyCapability =>
        _clipboard.GetCapabilities().ClipboardHistoryPrivacy;
    public void SetClipboardHistoryPrivacyPolicy(ClipboardHistoryPrivacyPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        Interlocked.Exchange(ref _clipboardHistoryPrivacyPolicy, (int)policy);
    }
    public LinuxTextInjectionCapabilities GetCapabilities()
    {
        var clipboard = _clipboard.GetCapabilities();
        return clipboard with { UInputAvailable = _uinput.IsAvailable,
            SecureFieldGuardAvailable = _secureFieldGuard.IsAvailable,
            CapturedTargetFocusAvailable = _targets.CanRestoreFocus };
    }
    public void CaptureTarget()
    {
        if (_disposed) return;
        var result = _targets.Capture();
        _capturedTarget = result.IsSuccess ? result.Value : null;
    }
    public void StartSession()
    {
        if (_disposed) return;
        CancelPendingClipboardRestore();
        try
        {
            var result = Task.Run(async () => await _clipboard.CaptureAsync(CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();
            _snapshot = result.IsSuccess ? result.Value : null;
        }
        catch { _snapshot = null; }
    }
    public void EndSession() => _capturedTarget = null;
    public void CancelPendingClipboardRestore()
    {
        CancellationTokenSource? value;
        lock (_gate) { value = _restoreCancellation; _restoreCancellation = null; }
        if (value is null) return;
        try { value.Cancel(); } catch { }
        value.Dispose();
    }
    public void ScheduleClipboardRestore(TimeSpan delay)
    {
        if (_disposed || _snapshot is null) return;
        CancelPendingClipboardRestore();
        var cancellation = new CancellationTokenSource();
        lock (_gate) _restoreCancellation = cancellation;
        _ = RestoreAfterDelayAsync(delay, cancellation);
    }
    public async ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default)
    {
        CancelPendingClipboardRestore();
        if (_snapshot is null) return PlatformResult.Success();
        var result = await TryRestoreAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) _snapshot = null;
        return result;
    }
    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _disposed ? ValueTask.FromResult(PlatformResult.Failure("injection_disposed", "The text injection service is disposed."))
            : TrySetTextAsync(text, cancellationToken);
    }
    public async ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed) return TextInjectionOutcome.Failed;
        var target = _capturedTarget;
        if (target is null)
            return (await TrySetTextAsync(text, cancellationToken).ConfigureAwait(false)).IsSuccess
                ? TextInjectionOutcome.CopiedToClipboard : TextInjectionOutcome.Failed;

        var focus = await TryFocusAsync(target, cancellationToken).ConfigureAwait(false);
        if (focus == TargetFocusState.Ready
            && await TrySecureStateAsync(cancellationToken).ConfigureAwait(false) == SecureFieldState.Secure)
            return TextInjectionOutcome.SecureFieldSkipped;

        var copied = await TrySetTextAsync(text, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return TextInjectionOutcome.Failed;
        if (focus != TargetFocusState.Ready) return TextInjectionOutcome.CopiedToClipboard;
        if (await TryFocusAsync(target, cancellationToken).ConfigureAwait(false) != TargetFocusState.Ready)
            return TextInjectionOutcome.CopiedToClipboard;
        try
        {
            var result = await Task.Run(_uinput.Paste, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? TextInjectionOutcome.Pasted : TextInjectionOutcome.CopiedToClipboard;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return TextInjectionOutcome.CopiedToClipboard; }
    }
    private async Task RestoreAfterDelayAsync(TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellation.Token);
            if (_snapshot is not null && (await TryRestoreAsync(_snapshot, cancellation.Token).ConfigureAwait(false)).IsSuccess)
                _snapshot = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_restoreCancellation, cancellation)) _restoreCancellation = null; }
            cancellation.Dispose();
        }
    }
    private async ValueTask<PlatformResult> TrySetTextAsync(string text, CancellationToken token)
    {
        try
        {
            var policy = (ClipboardHistoryPrivacyPolicy)Volatile.Read(ref _clipboardHistoryPrivacyPolicy);
            return await _clipboard.SetTextAsync(text, policy, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return PlatformResult.Failure("clipboard_failed", "The clipboard operation failed."); }
    }
    private async ValueTask<PlatformResult> TryRestoreAsync(ClipboardSnapshot snapshot, CancellationToken token)
    {
        try { return await _clipboard.RestoreAsync(snapshot, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return PlatformResult.Failure("clipboard_restore_failed", "The clipboard could not be restored."); }
    }
    private async ValueTask<TargetFocusState> TryFocusAsync(CapturedTarget target, CancellationToken token)
    {
        try { return await _targets.ValidateAndFocusAsync(target, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return TargetFocusState.Unavailable; }
    }
    private async ValueTask<SecureFieldState> TrySecureStateAsync(CancellationToken token)
    {
        try { return await _secureFieldGuard.GetFocusedFieldStateAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return SecureFieldState.Unknown; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingClipboardRestore();
        _snapshot = null; _capturedTarget = null;
        if (_clipboard is IDisposable disposable) disposable.Dispose();
        GC.SuppressFinalize(this);
    }
}
