using System.Threading;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.ViewModels;

/// <summary>
/// Owns the late hide of the recording overlay after a dictation ends in a
/// success or copied state (#983).
///
/// WHY THIS EXISTS: the dictation flows used to await the 400/500 ms success
/// animation INSIDE the flow, before their <c>finally</c>. The hotkey stayed
/// blocked for that whole wait, so a quick second dictation was dropped. The
/// flows now schedule the hide here and run their teardown at once.
///
/// A scheduled hide must never hide a NEWER recording's overlay. Every schedule
/// takes a new generation, and a new recording calls <see cref="Supersede"/>
/// before it shows its overlay. When the hold ends, the hide runs only if its
/// generation is still the current one. The check and the hide run together on
/// the UI thread (<c>runOnUi</c>), and a supersede always precedes the show it
/// guards, so no thread order lets a stale hide land after a newer show.
/// </summary>
internal sealed class DeferredOverlayHide
{
    /// <summary>How long the green "pasted" tick stays up.</summary>
    internal static readonly TimeSpan SuccessHold = TimeSpan.FromMilliseconds(400);

    /// <summary>How long the blue "copied" state stays up (copied or secure field).</summary>
    internal static readonly TimeSpan CopiedHold = TimeSpan.FromMilliseconds(500);

    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<Action> _runOnUi;
    private int _generation;

    internal DeferredOverlayHide(Func<TimeSpan, Task>? delay = null, Action<Action>? runOnUi = null)
    {
        _delay = delay ?? (hold => Task.Delay(hold));
        _runOnUi = runOnUi ?? RunOnWpfDispatcher;
    }

    /// <summary>
    /// How long the overlay holds the end state <paramref name="result"/> shows,
    /// or null when that result shows no end state and the overlay hides at once.
    /// </summary>
    internal static TimeSpan? HoldFor(SmartPasteResult result) => result switch
    {
        SmartPasteResult.Pasted => SuccessHold,
        SmartPasteResult.CopiedToClipboard or SmartPasteResult.SecureFieldSkipped => CopiedHold,
        _ => null,
    };

    /// <summary>
    /// A new overlay session took the window: no hide scheduled before this call
    /// may fire. Call it BEFORE the new session's show is raised.
    /// </summary>
    internal void Supersede() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Runs <paramref name="hide"/> after <paramref name="hold"/>, unless a later
    /// <see cref="Supersede"/> or <see cref="Schedule"/> came first. The returned
    /// task completes with true when the hide ran; callers on the dictation path
    /// discard it, and it never faults.
    /// </summary>
    internal Task<bool> Schedule(TimeSpan hold, Action hide)
    {
        ArgumentNullException.ThrowIfNull(hide);
        var generation = Interlocked.Increment(ref _generation);
        return HideAfterAsync(generation, hold, hide);
    }

    private async Task<bool> HideAfterAsync(int generation, TimeSpan hold, Action hide)
    {
        try
        {
            // No ConfigureAwait(false): when the flow schedules from the UI thread
            // the continuation comes back to it, and _runOnUi then runs inline.
            await _delay(hold);

            var hid = false;
            _runOnUi(() =>
            {
                if (Volatile.Read(ref _generation) != generation)
                {
                    LoggingService.Debug("DeferredOverlayHide: a newer overlay session owns the window; skipping the late hide");
                    return;
                }

                hide();
                hid = true;
            });
            return hid;
        }
        catch (Exception ex)
        {
            // Discarded by the caller, so a fault here would go unobserved. A hide
            // that fails (for example during shutdown) is not worth a crash.
            LoggingService.Warn($"DeferredOverlayHide: the late overlay hide failed: {ex.Message}");
            return false;
        }
    }

    private static void RunOnWpfDispatcher(Action action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
            return;

        dispatcher.Invoke(action);
    }
}
