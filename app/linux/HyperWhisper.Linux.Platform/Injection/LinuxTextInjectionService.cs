using System.Diagnostics;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

public sealed record LinuxTextInjectionCapabilities(
    bool ClipboardAvailable,
    string ClipboardBackend,
    bool UInputAvailable,
    bool PreservesAllClipboardFormats,
    bool SecureFieldGuardAvailable);

internal sealed record ClipboardSnapshot(string Text);

internal interface ILinuxClipboardBackend
{
    LinuxTextInjectionCapabilities GetCapabilities();
    ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken cancellationToken);
    ValueTask<PlatformResult> SetTextAsync(string text, CancellationToken cancellationToken);
}

internal interface IUInputPasteBackend
{
    bool IsAvailable { get; }
    PlatformResult Paste();
}

public sealed class LinuxTextInjectionService : ITextInjectionService
{
    private readonly object _gate = new();
    private readonly ILinuxClipboardBackend _clipboard;
    private readonly IUInputPasteBackend _uinput;
    private ClipboardSnapshot? _snapshot;
    private CancellationTokenSource? _restoreCancellation;
    private bool _targetCaptured;
    private bool _disposed;

    public LinuxTextInjectionService()
        : this(new CommandClipboardBackend(), new UInputPasteBackend())
    {
    }

    internal LinuxTextInjectionService(ILinuxClipboardBackend clipboard, IUInputPasteBackend uinput)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _uinput = uinput ?? throw new ArgumentNullException(nameof(uinput));
    }

    public bool IsCapturedTargetAvailable => _targetCaptured && !_disposed;

    public LinuxTextInjectionCapabilities GetCapabilities()
    {
        var clipboard = _clipboard.GetCapabilities();
        return clipboard with { UInputAvailable = _uinput.IsAvailable };
    }

    public void CaptureTarget() => _targetCaptured = !_disposed;

    public void StartSession()
    {
        if (_disposed) return;
        CancelPendingClipboardRestore();
        try
        {
            var captured = Task.Run(async () =>
                await _clipboard.CaptureAsync(CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();
            _snapshot = captured.IsSuccess ? captured.Value : null;
        }
        catch
        {
            _snapshot = null;
        }
    }

    public void EndSession() => _targetCaptured = false;

    public void CancelPendingClipboardRestore()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _restoreCancellation;
            _restoreCancellation = null;
        }
        if (cancellation is null) return;
        try { cancellation.Cancel(); } catch { }
        cancellation.Dispose();
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
        var snapshot = _snapshot;
        if (snapshot is null) return PlatformResult.Success();
        var restored = await TrySetClipboardTextAsync(snapshot.Text, cancellationToken);
        if (restored.IsSuccess) _snapshot = null;
        return restored;
    }

    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed)
            return ValueTask.FromResult(PlatformResult.Failure("injection_disposed", "The text injection service is disposed."));
        return TrySetClipboardTextAsync(text, cancellationToken);
    }

    public async ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed) return TextInjectionOutcome.Failed;
        var copied = await TrySetClipboardTextAsync(text, cancellationToken);
        if (copied.IsFailure) return TextInjectionOutcome.Failed;

        // Clipboard-first is the lossless boundary: any uinput failure leaves
        // the transcript available for a manual paste.
        PlatformResult pasted;
        try
        {
            pasted = await Task.Run(_uinput.Paste, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return TextInjectionOutcome.CopiedToClipboard;
        }
        return pasted.IsSuccess ? TextInjectionOutcome.Pasted : TextInjectionOutcome.CopiedToClipboard;
    }

    private async Task RestoreAfterDelayAsync(TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellation.Token);
            var snapshot = _snapshot;
            if (snapshot is not null)
            {
                var result = await TrySetClipboardTextAsync(snapshot.Text, cancellation.Token);
                if (result.IsSuccess) _snapshot = null;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_restoreCancellation, cancellation)) _restoreCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async ValueTask<PlatformResult> TrySetClipboardTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PlatformResult.Failure("clipboard_failed", "The clipboard operation failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingClipboardRestore();
        _snapshot = null;
        _targetCaptured = false;
        GC.SuppressFinalize(this);
    }
}

internal sealed class CommandClipboardBackend : ILinuxClipboardBackend
{
    private readonly string? _copyExecutable;
    private readonly string? _pasteExecutable;
    private readonly bool _wayland;

    public CommandClipboardBackend()
    {
        _wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        _copyExecutable = FindExecutable(_wayland ? "wl-copy" : "xclip");
        _pasteExecutable = FindExecutable(_wayland ? "wl-paste" : "xclip");
        if ((_copyExecutable is null || _pasteExecutable is null) && _wayland)
        {
            _wayland = false;
            _copyExecutable = FindExecutable("xclip");
            _pasteExecutable = _copyExecutable;
        }
    }

    public LinuxTextInjectionCapabilities GetCapabilities() => new(
        _copyExecutable is not null && _pasteExecutable is not null,
        _copyExecutable is null || _pasteExecutable is null ? "none" : _wayland ? "wayland-wl-clipboard" : "x11-xclip",
        false,
        false,
        false);

    public async ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken cancellationToken)
    {
        if (_pasteExecutable is null)
            return PlatformResult<ClipboardSnapshot?>.Failure("clipboard_unavailable", "No supported clipboard command is installed.");
        var result = await RunAsync(input: null, captureOutput: true, cancellationToken);
        return result.IsSuccess
            ? PlatformResult<ClipboardSnapshot?>.Success(new ClipboardSnapshot(result.Value!))
            : PlatformResult<ClipboardSnapshot?>.Failure(result.Error!.Code, result.Error.Message);
    }

    public async ValueTask<PlatformResult> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        if (_copyExecutable is null)
            return PlatformResult.Failure("clipboard_unavailable", "No supported clipboard command is installed.");
        var result = await RunAsync(text, captureOutput: false, cancellationToken);
        return result.IsSuccess ? PlatformResult.Success() : PlatformResult.Failure(result.Error!.Code, result.Error.Message);
    }

    private async Task<PlatformResult<string>> RunAsync(string? input, bool captureOutput, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(captureOutput ? _pasteExecutable! : _copyExecutable!)
            {
                UseShellExecute = false,
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = true,
            };
            if (_wayland)
            {
                if (!captureOutput)
                {
                    start.ArgumentList.Add("--type");
                    start.ArgumentList.Add("text/plain;charset=utf-8");
                }
            }
            else
            {
                start.ArgumentList.Add("-selection");
                start.ArgumentList.Add("clipboard");
                start.ArgumentList.Add(captureOutput ? "-out" : "-in");
            }

            using var process = Process.Start(start);
            if (process is null) return PlatformResult<string>.Failure("clipboard_start_failed", "The clipboard helper could not start.");
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            var output = captureOutput ? await process.StandardOutput.ReadToEndAsync(cancellationToken) : string.Empty;
            await process.WaitForExitAsync(cancellationToken);
            _ = await stderrTask;
            return process.ExitCode == 0
                ? PlatformResult<string>.Success(output)
                : PlatformResult<string>.Failure("clipboard_command_failed", "The clipboard helper reported an error.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return PlatformResult<string>.Failure("clipboard_command_failed", "The clipboard helper failed.");
        }
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
