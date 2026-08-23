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
    private readonly IScreenCaptureHook? _portal;
    private readonly string? _executable;
    private readonly CaptureKind _kind;

    public DesktopSelectionCaptureHook() : this(new DesktopCommandRunner()) { }
    internal DesktopSelectionCaptureHook(IDesktopCommandRunner runner)
    {
        _runner = runner;
        if (IsWayland() && CommandClipboardBackend.FindExecutable("python3") is { } python)
            _portal = new PortalScreenshotCaptureHook(runner, python, HasSessionBus());
        else if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("KDE", StringComparison.OrdinalIgnoreCase)
            && CommandClipboardBackend.FindExecutable("spectacle") is { } spectacle)
            (_executable, _kind) = (spectacle, CaptureKind.Spectacle);
        else if (CommandClipboardBackend.FindExecutable("gnome-screenshot") is { } gnome)
            (_executable, _kind) = (gnome, CaptureKind.Gnome);
        else if (!IsWayland() && CommandClipboardBackend.FindExecutable("import") is { } import)
            (_executable, _kind) = (import, CaptureKind.ImageMagick);
    }

    public ScreenCaptureCapabilities GetCapabilities() => _portal?.GetCapabilities() ?? new(_executable is not null,
        _kind switch { CaptureKind.Spectacle => "kde-spectacle", CaptureKind.Gnome => "gnome-screenshot",
            CaptureKind.ImageMagick => "x11-imagemagick", _ => "none" }, false);

    public async ValueTask<PlatformResult> CaptureSelectionAsync(string privateDestinationPath,
        CancellationToken cancellationToken = default)
    {
        if (_portal is not null)
            return await _portal.CaptureSelectionAsync(privateDestinationPath, cancellationToken).ConfigureAwait(false);
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
    private static bool HasSessionBus()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return true;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtime) && File.Exists(Path.Combine(runtime, "bus"));
    }
    private enum CaptureKind { None, Spectacle, Gnome, ImageMagick }
}

internal sealed class PortalScreenshotCaptureHook : IScreenCaptureHook
{
    private const string PortalScript = """
import os,secrets,sys
import gi
gi.require_version('Gio','2.0')
from gi.repository import Gio,GLib

destination=sys.argv[1]
loop=GLib.MainLoop()
bus=None
request_path=None
subscription=0
finished=False

def emit(value):
    global finished
    if finished: return
    finished=True
    print(value,flush=True)
    loop.quit()

def close_request():
    if bus is None or request_path is None: return
    try:
        bus.call_sync('org.freedesktop.portal.Desktop',request_path,
            'org.freedesktop.portal.Request','Close',None,None,
            Gio.DBusCallFlags.NONE,1000,None)
    except Exception:
        pass

def on_timeout():
    close_request()
    emit('TIMEOUT')
    return GLib.SOURCE_REMOVE

def on_response(connection,sender,path,interface,signal,parameters,user_data):
    try:
        response,results=parameters.unpack()
        if response == 1:
            emit('CANCELLED'); return
        if response != 0:
            emit('DENIED'); return
        uri=results.get('uri')
        if not isinstance(uri,str) or not uri:
            emit('FAILED'); return
        ok,contents,_=Gio.File.new_for_uri(uri).load_contents(None)
        if not ok:
            emit('FAILED'); return
        descriptor=os.open(destination,os.O_WRONLY|os.O_TRUNC|os.O_NOFOLLOW)
        try:
            view=memoryview(contents)
            while view:
                written=os.write(descriptor,view)
                view=view[written:]
        finally:
            os.close(descriptor)
        emit('SUCCESS')
    except Exception:
        emit('FAILED')

try:
    bus=Gio.bus_get_sync(Gio.BusType.SESSION,None)
    token='hyperwhisper_'+secrets.token_hex(16)
    sender=bus.get_unique_name()[1:].replace('.','_')
    expected='/org/freedesktop/portal/desktop/request/'+sender+'/'+token
    request_path=expected
    subscription=bus.signal_subscribe('org.freedesktop.portal.Desktop',
        'org.freedesktop.portal.Request','Response',expected,None,
        Gio.DBusSignalFlags.NONE,on_response,None)
    options={'handle_token':GLib.Variant('s',token),'interactive':GLib.Variant('b',True)}
    reply=bus.call_sync('org.freedesktop.portal.Desktop','/org/freedesktop/portal/desktop',
        'org.freedesktop.portal.Screenshot','Screenshot',GLib.Variant('(sa{sv})',('',options)),
        GLib.VariantType('(o)'),Gio.DBusCallFlags.NONE,25000,None)
    returned=reply.unpack()[0]
    if returned != expected:
        bus.signal_unsubscribe(subscription)
        request_path=returned
        subscription=bus.signal_subscribe('org.freedesktop.portal.Desktop',
            'org.freedesktop.portal.Request','Response',returned,None,
            Gio.DBusSignalFlags.NONE,on_response,None)
    GLib.timeout_add_seconds(120,on_timeout)
    loop.run()
except Exception:
    emit('UNAVAILABLE')
finally:
    if bus is not None and subscription:
        bus.signal_unsubscribe(subscription)
""";

    private static readonly TimeSpan PortalTimeout = TimeSpan.FromSeconds(125);
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _python;
    private readonly bool _sessionBusAvailable;

    internal PortalScreenshotCaptureHook(IDesktopCommandRunner runner, string? python, bool sessionBusAvailable)
    {
        _runner = runner;
        _python = python;
        _sessionBusAvailable = sessionBusAvailable;
    }

    public ScreenCaptureCapabilities GetCapabilities() => new(
        _python is not null && _sessionBusAvailable, "xdg-desktop-portal-screenshot", true);

    public async ValueTask<PlatformResult> CaptureSelectionAsync(string privateDestinationPath,
        CancellationToken cancellationToken = default)
    {
        if (_python is null || !_sessionBusAvailable)
            return PlatformResult.Failure("screen_capture_unsupported", "The desktop screenshot portal is unavailable.");
        try
        {
            var result = await _runner.RunAsync(_python, ["-c", PortalScript, privateDestinationPath], null,
                cancellationToken, PortalTimeout).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return PlatformResult.Failure("screen_capture_failed", "The desktop screenshot portal failed.");
            return ParsePortalOutcome(Encoding.UTF8.GetString(result.Output).Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return PlatformResult.Failure("screen_capture_timeout", "The desktop screenshot portal timed out."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return PlatformResult.Failure("screen_capture_failed", "The desktop screenshot portal failed."); }
    }

    internal static PlatformResult ParsePortalOutcome(string outcome) => outcome switch
    {
        "SUCCESS" => PlatformResult.Success(),
        "CANCELLED" => PlatformResult.Failure("screen_capture_cancelled", "Screen capture was cancelled."),
        "DENIED" => PlatformResult.Failure("screen_capture_denied", "Screen capture permission was denied."),
        "UNAVAILABLE" => PlatformResult.Failure("screen_capture_unavailable", "The desktop screenshot portal is unavailable."),
        "TIMEOUT" => PlatformResult.Failure("screen_capture_timeout", "The desktop screenshot portal timed out."),
        _ => PlatformResult.Failure("screen_capture_failed", "The desktop screenshot portal failed."),
    };
}
