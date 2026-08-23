var tests = new (string Name, Action Run)[]
{
    ("three stable passes commit while retaining a tail", StableAgreement),
    ("unstable hypotheses remain volatile", UnstableAgreement),
    ("finish commits the unconfirmed tail without overlap", FinishDeduplicates),
    ("no-space languages preserve their join policy", NoSpaceJoin),
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void StableAgreement()
{
    var engine = new BoundedWordAgreement(" ");
    var value = "one two three four five six seven eight nine ten";
    Equal("", engine.Observe(value).Committed);
    Equal("", engine.Observe(value).Committed);
    var third = engine.Observe(value);
    Equal("one two three four five six seven", third.Committed);
    Equal(value, third.Preview);
}

static void UnstableAgreement()
{
    var engine = new BoundedWordAgreement(" ");
    _ = engine.Observe("one two three four five six seven eight");
    _ = engine.Observe("one two changed four five six seven eight");
    var third = engine.Observe("one two three four five six seven eight");
    Equal("", third.Committed);
    Equal("one two three four five six seven eight", third.Preview);
}

static void FinishDeduplicates()
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

static void NoSpaceJoin()
{
    var engine = new BoundedWordAgreement("");
    var final = engine.Finish("alpha beta gamma");
    Equal("alphabetagamma", final.Preview);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}
