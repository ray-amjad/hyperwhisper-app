using System.ComponentModel;
using HyperWhisper.LiveStreaming;

namespace HyperWhisper.Linux.Overlay;

internal interface ILinuxLiveTranscriptPreviewSurface : IDisposable
{
    void Apply(EphemeralLiveTranscriptSnapshot snapshot);
    void HideBestEffort();
}

internal sealed class LinuxLiveTranscriptPreviewViewModel : INotifyPropertyChanged
{
    private string _text = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Text
    {
        get => _text;
        private set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal)) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public void Apply(EphemeralLiveTranscriptSnapshot snapshot) => Text = snapshot.DisplayText;
}

/// <summary>
/// Presentation-only owner for ephemeral interim text. It never receives a
/// history repository, diagnostics writer, or logger, and destroys its surface
/// as part of application shutdown.
/// </summary>
internal sealed class LinuxLiveTranscriptPreviewFeedback : IDisposable
{
    private readonly EphemeralLiveTranscriptPreview _preview;
    private readonly ILinuxOverlayDispatcher _dispatcher;
    private readonly Func<ILinuxLiveTranscriptPreviewSurface> _surfaceFactory;
    private ILinuxLiveTranscriptPreviewSurface? _surface;
    private bool _disposed;

    public LinuxLiveTranscriptPreviewFeedback(
        EphemeralLiveTranscriptPreview preview,
        ILinuxOverlayDispatcher? dispatcher = null,
        Func<ILinuxLiveTranscriptPreviewSurface>? surfaceFactory = null)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _dispatcher = dispatcher ?? new AvaloniaLinuxOverlayDispatcher();
        _surfaceFactory = surfaceFactory ?? (() => new LinuxLiveTranscriptPreviewWindow(
            new LinuxLiveTranscriptPreviewViewModel()));
        _preview.Changed += OnChanged;
    }

    public void Begin() => _preview.Begin();
    public void Complete() => _preview.Complete();
    public void Cancel() => _preview.Cancel();

    private void OnChanged(object? sender, EphemeralLiveTranscriptSnapshot snapshot)
    {
        try
        {
            _dispatcher.Post(() =>
            {
                if (_disposed) return;
                try
                {
                    if (!LinuxLivePreviewVisibilityPolicy.ShouldShow(snapshot))
                    {
                        _surface?.HideBestEffort();
                        return;
                    }
                    _surface ??= _surfaceFactory();
                    _surface.Apply(snapshot);
                }
                catch { /* Preview rendering cannot interrupt transcription. */ }
            });
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preview.Changed -= OnChanged;
        _preview.Cancel();
        try { _dispatcher.Post(() => { try { _surface?.Dispose(); } catch { } _surface = null; }); }
        catch { try { _surface?.Dispose(); } catch { } _surface = null; }
    }
}
