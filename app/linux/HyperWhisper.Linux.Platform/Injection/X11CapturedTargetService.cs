using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed class X11CapturedTargetService : ICapturedTargetService
{
    private readonly string? _xprop = CommandClipboardBackend.FindExecutable("xprop");
    private readonly string? _wmctrl = CommandClipboardBackend.FindExecutable("wmctrl");
    private bool IsX11Session
    {
        get
        {
            var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
                && !string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase);
        }
    }
    public bool CanRestoreFocus => IsX11Session && _xprop is not null && _wmctrl is not null;

    public PlatformResult<CapturedTarget?> Capture()
    {
        if (!IsX11Session || _xprop is null)
            return PlatformResult<CapturedTarget?>.Failure("target_capture_unavailable", "X11 target capture is unavailable.");
        var active = ReadActiveWindow(CancellationToken.None);
        return active is null ? PlatformResult<CapturedTarget?>.Failure("target_capture_failed", "The active X11 window could not be captured.")
            : PlatformResult<CapturedTarget?>.Success(new CapturedTarget(active));
    }

    public async ValueTask<TargetFocusState> ValidateAndFocusAsync(CapturedTarget target, CancellationToken token)
    {
        if (!IsX11Session || _xprop is null) return TargetFocusState.Unavailable;
        if (!await ExistsAsync(target.OpaqueId, token).ConfigureAwait(false)) return TargetFocusState.Lost;
        if (ReadActiveWindow(token) == target.OpaqueId) return TargetFocusState.Ready;
        if (_wmctrl is null) return TargetFocusState.Changed;
        var focused = await RunAsync(_wmctrl, ["-ia", target.OpaqueId], token).ConfigureAwait(false);
        return focused && ReadActiveWindow(token) == target.OpaqueId ? TargetFocusState.Ready : TargetFocusState.Changed;
    }

    private string? ReadActiveWindow(CancellationToken token)
    {
        var result = RunForOutput(_xprop!, ["-root", "_NET_ACTIVE_WINDOW"], token);
        if (result.ExitCode != 0) return null;
        var marker = result.Output.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        return result.Output[marker..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
    }

    private async Task<bool> ExistsAsync(string id, CancellationToken token) =>
        await RunAsync(_xprop!, ["-id", id, "WM_CLASS"], token).ConfigureAwait(false);

    private static async Task<bool> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token) =>
        await Task.Run(() => RunForOutput(executable, args, token).ExitCode == 0, token).ConfigureAwait(false);

    private static (int ExitCode, string Output) RunForOutput(string executable, IReadOnlyList<string> args, CancellationToken token)
    {
        try
        {
            var result = ExternalProcessRunner.RunAsync(executable, args, null, token).GetAwaiter().GetResult();
            return (result.ExitCode, Encoding.UTF8.GetString(result.Output));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return (-1, string.Empty); }
    }
}
