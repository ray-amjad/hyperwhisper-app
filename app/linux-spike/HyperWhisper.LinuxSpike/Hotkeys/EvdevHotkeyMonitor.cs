namespace HyperWhisper.LinuxSpike.Hotkeys;

public sealed class EvdevHotkeyMonitor : IAsyncDisposable
{
    private readonly IEvdevFrameSource _source;
    private readonly HotkeyPrivacyFilter _filter;
    private readonly IHotkeyDiagnostics _diagnostics;

    internal EvdevHotkeyMonitor(
        IEvdevFrameSource source,
        IReadOnlyList<HotkeyBinding> bindings,
        IHotkeyDiagnostics? diagnostics = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _filter = new HotkeyPrivacyFilter(bindings);
        _diagnostics = diagnostics ?? NullHotkeyDiagnostics.Instance;
    }

    public event EventHandler<HotkeySignal>? Signal;

    public static EvdevHotkeyMonitor Open(
        string devicePath,
        IReadOnlyList<HotkeyBinding> bindings,
        IHotkeyDiagnostics? diagnostics = null) =>
        new(new FileEvdevFrameSource(devicePath), bindings, diagnostics);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var frame = new byte[EvdevEventParser.X64FrameSize];
        try
        {
            while (await _source.ReadFrameAsync(frame, cancellationToken))
            {
                if (!EvdevEventParser.TryParseX64(frame, out var inputEvent))
                {
                    _diagnostics.MalformedFrame();
                    continue;
                }

                foreach (var signal in _filter.Process(inputEvent))
                {
                    RaiseSignal(signal);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _diagnostics.ReadFailed();
            throw;
        }
    }

    private void RaiseSignal(HotkeySignal signal)
    {
        var handlers = Signal;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<HotkeySignal> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, signal);
            }
            catch
            {
                _diagnostics.HandlerFailed();
            }
        }
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
