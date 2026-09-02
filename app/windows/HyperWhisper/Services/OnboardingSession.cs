// ONBOARDING SESSION
//
// Single source of truth for "the first run window owns this app right now".
//
// The first cut of the port enforced onboarding's exclusivity at the LEAF SINKS:
// five per item `_isOnboardingOpen` checks on the tray, plus a TextDeliveryGate
// read inside SmartPasteService. That shape leaks. The global toggle shortcut is
// a process wide WH_KEYBOARD_LL hook, so WPF modality cannot stop it, and
// MainViewModel had no onboarding check at all: pressing the hotkey behind the
// modal opened a second recorder on the microphone the level meter was already
// using, spent HyperWhisper Cloud credits, and wrote a History row with the half
// staged Mode. Only the final paste was swallowed, silently.
//
// So the flag moved to the ENTRY POINTS. This static is set once, by the window
// that owns the modal, and is read by:
//
//   - MainViewModel.StartRecordingAsync and StartStreamingRecordingAsync, which
//     is where the hotkey, push to talk and the tray all converge. A new hotkey
//     variant or a new tray item inherits the guard instead of having to
//     remember it.
//   - MainWindow's tray item enablement, so a disabled item also LOOKS disabled.
//
// ROUND 2 WIDENED "ENTRY POINT" FROM "STARTS A RECORDING" TO "WRITES STATE THE
// FLOW STAGES OR RESTORES". The flow stages three facts - the default Mode row
// and the active Mode selection, the provider API keys, and the input device -
// and a writer that moves any of them behind the modal is silently discarded by
// Complete() or Rollback(). The census found four classes of writer, and each
// now asks THIS static at the ONE funnel its class converges on, rather than
// growing a fourth and fifth per-site check:
//
//   - The active Mode: MainViewModel.TrySelectMode, which the changeMode global
//     shortcut (via CycleMode) and the tray's Mode submenu both go through.
//   - The input device: MainViewModel.TrySelectAudioDevice, which the tray's
//     Microphone submenu goes through.
//   - The Local API: one middleware in LocalApiServer refuses every mutating
//     (non-GET) request, so POST /modes, PATCH /modes/{id}, DELETE /modes/{id},
//     POST /transcribe and POST /post-process are all covered, and so is any
//     endpoint added later.
//   - The reverse direction: MainWindow.ShowOnboarding refuses to OPEN over a
//     dictation that is already in flight, because raising the delivery gate
//     under a completing transcription drops it on an empty Failed arm.
//
// It also drives TextDeliveryGate, because "onboarding is up" and "text may not
// be delivered into another app" are the same fact on Windows and were being
// written twice. The gate stays the belt and braces backstop for anything
// already in flight when the window opened.
//
// Backed by Interlocked for the same reason the gate is: the capture thread and
// the streaming callbacks both read it off the UI thread.

namespace HyperWhisper.Services;

public static class OnboardingSession
{
    private static int _active;

    /// <summary>
    /// True for exactly as long as the onboarding window is up. Safe to read from
    /// any thread.
    /// </summary>
    public static bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>
    /// Open or close the session. Raising it also raises
    /// <see cref="TextDeliveryGate"/>, so the two can never disagree.
    /// </summary>
    public static void SetActive(bool value)
    {
        var previous = Interlocked.Exchange(ref _active, value ? 1 : 0);
        TextDeliveryGate.SetSuppressed(value);

        if (previous != (value ? 1 : 0))
        {
            LoggingService.Info($"OnboardingSession: {(value ? "opened" : "closed")}");
        }
    }

    /// <summary>
    /// The one question every writer of staged state asks: may this change go
    /// through right now?
    /// </summary>
    /// <param name="context">
    /// What was refused, for the log. It is the only reason this is a method
    /// rather than a second read of <see cref="IsActive"/>: a write that is
    /// silently dropped is indistinguishable from a write that never arrived,
    /// and the first cut of the guard left exactly that hole.
    /// </param>
    /// <returns>True when the caller must NOT perform the write.</returns>
    public static bool BlocksStateChange(string context)
    {
        if (!IsActive)
        {
            return false;
        }

        LoggingService.Info($"OnboardingSession: refused '{context}' while the first-run window is open");
        return true;
    }
}
