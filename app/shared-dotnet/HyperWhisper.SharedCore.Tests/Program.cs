using HyperWhisper.SharedCore;

var tests = new (string Name, Action Run)[]
{
    ("CJK detection crosses the Linux UniFFI boundary", () =>
    {
        Assert.False(SharedCoreBridge.ContainsCjk("HyperWhisper"));
        Assert.True(SharedCoreBridge.ContainsCjk("音声"));
    }),
    ("application types normalize through the shared catalog", () =>
        Assert.Equal("terminal", SharedCoreBridge.NormalizeAppType("Terminal"))),
    ("language-aware spacing stays in the shared core", () =>
        Assert.Equal("hello ", SharedCoreBridge.AppendTrailingSpace("hello", "en"))),
    ("backup validation returns structured failures", () =>
        Assert.True(SharedCoreBridge.ValidateBackup("{}").Count > 0)),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value) => True(!value);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
