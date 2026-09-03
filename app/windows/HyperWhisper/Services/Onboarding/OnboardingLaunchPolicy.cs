// ONBOARDING LAUNCH POLICY
//
// The one place that decides whether this launch owes the user the first run
// flow. Split out of App so the decision is a pure function the smoke suite can
// pin without booting WPF, the same shape as MicrophonePrivacyService.Evaluate.
//
// The gate is SettingsService.OnboardingPending alone. Two things it is
// deliberately NOT gated on:
//
//   * HYPERWHISPER_WINDOWS_APPDATA_ROOT. That override guards the block above
//     this one in App.OnStartup, which registers a MACHINE WIDE Run key through
//     StartupService, i.e. a side effect that escapes an isolated profile.
//     Onboarding has no such escape: everything it decides is written inside
//     AppDataRoot. Guarding on it would also break the only end to end
//     verification there is, because a scratch profile is precisely how a fresh
//     OnboardingPending == true is produced (SettingsService.ApplyDefaults seeds
//     it from !_settingsFileExists, and a scratch profile has no settings file).
//
//   * A command line switch. SingleInstanceGuard.TryAcquire() runs at
//     App.OnStartup's first statement and kills the second instance before
//     e.Args is ever inspected, so a --onboarding flag would silently do nothing
//     whenever the app was already running. An environment variable is read from
//     the process the decision actually runs in.
//
// The one opt out is HYPERWHISPER_WINDOWS_SKIP_ONBOARDING=1, for a harness that
// boots the real App and must not hit a modal window it cannot dismiss.

namespace HyperWhisper.Services.Onboarding;

public static class OnboardingLaunchPolicy
{
    /// <summary>
    /// Set to "1" to suppress the first run flow for this process, whatever
    /// OnboardingPending says. Read once, at startup.
    /// </summary>
    public const string SkipEnvironmentVariable = "HYPERWHISPER_WINDOWS_SKIP_ONBOARDING";

    /// <summary>
    /// The live decision: the persisted flag, minus the explicit opt out.
    /// </summary>
    public static bool ShouldShowOnboarding() =>
        ShouldShowOnboarding(
            SettingsService.Instance.OnboardingPending,
            Environment.GetEnvironmentVariable(SkipEnvironmentVariable));

    /// <summary>
    /// The policy itself, with both inputs supplied. Only "1" opts out: an unset,
    /// empty, "0" or "false" value must NOT suppress the flow, so that a stale
    /// variable left behind in a shell cannot silently disable first run.
    /// </summary>
    internal static bool ShouldShowOnboarding(bool onboardingPending, string? skipValue)
    {
        if (!onboardingPending)
            return false;

        return !string.Equals(skipValue?.Trim(), "1", StringComparison.Ordinal);
    }
}
