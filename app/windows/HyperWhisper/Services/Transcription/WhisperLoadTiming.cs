namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Adds up, for one caller, how long the Whisper requests made inside it spent
/// starting the worker and loading the model (#1932). The Local API opens one
/// around its transcription to split <c>load_ms</c> from <c>decode_ms</c>: after
/// an idle exit the first request pays the worker start and the model load.
///
/// The scope is an AsyncLocal, so a GUI dictation running on the same shared
/// <see cref="TranscriptionService"/> at the same time adds to none, or to its own.
/// </summary>
public sealed class WhisperLoadTiming : IDisposable
{
    private static readonly AsyncLocal<WhisperLoadTiming?> s_current = new();

    private readonly WhisperLoadTiming? _outer;
    private long _ticks;
    private bool _disposed;

    private WhisperLoadTiming(WhisperLoadTiming? outer) => _outer = outer;

    /// <summary>
    /// Opens a scope on the calling flow. Call it from the method that awaits the
    /// transcription (not from inside a helper that awaits): a value set inside
    /// an async method does not flow back out to its caller.
    /// </summary>
    public static WhisperLoadTiming Begin()
    {
        var timing = new WhisperLoadTiming(s_current.Value);
        s_current.Value = timing;
        return timing;
    }

    /// <summary>The worker start and model load time added so far.</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Adds <paramref name="startAndLoad"/> to the scope on this flow, if one is open.</summary>
    internal static void Add(TimeSpan startAndLoad)
    {
        if (startAndLoad <= TimeSpan.Zero || s_current.Value is not { } timing) return;
        Interlocked.Add(ref timing._ticks, startAndLoad.Ticks);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(s_current.Value, this)) s_current.Value = _outer;
    }
}
