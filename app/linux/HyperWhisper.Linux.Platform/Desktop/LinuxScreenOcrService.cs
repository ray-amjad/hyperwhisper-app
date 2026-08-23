using System.Text;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public sealed record ScreenCaptureCapabilities(bool Available, string Backend, bool UsesDesktopPortal);

public interface IScreenCaptureHook
{
    ScreenCaptureCapabilities GetCapabilities();
    ValueTask<PlatformResult> CaptureSelectionAsync(string privateDestinationPath,
        CancellationToken cancellationToken = default);
}

public sealed record LinuxScreenOcrCapabilities(bool Available, string CaptureBackend,
    bool UsesDesktopPortal, bool TesseractAvailable);

public sealed class LinuxScreenOcrService : IScreenOcrService
{
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IScreenCaptureHook _capture;
    private readonly IDesktopCommandRunner _runner;
    private readonly IAppPaths _paths;
    private readonly string? _tesseract;

    public LinuxScreenOcrService() : this(new DesktopSelectionCaptureHook(), new DesktopCommandRunner(),
        new LinuxAppPaths(), CommandClipboardBackend.FindExecutable("tesseract")) { }

    public LinuxScreenOcrService(IScreenCaptureHook captureHook) : this(captureHook, new DesktopCommandRunner(),
        new LinuxAppPaths(), CommandClipboardBackend.FindExecutable("tesseract")) { }

    internal LinuxScreenOcrService(IScreenCaptureHook capture, IDesktopCommandRunner runner,
        IAppPaths paths, string? tesseract)
    {
        _capture = capture;
        _runner = runner;
        _paths = paths;
        _tesseract = tesseract;
    }

    public LinuxScreenOcrCapabilities GetCapabilities()
    {
        var capture = _capture.GetCapabilities();
        return new(capture.Available && _tesseract is not null, capture.Backend,
            capture.UsesDesktopPortal, _tesseract is not null);
    }

    public async ValueTask<PlatformResult<string?>> CaptureAndRecognizeAsync(int maxCharacters = 2000,
        CancellationToken cancellationToken = default)
    {
        if (maxCharacters <= 0)
            return PlatformResult<string?>.Failure("ocr_limit_invalid", "The OCR character limit must be positive.");
        if (!_capture.GetCapabilities().Available)
            return PlatformResult<string?>.Failure("screen_capture_unsupported", "Interactive screen capture is unsupported on this desktop.");
        if (_tesseract is null)
            return PlatformResult<string?>.Failure("ocr_unavailable", "Tesseract OCR is not installed.");

        string? imagePath = null;
        string? privateDirectory = null;
        try
        {
            var root = Path.GetFullPath(_paths.TemporaryDirectory);
            Directory.CreateDirectory(root);
            privateDirectory = Path.Combine(root, $"ocr-{Guid.NewGuid():N}");
            Directory.CreateDirectory(privateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(privateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            imagePath = Path.Combine(privateDirectory, "capture.png");
            using (new FileStream(imagePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            })) { }

            var capture = await _capture.CaptureSelectionAsync(imagePath, cancellationToken).ConfigureAwait(false);
            if (capture.IsFailure) return PlatformResult<string?>.Failure(capture.Error!.Code, capture.Error.Message);
            File.SetUnixFileMode(imagePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (new FileInfo(imagePath).Length == 0)
                return PlatformResult<string?>.Failure("screen_capture_empty", "The screen capture did not produce an image.");

            var result = await _runner.RunAsync(_tesseract, [imagePath, "stdout", "-l", "eng"], null,
                cancellationToken, OcrTimeout).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return PlatformResult<string?>.Failure("ocr_failed", "Tesseract could not recognize the captured image.");
            var text = StrictUtf8.GetString(result.Output).Trim();
            if (text.Length > maxCharacters) text = text[..maxCharacters];
            return PlatformResult<string?>.Success(text.Length == 0 ? null : text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return PlatformResult<string?>.Failure("ocr_timeout", "Screen recognition timed out."); }
        catch (DecoderFallbackException) { return PlatformResult<string?>.Failure("ocr_invalid_output", "Tesseract returned invalid text output."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return PlatformResult<string?>.Failure("ocr_unavailable", "Screen recognition is unavailable."); }
        finally
        {
            if (imagePath is not null)
            {
                try { File.Delete(imagePath); } catch { }
            }
            if (privateDirectory is not null)
            {
                try { Directory.Delete(privateDirectory); } catch { }
            }
        }
    }
}

internal sealed class DesktopSelectionCaptureHook : IScreenCaptureHook
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMinutes(2);
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _executable;
    private readonly CaptureKind _kind;

    public DesktopSelectionCaptureHook() : this(new DesktopCommandRunner()) { }
    internal DesktopSelectionCaptureHook(IDesktopCommandRunner runner)
    {
        _runner = runner;
        if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("KDE", StringComparison.OrdinalIgnoreCase)
            && CommandClipboardBackend.FindExecutable("spectacle") is { } spectacle)
            (_executable, _kind) = (spectacle, CaptureKind.Spectacle);
        else if (CommandClipboardBackend.FindExecutable("gnome-screenshot") is { } gnome)
            (_executable, _kind) = (gnome, CaptureKind.Gnome);
        else if (!IsWayland() && CommandClipboardBackend.FindExecutable("import") is { } import)
            (_executable, _kind) = (import, CaptureKind.ImageMagick);
    }

    public ScreenCaptureCapabilities GetCapabilities() => new(_executable is not null,
        _kind switch { CaptureKind.Spectacle => "kde-spectacle", CaptureKind.Gnome => "gnome-screenshot",
            CaptureKind.ImageMagick => "x11-imagemagick", _ => "none" }, false);

    public async ValueTask<PlatformResult> CaptureSelectionAsync(string privateDestinationPath,
        CancellationToken cancellationToken = default)
    {
        if (_executable is null) return PlatformResult.Failure("screen_capture_unsupported", "No supported interactive capture tool is installed.");
        var arguments = _kind switch
        {
            CaptureKind.Spectacle => new[] { "-r", "-b", "-n", "-o", privateDestinationPath },
            CaptureKind.Gnome => ["-a", "-f", privateDestinationPath],
            CaptureKind.ImageMagick => [privateDestinationPath],
            _ => [],
        };
        try
        {
            var result = await _runner.RunAsync(_executable, arguments, null, cancellationToken, CaptureTimeout).ConfigureAwait(false);
            return result.ExitCode == 0 ? PlatformResult.Success()
                : PlatformResult.Failure("screen_capture_cancelled", "Screen capture was cancelled or failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return PlatformResult.Failure("screen_capture_timeout", "Interactive screen capture timed out."); }
        catch { return PlatformResult.Failure("screen_capture_failed", "Interactive screen capture failed."); }
    }

    private static bool IsWayland() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
    private enum CaptureKind { None, Spectacle, Gnome, ImageMagick }
}
