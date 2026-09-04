using SherpaOnnx;
using uniffi.hyperwhisper_core;

internal sealed record LiveEngineUpdate(string Preview, string Committed);

/// <summary>
/// One bounded daemon-side live session. Nemotron retains its native online
/// decoder stream. Offline Parakeet performs independent rolling passes and
/// only exposes a committed prefix after three consecutive hypotheses agree.
/// </summary>
internal sealed class LiveEngineSession : IDisposable
{
    private const int MaximumSessionSamples = Program.TargetSampleRate * 60 * 20;
    private readonly Func<float[], LiveEngineUpdate> _accept;
    private readonly Func<LiveEngineUpdate> _finish;
    private readonly Action _dispose;
    private int _acceptedSamples;
    private bool _finished;

    private LiveEngineSession(
        Func<float[], LiveEngineUpdate> accept,
        Func<LiveEngineUpdate> finish,
        Action dispose)
    {
        _accept = accept;
        _finish = finish;
        _dispose = dispose;
    }

    public static LiveEngineSession CreateOnline(OnlineRecognizer recognizer, string language)
    {
        var stream = recognizer.CreateStream();
        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            stream.SetOption("language", language);

        string Current()
        {
            while (recognizer.IsReady(stream)) recognizer.Decode(stream);
            return Normalize(recognizer.GetResult(stream).Text);
        }

        return new LiveEngineSession(
            samples =>
            {
                stream.AcceptWaveform(Program.TargetSampleRate, samples);
                return new LiveEngineUpdate(Current(), "");
            },
            () =>
            {
                stream.AcceptWaveform(Program.TargetSampleRate, new float[Program.TargetSampleRate / 2]);
                stream.InputFinished();
                var text = Current();
                return new LiveEngineUpdate(text, text);
            },
            stream.Dispose);
    }

    public static LiveEngineSession CreateRollingOffline(OfflineRecognizer recognizer, string join)
    {
        const int decodeInterval = Program.TargetSampleRate;
        const int maximumWindow = Program.TargetSampleRate * 15;
        const int retainedTail = Program.TargetSampleRate * 3;
        var samples = new List<float>(maximumWindow);
        var samplesSinceLastPass = 0;
        var agreement = new BoundedWordAgreement(join);

        string Decode()
        {
            if (samples.Count < Program.TargetSampleRate) return agreement.Preview;
            var padded = new float[Math.Min(maximumWindow, samples.Count) + Program.TargetSampleRate];
            samples.CopyTo(Math.Max(0, samples.Count - maximumWindow), padded, 0,
                Math.Min(maximumWindow, samples.Count));
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(Program.TargetSampleRate, padded);
            recognizer.Decode(stream);
            return Normalize(stream.Result.Text);
        }

        return new LiveEngineSession(
            chunk =>
            {
                samples.AddRange(chunk);
                samplesSinceLastPass += chunk.Length;
                if (samples.Count > maximumWindow)
                    samples.RemoveRange(0, samples.Count - maximumWindow);
                if (samplesSinceLastPass < decodeInterval)
                    return new LiveEngineUpdate(agreement.Preview, "");
                samplesSinceLastPass = 0;
                var update = agreement.Observe(Decode());
                if (update.Committed.Length > 0 && samples.Count > retainedTail)
                {
                    samples.RemoveRange(0, samples.Count - retainedTail);
                }
                return update;
            },
            () => agreement.Finish(Decode()),
            agreement.Dispose);
    }

    public LiveEngineUpdate Accept(float[] samples)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) return new LiveEngineUpdate("", "");
        if (_acceptedSamples > MaximumSessionSamples - samples.Length)
            throw new InvalidOperationException("Live session exceeded the 20 minute limit");
        _acceptedSamples += samples.Length;
        return _accept(samples);
    }

    public LiveEngineUpdate Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        try { return _finish(); }
        finally { _dispose(); }
    }

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        _dispose();
    }

    private static string Normalize(string? value) => string.Join(' ',
        (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// The bounded LocalAgreement-3 engine behind the rolling-offline live path:
/// three consecutive hypotheses must agree on a word before it is committed,
/// and the transcript is capped at 512 KiB of UTF-16 units.
/// <para>
/// The algorithm itself now lives in <c>hw_text::agreement</c> and is reached
/// through the core's <c>HwBoundedAgreementSession</c> (issue #286), so the
/// daemon and the macOS streaming engine share one implementation instead of
/// two ports of the same paper. This type is only the adapter: it keeps the
/// daemon-facing surface — the constructor, <see cref="Preview"/>,
/// <see cref="Observe"/>, <see cref="Finish"/> and the
/// <see cref="InvalidOperationException"/> message that reaches the wire —
/// exactly as it was, which is why the harness in
/// <c>parakeet-engine-dotnet.Tests</c> did not change with it.
/// </para>
/// </summary>
internal sealed class BoundedWordAgreement : IDisposable
{
    private readonly HwBoundedAgreementSession _session;

    /// <summary>
    /// The preview the last <see cref="Observe"/> or <see cref="Finish"/>
    /// returned. The core's <c>preview()</c> accessor exists but must not be
    /// called from here: <c>LiveEngineSession.Decode</c> and the sub-interval
    /// early return read <see cref="Preview"/> on <em>every</em> <c>audio</c>
    /// request, and the value is a pure function of state that only those two
    /// calls change — so caching what they returned is exactly equivalent and
    /// keeps the audio path off the FFI entirely.
    /// </summary>
    private string _preview = "";

    public BoundedWordAgreement(string join) => _session = new HwBoundedAgreementSession(join);

    public string Preview => _preview;

    public LiveEngineUpdate Observe(string hypothesis) => Apply(() => _session.Observe(hypothesis));

    public LiveEngineUpdate Finish(string finalHypothesis) => Apply(() => _session.Finish(finalHypothesis));

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// Run one core call, cache its preview and translate its failure. The
    /// generated <c>HwStreamException.LimitExceeded</c> carries no message of
    /// its own, and <c>Program</c>'s live handler reports the exception text
    /// verbatim, so the daemon's original string is restored here rather than
    /// left to the binding.
    /// </summary>
    private LiveEngineUpdate Apply(Func<HwStreamUpdate> call)
    {
        HwStreamUpdate update;
        try
        {
            update = call();
        }
        catch (HwStreamException.LimitExceeded)
        {
            throw new InvalidOperationException("Live transcript exceeded the 512 KiB limit");
        }
        _preview = update.preview;
        return new LiveEngineUpdate(update.preview, update.committed);
    }
}
