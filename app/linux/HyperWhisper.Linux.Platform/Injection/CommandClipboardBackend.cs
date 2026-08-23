using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed class CommandClipboardBackend : ILinuxClipboardBackend, IDisposable
{
    private const int MaximumSnapshotBytes = 32 * 1024 * 1024;
    private const int MaximumTranscriptBytes = 8 * 1024 * 1024;
    private const string PrivacyHintMimeType = "x-kde-passwordManagerHint";
    private readonly string? _copy;
    private readonly string? _paste;
    private readonly bool _wayland;
    private readonly INativeClipboardOwner? _nativeOwner;

    public CommandClipboardBackend()
    {
        _wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        _copy = FindExecutable(_wayland ? "wl-copy" : "xclip");
        _paste = FindExecutable(_wayland ? "wl-paste" : "xclip");
        if ((_copy is null || _paste is null) && _wayland)
        {
            _wayland = false;
            _copy = FindExecutable("xclip");
            _paste = _copy;
        }
        if (_wayland)
        {
            // Stable Avalonia is hosted by XWayland. Owning CLIPBOARD through
            // that same X server lets the compositor bridge one multi-target
            // selection to native Wayland clients on GNOME and KDE.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
                _nativeOwner = new NativeX11ClipboardOwner(allowXWayland: true);
        }
        else _nativeOwner = new NativeX11ClipboardOwner();
    }

    internal CommandClipboardBackend(string copy, string paste, bool wayland, INativeClipboardOwner? nativeOwner)
    {
        _copy = copy;
        _paste = paste;
        _wayland = wayland;
        _nativeOwner = nativeOwner;
    }

    public LinuxTextInjectionCapabilities GetCapabilities() => new(
        _copy is not null && _paste is not null,
        _copy is null || _paste is null ? "none" : _wayland
            ? _nativeOwner?.IsAvailable == true ? "wayland-wl-clipboard+xwayland-owner" : "wayland-wl-clipboard"
            : "x11-xclip",
        false,
        _nativeOwner?.IsAvailable == true,
        false,
        false,
        _nativeOwner?.IsAvailable == true
            ? ClipboardHistoryPrivacyCapability.BestEffortAvailable
            : ClipboardHistoryPrivacyCapability.Unsupported);

    public async ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken token)
    {
        if (_paste is null) return PlatformResult<ClipboardSnapshot?>.Failure("clipboard_unavailable", "No supported clipboard helper is installed.");
        var listed = await RunAsync(_paste, ListArguments(), null, token, 64 * 1024).ConfigureAwait(false);
        if (listed.IsFailure) return PlatformResult<ClipboardSnapshot?>.Failure(listed.Error!.Code, listed.Error.Message);
        var formats = ParseFormats(Encoding.UTF8.GetString(listed.Value!));
        if (formats.Count > 64)
            return PlatformResult<ClipboardSnapshot?>.Failure("clipboard_snapshot_invalid", "The clipboard advertises too many formats.");
        var captured = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var total = 0;
        foreach (var format in formats)
        {
            var value = await RunAsync(_paste, ReadArguments(format), null, token,
                MaximumSnapshotBytes - total).ConfigureAwait(false);
            if (value.IsFailure)
            {
                if (value.Error!.Code == "clipboard_snapshot_too_large")
                    return PlatformResult<ClipboardSnapshot?>.Failure(value.Error.Code, value.Error.Message);
                return PlatformResult<ClipboardSnapshot?>.Failure("clipboard_capture_incomplete",
                    "A clipboard MIME payload could not be captured; the clipboard was left unchanged.");
            }
            total = checked(total + value.Value!.Length);
            if (total > MaximumSnapshotBytes)
                return PlatformResult<ClipboardSnapshot?>.Failure("clipboard_snapshot_too_large", "The clipboard exceeds the private snapshot limit.");
            captured[format] = value.Value;
        }
        return PlatformResult<ClipboardSnapshot?>.Success(captured.Count == 0 ? null : new ClipboardSnapshot(captured));
    }

    public async ValueTask<PlatformResult> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken token)
    {
        if (_copy is null) return PlatformResult.Failure("clipboard_unavailable", "No supported clipboard helper is installed.");
        if (snapshot.Formats.Count == 0) return PlatformResult.Success();
        var validation = ValidateSnapshot(snapshot);
        if (validation.IsFailure) return validation;
        if (_nativeOwner?.IsAvailable == true)
            return await _nativeOwner.OwnAsync(snapshot, token).ConfigureAwait(false);
        if (snapshot.Formats.Count > 1)
            return PlatformResult.Failure("clipboard_restore_partial",
                "No multi-format clipboard owner is available; the clipboard was left unchanged.");
        var preferred = SelectPreferred(snapshot.Formats);
        var restored = await RunAsync(_copy, WriteArguments(preferred.Key), preferred.Value, token).ConfigureAwait(false);
        if (restored.IsFailure) return PlatformResult.Failure(restored.Error!.Code, restored.Error.Message);
        return PlatformResult.Success();
    }

    public async ValueTask<PlatformResult> SetTextAsync(string text, ClipboardHistoryPrivacyPolicy privacyPolicy,
        CancellationToken token)
    {
        if (_copy is null) return PlatformResult.Failure("clipboard_unavailable", "No supported clipboard helper is installed.");
        var textBytes = Encoding.UTF8.GetBytes(text);
        if (textBytes.Length > MaximumTranscriptBytes)
            return PlatformResult.Failure("clipboard_text_too_large", "The transcript exceeds the private clipboard limit.");
        if (privacyPolicy == ClipboardHistoryPrivacyPolicy.BestEffort && _nativeOwner?.IsAvailable == true)
        {
            var privatePayload = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["text/plain;charset=utf-8"] = textBytes,
                [PrivacyHintMimeType] = "secret"u8.ToArray(),
            });
            return await _nativeOwner.OwnAsync(privatePayload, token).ConfigureAwait(false);
        }
        var result = await RunAsync(_copy, WriteArguments("text/plain;charset=utf-8"), textBytes, token).ConfigureAwait(false);
        return result.IsSuccess ? PlatformResult.Success() : PlatformResult.Failure(result.Error!.Code, result.Error.Message);
    }

    private IReadOnlyList<string> ListArguments() => _wayland
        ? ["--list-types"]
        : ["-selection", "clipboard", "-target", "TARGETS", "-out"];
    private IReadOnlyList<string> ReadArguments(string format) => _wayland
        ? ["--type", format]
        : ["-selection", "clipboard", "-target", format, "-out"];
    private IReadOnlyList<string> WriteArguments(string format) => _wayland
        ? ["--type", format]
        : ["-selection", "clipboard", "-target", format, "-in"];

    private static IReadOnlyList<string> ParseFormats(string output) => output
        .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => value is not "TARGETS" and not "TIMESTAMP" and not "MULTIPLE" and not "SAVE_TARGETS")
        .Distinct(StringComparer.Ordinal)
        .Take(65)
        .ToArray();

    private static KeyValuePair<string, byte[]> SelectPreferred(IReadOnlyDictionary<string, byte[]> formats)
    {
        foreach (var name in new[] { "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING" })
            if (formats.TryGetValue(name, out var value)) return new(name, value);
        return formats.First();
    }

    private static async Task<PlatformResult<byte[]>> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken token, int maximumOutputBytes = int.MaxValue)
    {
        try
        {
            var result = await ExternalProcessRunner.RunAsync(executable, arguments, input, token,
                maximumOutputBytes: maximumOutputBytes).ConfigureAwait(false);
            return result.ExitCode == 0 ? PlatformResult<byte[]>.Success(result.Output)
                : PlatformResult<byte[]>.Failure("clipboard_command_failed", "The clipboard helper reported an error.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InvalidDataException)
        { return PlatformResult<byte[]>.Failure("clipboard_snapshot_too_large", "The clipboard exceeds the private snapshot limit."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        { return PlatformResult<byte[]>.Failure("clipboard_command_failed", "The clipboard helper failed."); }
    }

    private static PlatformResult ValidateSnapshot(ClipboardSnapshot snapshot)
    {
        if (snapshot.Formats.Count > 64)
            return PlatformResult.Failure("clipboard_snapshot_invalid", "The clipboard snapshot has too many formats.");
        long total = 0;
        foreach (var pair in snapshot.Formats)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.IndexOfAny(['\0', '\r', '\n']) >= 0)
                return PlatformResult.Failure("clipboard_snapshot_invalid", "The clipboard snapshot contains an invalid MIME type.");
            total += pair.Value.LongLength;
            if (total > MaximumSnapshotBytes)
                return PlatformResult.Failure("clipboard_snapshot_too_large", "The clipboard exceeds the private snapshot limit.");
        }
        return PlatformResult.Success();
    }

    internal static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public void Dispose() => _nativeOwner?.Dispose();
}
