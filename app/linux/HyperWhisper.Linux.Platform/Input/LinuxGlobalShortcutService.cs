using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Input;

public interface IGlobalShortcutDiagnostics
{
    void MalformedFrame();
    void SourceFailed();
    void SubscriberFailed();
}

public sealed class LinuxGlobalShortcutService : IGlobalShortcutService
{
    private readonly object _gate = new();
    private readonly IEvdevSourceFactory _sourceFactory;
    private readonly IGlobalShortcutDiagnostics? _diagnostics;
    private readonly EvdevShortcutFilter _filter = new();
    private IReadOnlyList<IEvdevSource> _sources = [];
    private CancellationTokenSource? _cancellation;
    private Task[] _readerTasks = [];
    private bool _disposed;

    public LinuxGlobalShortcutService()
        : this(new LinuxKeyboardSourceFactory(), null)
    {
    }

    internal LinuxGlobalShortcutService(
        IEvdevSourceFactory sourceFactory,
        IGlobalShortcutDiagnostics? diagnostics)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _diagnostics = diagnostics;
    }

    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;

    public PlatformResult Start()
    {
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

        _filter.ReplaceBindings(bindings);
        return output;
    }

    public void Clear() => _filter.ReplaceBindings([]);
    public void ResetKeyboardState() => _filter.Reset();

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

                foreach (var signal in _filter.Process(source.Id, input))
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

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch
        {
        }

        _cancellation?.Dispose();
        _cancellation = null;
        GC.SuppressFinalize(this);
    }
}
