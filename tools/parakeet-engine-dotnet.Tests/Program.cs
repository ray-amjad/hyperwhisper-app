// This harness deliberately does NOT use top-level statements. The daemon's own
// entry point is a global `Program` type, and a top-level-statement file emits a
// second global `Program` that shadows it — `Program.IsNoSpaceLanguage` then
// fails to resolve (CS0436 + CS0117). A named entry point keeps the daemon's
// type reachable.
internal static class DaemonTests
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("three stable passes commit while retaining a tail", StableAgreement),
            ("unstable hypotheses remain volatile", UnstableAgreement),
            ("finish commits the unconfirmed tail without overlap", FinishDeduplicates),
            ("no-space languages preserve their join policy", NoSpaceJoin),
            ("the join policy comes from the shared core", NoSpaceLanguageTable),
            ("the shared table widens the daemon's old four codes", NoSpaceLanguageWidening),
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static void StableAgreement()
    {
        var engine = new BoundedWordAgreement(" ");
        var value = "one two three four five six seven eight nine ten";
        Equal("", engine.Observe(value).Committed);
        Equal("", engine.Observe(value).Committed);
        var third = engine.Observe(value);
        Equal("one two three four five six seven", third.Committed);
        Equal(value, third.Preview);
    }

    private static void UnstableAgreement()
    {
        var engine = new BoundedWordAgreement(" ");
        _ = engine.Observe("one two three four five six seven eight");
        _ = engine.Observe("one two changed four five six seven eight");
        var third = engine.Observe("one two three four five six seven eight");
        Equal("", third.Committed);
        Equal("one two three four five six seven eight", third.Preview);
    }

    private static void FinishDeduplicates()
    {
        var engine = new BoundedWordAgreement(" ");
        var value = "one two three four five six seven eight nine ten";
        _ = engine.Observe(value);
        _ = engine.Observe(value);
        _ = engine.Observe(value);
        var final = engine.Finish("six seven eight nine ten eleven");
        Equal("eight nine ten eleven", final.Committed);
        Equal("one two three four five six seven eight nine ten eleven", final.Preview);
    }

    private static void NoSpaceJoin()
    {
        var engine = new BoundedWordAgreement("");
        var final = engine.Finish("alpha beta gamma");
        Equal("alphabetagamma", final.Preview);
    }

    // Program.IsNoSpaceLanguage now delegates to hw-text through the UniFFI core
    // (issue #286). This asserts against the real native library, so it also
    // proves the daemon can load libhyperwhisper_core.so — the P/Invoke this
    // change introduces into a process that previously had none.
    private static void NoSpaceLanguageTable()
    {
        foreach (var code in new[] { "ja", "zh", "ko", "yue" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        foreach (var code in new[] { "en", "de", "fr", "es", "ru", "auto", "" })
            True(!Program.IsNoSpaceLanguage(code), $"'{code}' should be spaced");
    }

    // The four codes the daemon used to hardcode were a strict subset. Moving
    // onto the shared table adds Thai, the explicit Chinese script tags, case
    // insensitivity and the two-character prefix fallback.
    private static void NoSpaceLanguageWidening()
    {
        foreach (var code in new[] { "th", "zh-TW", "zh-Hans", "zh-Hant" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        foreach (var code in new[] { "JA", "YUE", "ZH-HANT" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        foreach (var code in new[] { "zh-CN", "ja-JP", "ko-KR" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        True(!Program.IsNoSpaceLanguage("en-US"), "en-US should be spaced");
    }

    private static void True(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}
