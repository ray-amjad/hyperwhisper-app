using HyperWhisper.SharedCore;

namespace HyperWhisper.LiveStreaming;

public sealed record EphemeralLiveTranscriptSnapshot(
    string CommittedText,
    string PartialText,
    string DisplayText,
    bool IsActive);

/// <summary>
/// Memory-only presentation state for interim live text. It has no serializer,
/// logger, or repository dependency. Partial content is replaced in place and
/// all content is cleared synchronously when the session completes or aborts.
/// </summary>
public sealed class EphemeralLiveTranscriptPreview : ILiveTranscriptSink
{
    public const int MaximumDisplayCharacters = 32 * 1024;
    private readonly object _gate = new();
    private string _committed = "";
    private string _partial = "";
    private bool _active;

    public event EventHandler<EphemeralLiveTranscriptSnapshot>? Changed;

    public EphemeralLiveTranscriptSnapshot Snapshot
    {
        get
        {
            lock (_gate) return CreateSnapshot();
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            _committed = "";
            _partial = "";
            _active = true;
        }
        Publish();
    }

    public void OnTranscript(LiveTranscriptUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var value = Normalize(update.Text);
        if (value.Length == 0) return;
        lock (_gate)
        {
            if (!_active) return;
            if (update.IsFinal)
            {
                _committed = AppendWithoutOverlap(_committed, value);
                _partial = "";
            }
            else
            {
                _partial = value;
            }
            BoundDisplay();
        }
        Publish();
    }

    public void Complete() => Clear();
    public void Cancel() => Clear();

    private void Clear()
    {
        lock (_gate)
        {
            _committed = "";
            _partial = "";
            _active = false;
        }
        Publish();
    }

    private void BoundDisplay()
    {
        var combined = Display(_committed, _partial);
        if (combined.Length <= MaximumDisplayCharacters) return;
        var overflow = combined.Length - MaximumDisplayCharacters;
        if (overflow >= _committed.Length) _committed = "";
        else _committed = _committed[overflow..].TrimStart();
        combined = Display(_committed, _partial);
        if (combined.Length > MaximumDisplayCharacters)
            _partial = combined[^MaximumDisplayCharacters..].TrimStart();
    }

    private EphemeralLiveTranscriptSnapshot CreateSnapshot() => new(
        _committed, _partial, Display(_committed, _partial), _active);

    private void Publish()
    {
        EphemeralLiveTranscriptSnapshot snapshot;
        EventHandler<EphemeralLiveTranscriptSnapshot>? handlers;
        lock (_gate) { snapshot = CreateSnapshot(); handlers = Changed; }
        if (handlers is null) return;
        foreach (EventHandler<EphemeralLiveTranscriptSnapshot> handler in handlers.GetInvocationList())
            try { handler(this, snapshot); } catch { }
    }

    private static string Display(string committed, string partial)
    {
        if (partial.Length == 0) return committed;
        if (committed.Length == 0 || partial.StartsWith(committed, StringComparison.OrdinalIgnoreCase)) return partial;
        return $"{committed} {partial}";
    }

    private static string AppendWithoutOverlap(string current, string addition)
    {
        if (current.Length == 0) return addition;
        if (addition.StartsWith(current, StringComparison.OrdinalIgnoreCase)) return addition;
        var maximum = Math.Min(current.Length, addition.Length);
        for (var count = maximum; count > 0; count--)
        {
            if (string.Equals(current[^count..], addition[..count], StringComparison.OrdinalIgnoreCase))
                return Normalize(current + addition[count..]);
        }
        return $"{current} {addition}";
    }

    private static string Normalize(string value) => string.Join(' ',
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
