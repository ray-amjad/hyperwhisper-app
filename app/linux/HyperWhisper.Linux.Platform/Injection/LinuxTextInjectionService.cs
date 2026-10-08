using System.Text;
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
    // #1514: the UTF-8 bytes of the last transcript this service wrote, once a restore of
    // _snapshot was scheduled for it. It means "this transcript is on the clipboard and the
    // user's content is due back". While the clipboard still holds exactly this transcript,
    // StartSession keeps _snapshot instead of capturing the transcript. Guarded by _gate.
    private byte[]? _scheduledTranscript;
    // The last transcript written with no restore scheduled for it yet. ScheduleClipboardRestore
    // moves it to _scheduledTranscript; a session that starts while it is still set treats that
    // transcript as left on the clipboard on purpose (restore off) and snapshots it afresh.
    private byte[]? _unscheduledTranscript;
    // Bumped by every StartSession. A restore records it with the snapshot it restores and clears
    // state only while it is unchanged: a chained session keeps the SAME snapshot object, so a
    // reference check cannot tell its snapshot from the one an in-flight restore read. Guarded by _gate.
    private long _sessionGeneration;
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
    /// <summary>
    /// Cancels a pending restore and snapshots the clipboard for this session.
    /// CHAINED DICTATIONS (#1514, the Linux twin of #1496): when the last transcript this service
    /// wrote had a restore scheduled, and the clipboard still holds exactly that transcript, the
    /// clipboard is the app's, not the user's. Capturing it would make this session's restore
    /// write the old transcript back and lose the user's clipboard, so the earlier snapshot is
    /// kept. Linux has no clipboard change counter, so "nothing wrote since" is read from the
    /// content: a user copy (other text, or other formats such as text/html) differs and is
    /// snapshotted afresh, as before.
    /// </summary>
    public void StartSession()
    {
        if (_disposed) return;
        // Before the cancel: a restore already inside RestoreAsync may still finish, and must not
        // clear the state this session is about to keep or replace.
        lock (_gate) _sessionGeneration++;
        CancelPendingClipboardRestore();
        PlatformResult<ClipboardSnapshot?>? result;
        try
        {
            result = Task.Run(async () => await _clipboard.CaptureAsync(CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();
        }
        catch { result = null; }
        lock (_gate)
        {
            _unscheduledTranscript = null;
            if (_snapshot is not null && _scheduledTranscript is not null && result is { IsSuccess: true }
                && HoldsOnlyTranscript(result.Value, _scheduledTranscript))
                return;
            _scheduledTranscript = null;
            _snapshot = result is { IsSuccess: true } ? result.Value : null;
        }
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
        lock (_gate)
        {
            _restoreCancellation = cancellation;
            // The transcript this session wrote now has a restore due, so a session started
            // before it runs keeps the snapshot. With no write this session, the earlier mark stands.
            if (_unscheduledTranscript is not null)
            {
                _scheduledTranscript = _unscheduledTranscript;
                _unscheduledTranscript = null;
            }
        }
        _ = RestoreAfterDelayAsync(delay, cancellation);
    }
    public async ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default)
    {
        CancelPendingClipboardRestore();
        var (snapshot, generation) = ReadSnapshot();
        if (snapshot is null) return PlatformResult.Success();
        var result = await TryRestoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) ClearRestoredSnapshot(generation);
        return result;
    }
    public async ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed) return PlatformResult.Failure("injection_disposed", "The text injection service is disposed.");
        var result = await TrySetTextAsync(text, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) MarkTranscriptWritten(text);
        return result;
    }
    public async ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed) return TextInjectionOutcome.Failed;
        var target = _capturedTarget;
        if (target is null)
        {
            if ((await TrySetTextAsync(text, cancellationToken).ConfigureAwait(false)).IsFailure)
                return TextInjectionOutcome.Failed;
            MarkTranscriptWritten(text);
            return TextInjectionOutcome.CopiedToClipboard;
        }

        var focus = await TryFocusAsync(target, cancellationToken).ConfigureAwait(false);
        if (focus == TargetFocusState.Ready
            && await TrySecureStateAsync(cancellationToken).ConfigureAwait(false) == SecureFieldState.Secure)
            return TextInjectionOutcome.SecureFieldSkipped;

        var copied = await TrySetTextAsync(text, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return TextInjectionOutcome.Failed;
        MarkTranscriptWritten(text);
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
            var (snapshot, generation) = ReadSnapshot();
            if (snapshot is not null && (await TryRestoreAsync(snapshot, cancellation.Token).ConfigureAwait(false)).IsSuccess)
                ClearRestoredSnapshot(generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_restoreCancellation, cancellation)) _restoreCancellation = null; }
            cancellation.Dispose();
        }
    }
    /// <summary>
    /// Records that the clipboard now holds a transcript this service wrote. It drops the armed
    /// mark: only a restore scheduled for this write (ScheduleClipboardRestore) arms it again, so
    /// a transcript left with no restore (restore off) is the clipboard's content next session.
    /// </summary>
    private void MarkTranscriptWritten(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            _scheduledTranscript = null;
            _unscheduledTranscript = bytes;
        }
    }

    private (ClipboardSnapshot? Snapshot, long Generation) ReadSnapshot()
    {
        lock (_gate) return (_snapshot, _sessionGeneration);
    }

    /// <summary>The user's content is back on the clipboard: no snapshot or mark is current.</summary>
    private void ClearRestoredSnapshot(long generation)
    {
        lock (_gate)
        {
            // A session that started meanwhile owns the state now, even when it kept the very
            // snapshot this restore wrote (a chain): it schedules its own restore of it.
            if (_sessionGeneration != generation) return;
            _snapshot = null;
            _scheduledTranscript = null;
            _unscheduledTranscript = null;
        }
    }

    // The UTF-8 plain-text targets this service's transcript writes publish (CommandClipboardBackend
    // and the native owner), compared byte for byte; the legacy X11 aliases may be re-encoded by the
    // owner, so they are allowed without a byte check; the privacy hint is ours too.
    private static readonly string[] Utf8TextFormats = ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING"];
    private static readonly string[] LegacyTextFormats = ["STRING", "TEXT", "COMPOUND_TEXT"];
    private const string PrivacyHintFormat = "x-kde-passwordManagerHint";

    /// <summary>
    /// Whether <paramref name="captured"/> is exactly a transcript write of <paramref name="transcript"/>:
    /// at least one UTF-8 text target equal to it, every UTF-8 text target equal to it, and no
    /// format a transcript write does not publish. Anything else (another text, an HTML or image
    /// copy, an empty clipboard) is someone else's content.
    /// </summary>
    internal static bool HoldsOnlyTranscript(ClipboardSnapshot? captured, byte[] transcript)
    {
        if (captured is null || captured.Formats.Count == 0) return false;
        var matched = false;
        foreach (var (format, value) in captured.Formats)
        {
            if (Utf8TextFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                if (!value.AsSpan().SequenceEqual(transcript)) return false;
                matched = true;
            }
            else if (!LegacyTextFormats.Contains(format, StringComparer.Ordinal)
                && !string.Equals(format, PrivacyHintFormat, StringComparison.Ordinal))
                return false;
        }
        return matched;
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
        lock (_gate) { _snapshot = null; _scheduledTranscript = null; _unscheduledTranscript = null; }
        _capturedTarget = null;
        if (_clipboard is IDisposable disposable) disposable.Dispose();
        GC.SuppressFinalize(this);
    }
}
