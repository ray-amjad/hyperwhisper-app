using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Input;

public interface IGlobalShortcutDiagnostics
{
    void MalformedFrame();
    void SourceFailed();
    void SubscriberFailed();
}

internal interface IShortcutInterferenceSource
{
    event EventHandler? Interfered;
    void SetInterferenceArmed(bool armed);
}

public sealed class LinuxGlobalShortcutService : IGlobalShortcutService, IShortcutInterferenceSource
{
    private readonly object _gate = new();
    private readonly IEvdevSourceFactory _sourceFactory;
    private readonly IGlobalShortcutDiagnostics? _diagnostics;
    private readonly EvdevShortcutFilter _filter = new();
    private readonly IGlobalShortcutService? _x11;
    private IReadOnlyList<IEvdevSource> _sources = [];
    private CancellationTokenSource? _cancellation;
    private Task[] _readerTasks = [];
    private bool _disposed;
    private volatile bool _interferenceArmed;

    public LinuxGlobalShortcutService() : this(new LinuxKeyboardSourceFactory(), null,
        IsTrueXorgSession() ? new X11GlobalShortcutService() : null) { }

    internal LinuxGlobalShortcutService(
        IEvdevSourceFactory sourceFactory,
        IGlobalShortcutDiagnostics? diagnostics) : this(sourceFactory, diagnostics, null) { }

    internal LinuxGlobalShortcutService(IEvdevSourceFactory sourceFactory,
        IGlobalShortcutDiagnostics? diagnostics, IGlobalShortcutService? x11)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _diagnostics = diagnostics;
        _x11 = x11;
        if (_x11 is not null)
        {
            _x11.ShortcutPressed += ForwardPressed;
            _x11.ShortcutReleased += ForwardReleased;
        }
    }

    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;
    public event EventHandler? Interfered;

    public PlatformResult Start()
    {
        if (_x11 is not null) return _x11.Start();
        lock (_gate)
        {
            if (_disposed)
            {
                return PlatformResult.Failure("shortcut_disposed", "The shortcut service is disposed.");
            }

            if (_cancellation is not null)
            {
                return PlatformResult.Success();
            }

            var opened = _sourceFactory.OpenKeyboardSources();
            if (opened.ErrorCode is not null)
            {
                return PlatformResult.Failure(opened.ErrorCode, opened.ErrorMessage!);
            }

            _sources = opened.Sources;
            _cancellation = new CancellationTokenSource();
            _readerTasks = _sources
                .Select(source => ReadSourceAsync(source, _cancellation.Token))
                .ToArray();
            return PlatformResult.Success();
        }
    }

    public IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(
        IReadOnlyCollection<NamedShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        if (_x11 is not null) return _x11.RegisterShortcuts(shortcuts);
        var output = new Dictionary<string, PlatformResult>(StringComparer.Ordinal);
        var bindings = new List<EvdevBinding>();
        var duplicateNames = shortcuts.GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var shortcut in shortcuts)
        {
            if (duplicateNames.Contains(shortcut.Name))
            {
                output[shortcut.Name] = PlatformResult.Failure("shortcut_duplicate", "Shortcut names must be unique.");
                continue;
            }

            var mapped = EvdevShortcutMapper.Map(shortcut);
            if (mapped.IsFailure)
            {
                output[shortcut.Name] = PlatformResult.Failure(mapped.Error!.Code, mapped.Error.Message);
                continue;
            }

            bindings.Add(mapped.Value!);
            output[shortcut.Name] = PlatformResult.Success();
        }

        if (output.Values.All(result => result.IsSuccess) && bindings.Count == shortcuts.Count)
            _filter.ReplaceBindings(bindings);
        return output;
    }

    public void Clear() { if (_x11 is not null) _x11.Clear(); else _filter.ReplaceBindings([]); }
    public void ResetKeyboardState() { if (_x11 is not null) _x11.ResetKeyboardState(); else _filter.Reset(); }

    private async Task ReadSourceAsync(IEvdevSource source, CancellationToken cancellationToken)
    {
        var frame = new byte[EvdevParser.X64FrameSize];
        try
        {
            while (await source.ReadFrameAsync(frame, cancellationToken))
            {
                if (!EvdevParser.TryParse(frame, out var input))
                {
                    _diagnostics?.MalformedFrame();
                    continue;
                }

                var filtered = _filter.Process(source.Id, input, _interferenceArmed);
                if (filtered.Interfered) RaiseInterfered();
                foreach (var signal in filtered.Signals)
                {
                    Raise(signal.Shortcut, signal.Pressed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _diagnostics?.SourceFailed();
        }
    }
    public void SetInterferenceArmed(bool armed) => _interferenceArmed = armed;
    private void ForwardPressed(object? sender, ShortcutTriggeredEventArgs args) => Raise(new NamedShortcut(args.Name, args.Shortcut), true);
    private void ForwardReleased(object? sender, ShortcutTriggeredEventArgs args) => Raise(new NamedShortcut(args.Name, args.Shortcut), false);
    internal static bool IsTrueXorgSession() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "x11", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));

    private void RaiseInterfered()
    {
        var handlers = Interfered; if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch { _diagnostics?.SubscriberFailed(); }
    }

    private void Raise(NamedShortcut shortcut, bool pressed)
    {
        var handlers = pressed ? ShortcutPressed : ShortcutReleased;
        if (handlers is null)
        {
            return;
        }

        var args = new ShortcutTriggeredEventArgs(shortcut.Name, shortcut.Shortcut);
        foreach (EventHandler<ShortcutTriggeredEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                _diagnostics?.SubscriberFailed();
            }
        }
    }

    public void Dispose()
    {
        Task[] tasks;
        IReadOnlyList<IEvdevSource> sources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_x11 is not null)
            {
                _x11.ShortcutPressed -= ForwardPressed; _x11.ShortcutReleased -= ForwardReleased;
                _x11.Dispose(); GC.SuppressFinalize(this); return;
            }
            _cancellation?.Cancel();
            tasks = _readerTasks;
            sources = _sources;
            _readerTasks = [];
            _sources = [];
        }

        foreach (var source in sources.Reverse())
        {
            try
            {
                source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        // Some evdev character-device drivers do not complete an outstanding
        // read promptly after close. The readers own no state after sources are
        // detached, so observe completion without blocking the desktop UI thread.
        _ = Task.WhenAll(tasks).ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _cancellation?.Dispose();
        _cancellation = null;
        GC.SuppressFinalize(this);
    }
}
