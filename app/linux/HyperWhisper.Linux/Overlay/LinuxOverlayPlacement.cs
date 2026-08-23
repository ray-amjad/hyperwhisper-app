using System.Text.Json;

namespace HyperWhisper.Linux.Overlay;

internal static class LinuxOverlayInteractionPolicy
{
    // This surface may receive pointer movement for dragging but must never become
    // a keyboard target and displace the application receiving dictated text.
    public const bool ShowActivated = false;
    public const bool Focusable = false;
}

internal readonly record struct LinuxOverlayPlacement(double XRatio, double YRatio)
{
    public bool IsValid => double.IsFinite(XRatio) && double.IsFinite(YRatio)
        && XRatio is >= 0 and <= 1 && YRatio is >= 0 and <= 1;
}

internal readonly record struct LinuxOverlayPixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
}

internal readonly record struct LinuxOverlayPixelPoint(int X, int Y);

internal static class LinuxOverlayPlacementCalculator
{
    private const double DefaultXRatio = 0.5;
    private const int DefaultBottomMarginDip = 20;

    public static LinuxOverlayPixelPoint Restore(
        LinuxOverlayPlacement? stored,
        LinuxOverlayPixelRect workArea,
        int overlayWidth,
        int overlayHeight,
        double scaling)
    {
        ValidateGeometry(workArea, overlayWidth, overlayHeight);
        var scale = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        var maxX = Math.Max(0, workArea.Width - overlayWidth);
        var maxY = Math.Max(0, workArea.Height - overlayHeight);
        var placement = stored is { IsValid: true } value
            ? value
            : new LinuxOverlayPlacement(DefaultXRatio,
                maxY == 0 ? 0 : Math.Clamp((maxY - DefaultBottomMarginDip * scale) / maxY, 0, 1));
        return new(
            workArea.X + (int)Math.Round(maxX * placement.XRatio),
            workArea.Y + (int)Math.Round(maxY * placement.YRatio));
    }

    public static LinuxOverlayPlacement Capture(
        LinuxOverlayPixelPoint position,
        LinuxOverlayPixelRect workArea,
        int overlayWidth,
        int overlayHeight)
    {
        ValidateGeometry(workArea, overlayWidth, overlayHeight);
        var maxX = Math.Max(0, workArea.Width - overlayWidth);
        var maxY = Math.Max(0, workArea.Height - overlayHeight);
        return new(
            maxX == 0 ? 0 : Math.Clamp((double)(position.X - workArea.X) / maxX, 0, 1),
            maxY == 0 ? 0 : Math.Clamp((double)(position.Y - workArea.Y) / maxY, 0, 1));
    }

    private static void ValidateGeometry(LinuxOverlayPixelRect workArea, int overlayWidth, int overlayHeight)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || overlayWidth <= 0 || overlayHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea), "Overlay placement geometry must be positive.");
    }
}

internal interface ILinuxOverlayPlacementStore
{
    LinuxOverlayPlacement? Load();
    void Save(LinuxOverlayPlacement placement);
}

internal sealed class LinuxOverlayPlacementPersistence : IDisposable
{
    private readonly object _gate = new();
    private readonly ILinuxOverlayPlacementStore _store;
    private readonly TimeSpan _delay;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _pending;
    private LinuxOverlayPlacement _latest;
    private bool _hasPending;
    private bool _disposed;

    public LinuxOverlayPlacementPersistence(
        ILinuxOverlayPlacementStore store,
        TimeSpan? delay = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _delay = delay ?? TimeSpan.FromMilliseconds(350);
        if (_delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _schedule = schedule ?? Schedule;
    }

    public LinuxOverlayPlacement? LoadBestEffort()
    {
        try { var value = _store.Load(); return value is { IsValid: true } ? value : null; }
        catch { return null; }
    }

    public void SaveDebounced(LinuxOverlayPlacement placement)
    {
        if (!placement.IsValid) return;
        lock (_gate)
        {
            if (_disposed) return;
            _latest = placement;
            _hasPending = true;
            _pending?.Dispose();
            _pending = _schedule(_delay, Flush);
        }
    }

    private void Flush()
    {
        LinuxOverlayPlacement placement;
        lock (_gate)
        {
            if (_disposed) return;
            placement = _latest;
            _hasPending = false;
            _pending?.Dispose();
            _pending = null;
        }
        try { _store.Save(placement); } catch { /* Placement must never block recording. */ }
    }

    public void Dispose()
    {
        LinuxOverlayPlacement placement = default;
        var save = false;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            save = _hasPending;
            placement = _latest;
            _hasPending = false;
            _pending?.Dispose();
            _pending = null;
        }
        if (save)
            try { _store.Save(placement); } catch { }
    }

    private static IDisposable Schedule(TimeSpan delay, Action callback)
    {
        Timer? timer = null;
        timer = new Timer(_ => { try { callback(); } finally { timer?.Dispose(); } }, null, delay,
            Timeout.InfiniteTimeSpan);
        return timer;
    }
}

internal sealed class JsonLinuxOverlayPlacementStore : ILinuxOverlayPlacementStore
{
    private const int MaximumBytes = 1024;
    private readonly string _path;

    public JsonLinuxOverlayPlacementStore(string? configHome = null)
    {
        var root = string.IsNullOrWhiteSpace(configHome)
            ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            : configHome;
        if (string.IsNullOrWhiteSpace(root))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = Path.Combine(home, ".config");
        }
        _path = Path.Combine(Path.GetFullPath(root), "hyperwhisper", "overlay-placement.json");
    }

    public LinuxOverlayPlacement? Load()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumBytes) return null;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        var placement = JsonSerializer.Deserialize<LinuxOverlayPlacement>(stream);
        return placement.IsValid ? placement : null;
    }

    public void Save(LinuxOverlayPlacement placement)
    {
        if (!placement.IsValid) return;
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, placement);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
