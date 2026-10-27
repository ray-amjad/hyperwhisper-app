// CLOUD CREDITS LOW-BALANCE WARNING (issue #2618)
//
// The server floors minutes_remaining (hyperwhisper-cloud/src/routes/usage.ts,
// floor(credits / 1.67)), so a balance under one minute arrives as 0 minutes with
// credits still above 0. CloudAccountSettingsPage colours the number and shows a
// banner only when IsLow or IsExhausted is true, so that balance must count as
// low. The macOS counterpart is hyperwhisperTests/HyperWhisperCloudCreditsWarningTests.swift.

using HyperWhisper.Models;

namespace HyperWhisper.SmokeTests;

internal static class CloudCreditsWarningChecks
{
    public static void RunAll(Action<string, Action> run)
    {
        run("cloud credits: a balance under one minute shows the low warning (#2618)", () =>
        {
            var credits = Usage(1.5);
            Assert(credits.MinutesRemaining == 0, $"1.5 credits should floor to 0 minutes, got {credits.MinutesRemaining}");
            Assert(credits.IsLow, "1.5 credits (~54 s) must count as low");
            Assert(!credits.IsExhausted, "1.5 credits is not exhausted");
            Assert(ShowsAnyWarning(credits), "no low and no exhausted banner at 0 minutes with credits left");
        });

        run("cloud credits: the low threshold and the exhausted state stay as they were (#2618)", () =>
        {
            Assert(Usage(5).IsLow, "5 credits (2 min) must count as low");

            var plenty = Usage(20);
            Assert(plenty.MinutesRemaining == 11, $"20 credits should be 11 minutes, got {plenty.MinutesRemaining}");
            Assert(!ShowsAnyWarning(plenty), "11 minutes must show no warning");

            foreach (var value in new[] { 0.0, -1.0 })
            {
                var empty = Usage(value);
                Assert(empty.IsExhausted, $"{value} credits must be exhausted");
                Assert(!empty.IsLow, $"{value} credits must be exhausted, not low");
                Assert(ShowsAnyWarning(empty), $"{value} credits must show the exhausted banner");
            }
        });
    }

    // The /usage payload with the server's own rounding and flooring of minutes.
    private static HyperWhisperCloudCredits Usage(double credits)
    {
        var normalized = Math.Round(credits * 10, MidpointRounding.AwayFromZero) / 10;
        return new HyperWhisperCloudCredits
        {
            CreditsRemaining = normalized,
            MinutesRemaining = (int)Math.Floor(normalized / 1.67),
            CreditsPerMinute = 1.67,
            IsLicensed = true
        };
    }

    // CloudAccountSettingsPage.xaml.cs: CreditsLowWarning / CreditsExhaustedWarning visibility.
    private static bool ShowsAnyWarning(HyperWhisperCloudCredits credits) =>
        (credits.IsLow && !credits.IsExhausted) || credits.IsExhausted;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
