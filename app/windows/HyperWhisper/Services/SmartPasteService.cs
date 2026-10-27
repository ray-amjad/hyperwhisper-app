using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using HyperWhisper.Models;
using HyperWhisper.Services.Platform;
using static HyperWhisper.Services.SmartPasteDelayTable;
using static HyperWhisper.Services.SmartPasteNativeMethods;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services;

/// <summary>
/// SMART PASTE SERVICE
///
/// Pastes transcription text into the previously focused application:
/// 1. Captures the foreground window before recording starts
/// 2. Captures clipboard content for later restoration
/// 3. After transcription, copies text to clipboard
/// 4. Reactivates the captured window
/// 5. Simulates Ctrl+V to paste, once the user's held Shift/Alt/Win keys are up
///    (awaited and bounded in SmartPasteAsync, released if still down; #1495)
/// 6. Schedules restoration of original clipboard content
///
/// CLIPBOARD PRESERVATION (matches macOS behavior):
/// - Captures ALL clipboard formats at recording start (text, images, RTF, HTML, files, etc.)
/// - Stores raw data instead of objects (Windows clipboard objects can't be reused after clear)
/// - Restores original clipboard after configurable delay (default 10 seconds)
/// - Cancels pending restoration when new recording starts
///
/// USES:
/// - SyntheticKeyboard (SendInput, stamped with the HyperWhisper marker) for Ctrl+V
/// - Win32 SetForegroundWindow to reactivate previous app
/// - AllowSetForegroundWindow to bypass foreground restrictions
///
/// FOCUS-AWARE PASTE:
/// - Captures focused child HWND via GetGUIThreadInfo at recording start
/// - Restores focus using AttachThreadInput + SetFocus for cross-process child focus
/// - Polls for focus readiness instead of fixed delays (max 300ms for Chromium, 150ms native)
/// - Detects password fields via UI Automation and skips paste (text stays in clipboard)
/// - Browsers/Electron skip non-text-field UIA gating, but still run secure-field checks
/// - Non-text-field focus still attempts paste (Ctrl+V is harmless, text stays in clipboard)
/// </summary>
public class SmartPasteService : IDisposable, PlatformContracts.ITextInjectionService
{
    // =========================================================================
    // INSTANCE FIELDS
    // =========================================================================

    private IntPtr _previousForegroundWindow;
    private uint _previousThreadId;
    private IntPtr _previousFocusedChild;

    // Clipboard snapshot and restore: the saved formats, the restore timer, the
    // clipboard lock and the #1496/#1583/#1584 sequence-number marks (#862).
    private readonly ClipboardBackup _clipboard;

    /// <summary>Milliseconds on a monotonic clock. A seam so a test can move time past the restore delay.</summary>
    internal Func<long> TickCountMs { get; set; } = static () => Environment.TickCount64;

    /// <summary>
    /// Reads whether a virtual key is down, for the held-modifier guard (#1495).
    /// GetAsyncKeyState in production; the smoke suite swaps in a fake so it can
    /// drive the guard without a keyboard.
    /// </summary>
    internal Func<int, bool> IsKeyDown { get; set; } = static vk => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>The awaited pause between key-state polls. A seam for the same reason.</summary>
    internal Func<TimeSpan, CancellationToken, Task> ModifierPollDelay { get; set; } =
        static (interval, token) => Task.Delay(interval, token);

    /// <summary>Whether a Shift, Alt or Win key is down now, i.e. a Ctrl+V sent now would not be plain (#1495).</summary>
    internal bool AnyPasteModifierHeld => PasteModifierGuard.HeldConflictingModifiers(IsKeyDown).Count > 0;

    /// <summary>The longest <see cref="WaitForPasteModifiersReleasedAsync"/> waits.</summary>
    internal TimeSpan ModifierReleaseTimeout { get; set; } = PasteModifierGuard.DefaultReleaseTimeout;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public SmartPasteService()
    {
        SendModifierKeyEvent = SendModifierKeyEventWithSendInput;
        _clipboard = new ClipboardBackup(() => TickCountMs());
    }

    void PlatformContracts.ITextInjectionService.CaptureTarget()
        => CaptureForegroundWindow();

    bool PlatformContracts.ITextInjectionService.IsCapturedTargetAvailable
        => IsCapturedTargetAvailable();

