// ONBOARDING STEP MACHINE
//
// The eight first-run steps, in order. Mirrors
// app/macos/hyperwhisper/Views/Onboarding/OnboardingFlowModel.swift.
//
// The raw values are load-bearing, not decorative:
//   - Advance() is step + 1 and Back() is step - 1, so the order IS the machine.
//   - Count is the progress hairline's segment count.
// Renumbering a case renumbers the flow.

namespace HyperWhisper.ViewModels.Onboarding;

/// <summary>
/// The eight onboarding steps, in order. Values are stable so a step can be
/// compared, persisted, or reported without a lookup table.
/// </summary>
public enum OnboardingStep
{
    Welcome = 0,
    Permissions = 1,
    Source = 2,
    Configure = 3,
    Setup = 4,
    Microphone = 5,
    TryIt = 6,
    Done = 7
}

/// <summary>
/// Helpers over <see cref="OnboardingStep"/>. The count lives here rather than
/// as a magic 8 in the progress control.
/// </summary>
public static class OnboardingSteps
{
    /// <summary>Number of steps, i.e. the number of progress segments.</summary>
    public const int Count = 8;

    /// <summary>The first step.</summary>
    public const OnboardingStep First = OnboardingStep.Welcome;

    /// <summary>The last step.</summary>
    public const OnboardingStep Last = OnboardingStep.Done;
}
