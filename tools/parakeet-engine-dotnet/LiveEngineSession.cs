using SherpaOnnx;

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
            () => { });
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

internal sealed class BoundedWordAgreement
{
    private const int ConfirmationsNeeded = 3;
    private const int MinimumWords = 8;
    private const int TrailingWords = 3;
    private const int MaximumCharacters = 512 * 1024;
    private readonly string _join;
    private readonly List<string[]> _hypotheses = [];
    private readonly List<string> _committed = [];

    public BoundedWordAgreement(string join) => _join = join;

    public string Preview => Join(_committed.Concat(_hypotheses.LastOrDefault() ?? []));

    public LiveEngineUpdate Observe(string hypothesis)
    {
        var words = WithoutCommittedOverlap(Split(hypothesis));
        if (Join(_committed.Concat(words)).Length > MaximumCharacters)
            throw new InvalidOperationException("Live transcript exceeded the 512 KiB limit");
        _hypotheses.Add(words);
        if (_hypotheses.Count > ConfirmationsNeeded) _hypotheses.RemoveAt(0);

        string[] newlyCommitted = [];
        if (_hypotheses.Count == ConfirmationsNeeded)
        {
            var common = CommonPrefix(_hypotheses);
            var confirmationCount = common.Length >= MinimumWords
                ? common.Length - TrailingWords
                : 0;
            if (confirmationCount >= MinimumWords - TrailingWords)
            {
                newlyCommitted = common[..confirmationCount];
                AppendCommitted(newlyCommitted);
                for (var index = 0; index < _hypotheses.Count; index++)
                    _hypotheses[index] = _hypotheses[index].Skip(confirmationCount).ToArray();
            }
        }
        return new LiveEngineUpdate(Preview, Join(newlyCommitted));
    }

    public LiveEngineUpdate Finish(string finalHypothesis)
    {
        var tail = WithoutCommittedOverlap(Split(finalHypothesis));
        AppendCommitted(tail);
        _hypotheses.Clear();
        var final = Join(_committed);
        return new LiveEngineUpdate(final, Join(tail));
    }

    private string[] WithoutCommittedOverlap(string[] words)
    {
        var maximum = Math.Min(_committed.Count, words.Length);
        for (var count = maximum; count > 0; count--)
        {
            var matches = true;
            for (var index = 0; index < count; index++)
            {
                if (!Equivalent(_committed[_committed.Count - count + index], words[index]))
                { matches = false; break; }
            }
            if (matches) return words[count..];
        }
        return words;
    }

    private void AppendCommitted(IEnumerable<string> words)
    {
        foreach (var word in words)
        {
            if (Preview.Length + word.Length + 1 > MaximumCharacters)
                throw new InvalidOperationException("Live transcript exceeded the 512 KiB limit");
            _committed.Add(word);
        }
    }

    private static string[] CommonPrefix(IReadOnlyList<string[]> values)
    {
        var length = values.Min(value => value.Length);
        var count = 0;
        while (count < length && values.Skip(1).All(value => Equivalent(values[0][count], value[count]))) count++;
        return values[0][..count];
    }

    private static bool Equivalent(string left, string right) => string.Equals(
        NormalizeWord(left), NormalizeWord(right), StringComparison.Ordinal);
    private static string NormalizeWord(string value) => new(value.ToLowerInvariant()
        .Where(character => char.IsLetterOrDigit(character)).ToArray());
    private static string[] Split(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private string Join(IEnumerable<string> words) => string.Join(_join, words);
}