    void PlatformContracts.ITextInjectionService.StartSession()
        => StartRecordingSession();

    void PlatformContracts.ITextInjectionService.EndSession()
        => EndRecordingSession();

    void PlatformContracts.ITextInjectionService.CancelPendingClipboardRestore()
        => CancelPendingRestore();

    void PlatformContracts.ITextInjectionService.ScheduleClipboardRestore(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Restore delay cannot be negative.");
        }

        _clipboard.ScheduleClipboardRestore(delay);
    }

    ValueTask<PlatformContracts.PlatformResult>
        PlatformContracts.ITextInjectionService.RestoreClipboardImmediatelyAsync(
            CancellationToken cancellationToken)
    {
        if (_clipboard.IsDisposed)
        {
            return ValueTask.FromResult(PlatformContracts.PlatformResult.Failure(
                "text_injection.disposed",
                "The Windows text injection service has been disposed."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PlatformContracts.PlatformResult.Failure(
                "text_injection.cancelled",
                "Clipboard restoration was cancelled."));
        }

        return ValueTask.FromResult(_clipboard.TryRestoreClipboardImmediately()
            ? PlatformContracts.PlatformResult.Success()
            : PlatformContracts.PlatformResult.Failure(
                "text_injection.clipboard_restore_failed",
                "Windows could not restore the saved clipboard content."));
    }

    ValueTask<PlatformContracts.PlatformResult>
        PlatformContracts.ITextInjectionService.CopyToClipboardAsync(
            string text,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ValueTask.FromResult(PlatformContracts.PlatformResult.Failure(
                "text_injection.empty_text",
                "There is no text to copy."));
        }

        if (_clipboard.IsDisposed)
        {
            return ValueTask.FromResult(PlatformContracts.PlatformResult.Failure(
                "text_injection.disposed",
                "The Windows text injection service has been disposed."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PlatformContracts.PlatformResult.Failure(
                "text_injection.cancelled",
                "Clipboard copy was cancelled."));
        }

        return ValueTask.FromResult(CopyToClipboard(text)
            ? PlatformContracts.PlatformResult.Success()
            : PlatformContracts.PlatformResult.Failure(
                "text_injection.clipboard_failed",
                "Windows could not copy the transcript to the clipboard."));
    }

    ValueTask<PlatformContracts.TextInjectionOutcome>
        PlatformContracts.ITextInjectionService.InjectTranscriptAsync(
            string text,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || _clipboard.IsDisposed || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PlatformContracts.TextInjectionOutcome.Failed);
        }

        return InjectTranscriptAfterModifierReleaseAsync(text, cancellationToken);
    }

    private async ValueTask<PlatformContracts.TextInjectionOutcome> InjectTranscriptAfterModifierReleaseAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var result = await SmartPasteAsync(text, cancellationToken);
        return WindowsTextInjectionMapper.ToPlatform(result);
    }

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Captures the current foreground window.
    /// Call this BEFORE showing recording overlay or any UI.
    /// </summary>
    public void CaptureForegroundWindow()
    {
        _previousForegroundWindow = GetForegroundWindow();
        _previousThreadId = 0;
        _previousFocusedChild = IntPtr.Zero;

        if (_previousForegroundWindow != IntPtr.Zero)
        {
            // Get the thread that owns the foreground window
            _previousThreadId = GetWindowThreadProcessId(_previousForegroundWindow, out _);

            // Get the focused child control within that thread's input queue
            if (_previousThreadId != 0)
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(_previousThreadId, ref info))
                {
                    _previousFocusedChild = info.hwndFocus;
                    LoggingService.Debug($"SmartPasteService: Captured foreground window: {_previousForegroundWindow}, thread: {_previousThreadId}, focusedChild: {_previousFocusedChild}");
                }
                else
                {
                    LoggingService.Debug($"SmartPasteService: Captured foreground window: {_previousForegroundWindow}, thread: {_previousThreadId}, GetGUIThreadInfo failed");
                }
            }
        }
    }

    /// <summary>
    /// Returns whether the captured paste target still exists and is not minimized.
    /// Streaming uses this to stop automatically if the pinned target disappears.
    /// </summary>
    public bool IsCapturedTargetAvailable()
    {
        return _previousForegroundWindow != IntPtr.Zero &&
               IsWindow(_previousForegroundWindow) &&
               !IsIconic(_previousForegroundWindow);
    }

    /// <inheritdoc cref="ClipboardBackup.StartRecordingSession"/>
    public void StartRecordingSession()
        => _clipboard.StartRecordingSession();

    /// <inheritdoc cref="ClipboardBackup.EndRecordingSession"/>
    public void EndRecordingSession()
        => _clipboard.EndRecordingSession();

    /// <inheritdoc cref="ClipboardBackup.CancelPendingRestore"/>
    public void CancelPendingRestore()
        => _clipboard.CancelPendingRestore();

    public bool HasPendingClipboardRestore => _clipboard.HasPendingClipboardRestore;

    /// <inheritdoc cref="ClipboardBackup.ScheduleClipboardRestore(TimeSpan)"/>
    public void ScheduleClipboardRestore()
        => _clipboard.ScheduleClipboardRestore(
            TimeSpan.FromSeconds(SettingsService.Instance.ClipboardRestoreDelaySeconds));

    /// <summary>
    /// Restores saved clipboard content immediately, used during app shutdown before disposal.
    /// </summary>
    public void RestoreClipboardImmediately()
        => _clipboard.TryRestoreClipboardImmediately();

    /// <summary>
    /// Copies text to clipboard without attempting to paste.
    /// Use this when auto-paste is disabled.
    /// </summary>
    /// <returns>True if clipboard copy succeeded.</returns>
    public bool CopyToClipboard(string text)
    {
        // DIAGNOSTICS: metadata for this attempt, reported at every exit. Holds
        // counts, flags and durations only — never the transcript.
        var attempt = NewPasteAttempt(text, autoPasteAttempted: false);

        // Suppression is checked HERE, at the sink, not at each of the four call
        // sites that lead here. See TextDeliveryGate.
        if (TextDeliveryGate.IsSuppressed)
        {
            LoggingService.Info("SmartPasteService: Clipboard copy suppressed by TextDeliveryGate");
            return false;
        }

        if (string.IsNullOrEmpty(text))
        {
            LoggingService.Warn("SmartPasteService: Empty text, nothing to copy");
            SmartPasteDiagnostics.Report(PasteOutcome.EmptyText, attempt);
            return false;
        }

        try
        {
            ClipboardBackup.SetClipboardText(text);
            _clipboard.MarkClipboardHoldsOwnTranscript();
            LoggingService.Info("SmartPasteService: Text copied to clipboard (auto-paste disabled)");
            SmartPasteDiagnostics.Report(PasteOutcome.ClipboardOnly, attempt);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error("SmartPasteService: Failed to set clipboard", ex);
            SmartPasteDiagnostics.Report(PasteOutcome.ClipboardSetFailed, attempt, ex);
            return false;
        }
    }

    /// <summary>
    /// Seeds the diagnostics record for one delivery attempt from the target
    /// captured at recording start.
    ///
    /// The window state is read here rather than after the paste: on the failure
    /// paths the target is exactly what a triager needs, and reading it later
    /// would describe the desktop after the attempt instead of before it.
    /// </summary>
    private PasteAttempt NewPasteAttempt(string text, bool autoPasteAttempted)
    {
        var attempt = new PasteAttempt
        {
            AutoPasteAttempted = autoPasteAttempted,
            CharacterCount = text?.Length ?? 0,
            HadCapturedTarget = _previousForegroundWindow != IntPtr.Zero,
            HideFromClipboardHistory = SettingsService.Instance.HideFromClipboardHistory
        };

        if (attempt.HadCapturedTarget)
        {
            attempt.TargetWindowAlive = IsWindow(_previousForegroundWindow);
            attempt.TargetWindowMinimized = attempt.TargetWindowAlive && IsIconic(_previousForegroundWindow);
        }

        return attempt;
    }

    /// <summary>
    /// The outcome of the last SmartPaste call, or null when TextDeliveryGate
    /// refused it (that exit reports nothing). SmartPasteResult.Failed covers
    /// four exits and only one of them lost the text, so a caller that must
    /// tell them apart reads this (#905). The return values stay as they are:
    /// streaming keeps a segment for its fallback only on Failed.
    /// </summary>
    public PasteOutcome? LastSmartPasteOutcome { get; private set; }

    private void ReportSmartPasteOutcome(PasteOutcome outcome, PasteAttempt attempt, Exception? exception = null)
    {
        LastSmartPasteOutcome = outcome;
        SmartPasteDiagnostics.Report(outcome, attempt, exception);
    }

    /// <summary>
    /// <see cref="SmartPaste"/>, after first waiting (asynchronously, bounded) for
    /// the user to let go of any Shift, Alt or Win key they are still holding
    /// (#1495). Every paste that can follow a shortcut press goes through here: the
    /// batch stop (toggle chord, push-to-talk release, tray, duration limit) and
    /// the shared-core text delivery.
    ///
    /// Must be awaited from the UI thread, as SmartPaste is today: the clipboard
    /// write needs the STA thread, and the awaits here resume on it.
    /// </summary>
    public async Task<SmartPasteResult> SmartPasteAsync(string text, CancellationToken cancellationToken = default)
    {
        // Nothing will be typed on these exits, so there is nothing to wait for.
        if (!string.IsNullOrEmpty(text) && !TextDeliveryGate.IsSuppressed)
        {
            try
            {
                await WaitForPasteModifiersReleasedAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LoggingService.Info("SmartPasteService: Paste cancelled while waiting for held modifiers to be released");
                return SmartPasteResult.Failed;
            }
        }

        return SmartPaste(text);
    }

    /// <summary>
    /// Waits, without blocking the thread, until no Shift, Alt or Win key is down,
    /// for at most <see cref="ModifierReleaseTimeout"/>. Returns at once when none
    /// is down. Logs how long it waited, and when it gave up. Giving up is not a
    /// failure: SmartPaste releases whatever is still down right before its Ctrl+V.
    ///
    /// Never replace the awaited delay with Thread.Sleep. The paste runs on the UI
    /// thread, which is also where both WH_KEYBOARD_LL hooks run: a sleeping UI
    /// thread cannot process the very key-up this waits for, and it freezes the
    /// whole desktop keyboard until Windows times the hook out.
    /// </summary>
    public async Task WaitForPasteModifiersReleasedAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var wait = await PasteModifierGuard.WaitForReleaseAsync(
            IsKeyDown,
            ModifierReleaseTimeout,
            PasteModifierGuard.DefaultPollInterval,
            ModifierPollDelay,
            cancellationToken);
        stopwatch.Stop();

        if (!wait.WasHeld)
            return;

        var heldKeys = PasteModifierGuard.Describe(wait.InitiallyHeld);
        if (wait.Released)
        {
            LoggingService.Info(
                $"SmartPasteService: Waited {stopwatch.ElapsedMilliseconds}ms for held {heldKeys} to be released before Ctrl+V");
        }
        else
        {
            LoggingService.Warn(
                $"SmartPasteService: Gave up waiting for held modifiers after {stopwatch.ElapsedMilliseconds}ms " +
                $"(held at start: {heldKeys}; still held: {PasteModifierGuard.Describe(wait.StillHeld)}); " +
                "they will be released before Ctrl+V");
        }
    }

    /// <summary>
    /// Pastes text by copying to clipboard, reactivating the previous window,
    /// and simulating Ctrl+V. Uses focus-aware detection for password field safety
    /// and app-specific paste delays.
    ///
    /// A caller that can await should call <see cref="SmartPasteAsync"/> instead,
    /// which first waits for the user's held modifiers to come up. This method does
    /// not wait; it releases any Shift, Alt or Win key that is still down right
    /// before the Ctrl+V, so the target always receives a plain Ctrl+V (#1495).
    /// </summary>
    /// <returns>SmartPasteResult indicating what happened</returns>
    public SmartPasteResult SmartPaste(string text)
    {
        LastSmartPasteOutcome = null;

        // DIAGNOSTICS: metadata for this attempt, reported at every exit so the
        // last step of the record → transcribe → paste flow is visible in
        // production. Holds counts, flags and durations only — never the text.
        var attempt = NewPasteAttempt(text, autoPasteAttempted: true);

        // Suppression is checked HERE, at the sink, not at each of the four call
        // sites that lead here. Without it a global hotkey pressed while the
        // first-run window is open would paste a test sentence into whatever the
        // user had focused AND clobber their clipboard. Failed is the honest
        // answer and it is silent: this exit records no outcome, so
        // LastSmartPasteOutcome stays null and the batch Failed arm, which
        // reports only ClipboardSetFailed (#905), shows nothing. See
        // TextDeliveryGate.
        if (TextDeliveryGate.IsSuppressed)
        {
            LoggingService.Info("SmartPasteService: Paste suppressed by TextDeliveryGate");
            return SmartPasteResult.Failed;
        }

        if (string.IsNullOrEmpty(text))
        {
            LoggingService.Warn("SmartPasteService: Empty text, nothing to paste");
            ReportSmartPasteOutcome(PasteOutcome.EmptyText, attempt);
            return SmartPasteResult.Failed;
        }

        LoggingService.Info("SmartPasteService: Starting smart paste...");

        // Step 1: Copy text to clipboard (always do this as fallback)
        try
        {
            ClipboardBackup.SetClipboardText(text);
            _clipboard.MarkClipboardHoldsOwnTranscript();
            LoggingService.Debug("SmartPasteService: Text copied to clipboard");
        }
        catch (Exception ex)
        {
            LoggingService.Error("SmartPasteService: Failed to set clipboard", ex);
            ReportSmartPasteOutcome(PasteOutcome.ClipboardSetFailed, attempt, ex);
            return SmartPasteResult.Failed;
        }

        // Step 2: Reactivate previous window
        if (_previousForegroundWindow == IntPtr.Zero)
        {
            LoggingService.Warn("SmartPasteService: No previous window captured, text is in clipboard only");
            ReportSmartPasteOutcome(PasteOutcome.NoTargetWindow, attempt);
            return SmartPasteResult.CopiedToClipboard;
        }

        var processName = GetProcessNameFromWindow(_previousForegroundWindow);
        bool isBrowser = !string.IsNullOrEmpty(processName) && BrowserProcessNames.Contains(processName);
        bool isElectron = !string.IsNullOrEmpty(processName) && ElectronProcessNames.Contains(processName);
        bool isChromium = isBrowser || isElectron;

        attempt.TargetProcessName = string.IsNullOrEmpty(processName) ? null : processName;
        attempt.TargetAppKind = ClassifyPasteTarget(processName, isBrowser, isElectron);

        LoggingService.Debug($"SmartPasteService: Process='{processName}', isChromium={isChromium}");

        // Step 3: Restore focus to the target window + child control
        var focus = RestoreFocus(isChromium);
        attempt.FocusReady = focus.Ready;
        attempt.FocusWaitMs = focus.WaitedMs;

        var (_, isFocusedPassword) = DetectFocusedField();
        if (isFocusedPassword)
        {
            LoggingService.Info("SmartPasteService: Password field detected - skipping paste, text in clipboard");
            ReportSmartPasteOutcome(PasteOutcome.SecureFieldSkipped, attempt);
            return SmartPasteResult.SecureFieldSkipped;
        }

        // Step 4: Browser and Electron fast-path skips text-field detection only.
        // UIA is unreliable inside Chromium content areas (both browsers and Electron apps).
        // Secure-field detection above still runs for Chromium targets before this fast path.
        if (!isChromium)
        {
            // Step 5: Focus detection for native apps only
            var (isTextField, isPassword) = DetectFocusedField();
            if (isPassword)
            {
                LoggingService.Info("SmartPasteService: Password field detected — skipping paste, text in clipboard");
                ReportSmartPasteOutcome(PasteOutcome.SecureFieldSkipped, attempt);
                return SmartPasteResult.SecureFieldSkipped;
            }

            if (!isTextField)
            {
                LoggingService.Debug("SmartPasteService: No text field detected, attempting paste anyway");
            }
        }

        // Step 6: Simulate Ctrl+V to paste
        try
        {
            // A Shift, Alt or Win key still down here would turn the Ctrl+V into
            // Ctrl+Alt+V or Ctrl+Shift+V, which pastes nothing in most targets
            // while this method reports Pasted (#1495). Release it first.
            ReleaseHeldModifiersBeforePaste();

            // Stamped with HyperWhisper's marker, so push-to-talk bound to Ctrl
            // does not read this generic VK_CONTROL as the user's press (#1851).
            SyntheticKeyboard.Send(SyntheticKeyboard.CtrlV);

            LoggingService.Info("SmartPasteService: Sent Ctrl+V to paste text");
            ReportSmartPasteOutcome(PasteOutcome.Pasted, attempt);
            return SmartPasteResult.Pasted;
        }
        catch (Exception ex)
        {
            LoggingService.Error("SmartPasteService: Failed to simulate Ctrl+V", ex);
            ReportSmartPasteOutcome(PasteOutcome.KeystrokeFailed, attempt, ex);
            return SmartPasteResult.Failed;
        }
    }

    /// <summary>
    /// Sends one synthetic key event of a release plan. SyntheticKeyboard in
    /// production (it sets the extended-key flag for Right Alt and stamps the
    /// HyperWhisper marker push-to-talk ignores); the smoke suite records the
    /// events instead of typing them.
    /// </summary>
    internal Action<PasteKeyEvent> SendModifierKeyEvent { get; set; }

    private static void SendModifierKeyEventWithSendInput(PasteKeyEvent keyEvent)
        => SyntheticKeyboard.Send(new[] { keyEvent });

    /// <summary>
    /// Releases any Shift, Alt or Win key that is still down, so the Ctrl+V that
    /// follows reaches the target as a plain Ctrl+V (#1495). Normally nothing is
    /// down, because <see cref="SmartPasteAsync"/> waited for it; this covers the
    /// wait giving up, a key pressed after the wait, and the streaming segment
    /// path, which cannot wait without reordering segments.
    ///
    /// When Alt or Win is held, an unassigned key is tapped first so the
    /// synthetic key-up is a chord release: a lone Alt key-up would open the
    /// target's menu bar, and a lone Win key-up would open Start. The keys are not
    /// pressed again afterwards, so their logical state stays up while the user
    /// may still hold them; the user's own key-up later is a no-op for the target.
    /// </summary>
    /// <returns>The events sent; empty when nothing was held.</returns>
    internal IReadOnlyList<PasteKeyEvent> ReleaseHeldModifiersBeforePaste()
    {
        var held = PasteModifierGuard.HeldConflictingModifiers(IsKeyDown);
        var plan = PasteModifierGuard.PlanRelease(held);
        if (plan.Count == 0)
            return plan;

        LoggingService.Warn(
            $"SmartPasteService: {PasteModifierGuard.Describe(held)} still held at Ctrl+V; " +
            $"releasing {(plan.Count > held.Count ? "with a menu-mask key " : string.Empty)}before the paste");

        foreach (var keyEvent in plan)
            SendModifierKeyEvent(keyEvent);

        return plan;
    }

    // =========================================================================
    // FOCUS RESTORATION
    // =========================================================================

    /// <summary>
    /// Restores focus to the previously captured window and child control.
    ///
    /// For Chromium apps (browsers, Electron), SetForegroundWindow alone only activates
    /// the outer window — it does NOT restore keyboard focus to the renderer/text field.
    /// This method uses AttachThreadInput + SetFocus to restore the focused child control,
    /// then polls for readiness instead of using a fixed sleep.
    /// </summary>
    /// <returns>
    /// How the focus poll ended, for diagnostics only. A timed-out poll is the
    /// signal that a paste landed in an app that was not ready for it.
    /// </returns>
    private FocusWaitResult RestoreFocus(bool isChromium)
    {
        var currentForeground = GetForegroundWindow();
        bool alreadyForeground = currentForeground == _previousForegroundWindow;

        if (!alreadyForeground)
        {
            // Target window lost foreground — need to reactivate it
            AllowSetForegroundWindow(ASFW_ANY);

            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = GetWindowThreadProcessId(currentForeground, out _);
            bool attached = false;

            try
            {
                // Attach our input queue to the current foreground thread so we can
                // call SetForegroundWindow reliably
                if (currentThreadId != foregroundThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                BringWindowToTop(_previousForegroundWindow);
                SetForegroundWindow(_previousForegroundWindow);
                LoggingService.Debug("SmartPasteService: Activated target window via AttachThreadInput + SetForegroundWindow");
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }
        else
        {
            LoggingService.Debug("SmartPasteService: Target already foreground, skipping SetForegroundWindow");
        }

        // Restore the focused child control if we captured one
        if (_previousFocusedChild != IntPtr.Zero && IsWindow(_previousFocusedChild))
        {
            uint currentThreadId = GetCurrentThreadId();
            bool attached = false;

            try
            {
                // Attach to the target thread so SetFocus works cross-process
                if (currentThreadId != _previousThreadId && _previousThreadId != 0)
                {
                    attached = AttachThreadInput(currentThreadId, _previousThreadId, true);
                }

                SetFocus(_previousFocusedChild);
                LoggingService.Debug($"SmartPasteService: Restored focus to child {_previousFocusedChild}");
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, _previousThreadId, false);
                }
            }
        }

        // Wait for focus to be ready instead of a fixed sleep
        return WaitForFocusReady(isChromium);
    }

    /// <summary>Outcome of one focus poll. Diagnostics only — nothing branches on it.</summary>
    private readonly record struct FocusWaitResult(bool Ready, int WaitedMs);

    /// <summary>
    /// Polls until the target window is foreground and has keyboard focus,
    /// or until the timeout is reached. Replaces fixed Thread.Sleep delays.
    /// </summary>
    private FocusWaitResult WaitForFocusReady(bool isChromium)
    {
        // Max wait: 300ms for Chromium, 150ms for native apps
        int maxWaitMs = isChromium ? 300 : 150;
        const int pollIntervalMs = 15;
        int waited = 0;

        while (waited < maxWaitMs)
        {
            // Check if foreground window matches
            if (GetForegroundWindow() == _previousForegroundWindow)
            {
                // Check if the target thread has keyboard focus
                if (_previousThreadId != 0)
                {
                    var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                    if (GetGUIThreadInfo(_previousThreadId, ref info) && info.hwndFocus != IntPtr.Zero)
                    {
                        LoggingService.Debug($"SmartPasteService: Focus ready after {waited}ms (focus={info.hwndFocus})");
                        return new FocusWaitResult(Ready: true, WaitedMs: waited);
                    }
                }
                else
                {
                    // No thread ID captured — foreground match is good enough
                    LoggingService.Debug($"SmartPasteService: Foreground match after {waited}ms (no thread info)");
                    return new FocusWaitResult(Ready: true, WaitedMs: waited);
                }
            }

            Thread.Sleep(pollIntervalMs);
            waited += pollIntervalMs;
        }

        LoggingService.Debug($"SmartPasteService: Focus wait timed out after {maxWaitMs}ms, proceeding with paste");
        return new FocusWaitResult(Ready: false, WaitedMs: waited);
    }

    // =========================================================================
    // FOCUS DETECTION HELPERS
    // =========================================================================

    /// <summary>
    /// Resolves the process name from a window handle.
    /// </summary>
    private static string GetProcessNameFromWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SmartPasteService: GetProcessNameFromWindow failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Detects whether the focused UI element is a text field or password field
    /// using UI Automation. Runs on <see cref="UiaProbeHost"/>'s STA thread with
    /// a 200ms timeout.
    ///
    /// NOT on the UI thread (issue #672). SmartPaste runs on the UI thread, and
    /// the UI thread is where both WH_KEYBOARD_LL hooks live — a UIA call that
    /// hangs there freezes the keyboard for the whole desktop, not just for this
    /// app. A Dispatcher.Invoke timeout does not bound a delegate that has
    /// already started, so the probe has to run somewhere it is allowed to hang.
    ///
    /// Returns (isTextField, isPassword). On timeout or any failure returns
    /// (false, false) to fall back to blind paste.
    /// </summary>
    private static (bool isTextField, bool isPassword) DetectFocusedField()
        => UiaProbeHost.Probe(
            "SmartPasteService.DetectFocusedField",
            static () =>
            {
                var focused = AutomationElement.FocusedElement;
                if (focused == null) return (false, false);

                var controlType = focused.Current.ControlType;
                bool isTextField = controlType.Id == ControlType.Edit.Id ||
                                   controlType.Id == ControlType.Document.Id;

                bool isPassword = false;
                try
                {
                    isPassword = (bool)focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty);
                }
                catch
                {
                    // IsPassword not supported for this element
                }

                return (isTextField, isPassword);
            },
            fallback: (false, false));

    // =========================================================================
    // IDISPOSABLE IMPLEMENTATION
    // =========================================================================

    /// <inheritdoc cref="ClipboardBackup.Dispose"/>
    public void Dispose()
        => _clipboard.Dispose();
}
