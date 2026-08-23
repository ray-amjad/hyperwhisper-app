namespace HyperWhisper.LinuxSpike.Injection;

public enum InjectionOutcome
{
    Injected,
    ClipboardOnly,
    Failed,
}

public sealed record InjectionResult(InjectionOutcome Outcome, string Reason);

public interface IClipboardWriter
{
    Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken);
}

public interface IUInputPasteBackend
{
    Task<bool> TryPasteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The single transcript-injection chokepoint. Text is placed on the clipboard
/// before uinput is attempted, so an unavailable virtual keyboard degrades to
/// a lossless clipboard-only result.
/// </summary>
public sealed class TranscriptInjector
{
    private readonly IClipboardWriter _clipboard;
    private readonly IUInputPasteBackend _uinput;

    public TranscriptInjector(IClipboardWriter clipboard, IUInputPasteBackend uinput)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _uinput = uinput ?? throw new ArgumentNullException(nameof(uinput));
    }

    public async Task<InjectionResult> InjectAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!await _clipboard.TrySetTextAsync(text, cancellationToken))
        {
            return new InjectionResult(InjectionOutcome.Failed, "clipboard-unavailable");
        }

        try
        {
            if (await _uinput.TryPasteAsync(cancellationToken))
            {
                return new InjectionResult(InjectionOutcome.Injected, "uinput-paste");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The transcript is already safe on the clipboard. Native access
            // failures must not turn a recoverable fallback into data loss.
            return new InjectionResult(InjectionOutcome.ClipboardOnly, "uinput-error");
        }

        return new InjectionResult(InjectionOutcome.ClipboardOnly, "uinput-unavailable");
    }
}

public interface IPathAccessProbe
{
    bool CanWrite(string path);
}

public sealed class UInputCapabilityProbe
{
    public const string DefaultDevicePath = "/dev/uinput";

    private readonly IPathAccessProbe _accessProbe;

    public UInputCapabilityProbe(IPathAccessProbe accessProbe)
    {
        _accessProbe = accessProbe ?? throw new ArgumentNullException(nameof(accessProbe));
    }

    public bool IsAvailable(string path = DefaultDevicePath) => _accessProbe.CanWrite(path);
}

public sealed class FilePathAccessProbe : IPathAccessProbe
{
    public bool CanWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
