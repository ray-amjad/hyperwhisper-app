// TEXT DELIVERY GATE
//
// Single source of truth for whether transcribed text may be delivered into
// another application right now. The Windows port of
// app/macos/hyperwhisper/Utilities/TextDeliveryGate.swift.
//
// On Windows every transcript reaches another app through SmartPasteService:
// SmartPaste() writes the clipboard and simulates Ctrl+V, CopyToClipboard()
// writes the clipboard only. Both consult this gate, so suppression is defined
// in exactly ONE place and no CALLER can leak text by forgetting a per call
// guard. That is the fix for the shape the app had before: every call site read
// SettingsService.AutoPasteEnabled for itself and SmartPaste wrote the clipboard
// unconditionally, so a global hotkey pressed while the first run window was
// open pasted a test sentence into whatever the user had focused and clobbered
// their clipboard.
//
// Suppressed for the whole lifetime of the onboarding window, where transcripts
// are shown inline only, regardless of what started the recording (the toggle
// shortcut, the streaming shortcut, or the step's own Record button).
//
// Backed by Interlocked because the streaming callbacks and the NAudio capture
// thread both read it off the UI thread.

namespace HyperWhisper.Services;

public static class TextDeliveryGate
{
    private static int _suppressed;

    /// <summary>
    /// True when delivery into other applications is blocked. Safe to read from
    /// any thread.
    /// </summary>
    public static bool IsSuppressed => Volatile.Read(ref _suppressed) != 0;

    /// <summary>
    /// Update the suppression state. Driven by the onboarding window's lifetime.
    /// </summary>
    public static void SetSuppressed(bool value)
    {
        var previous = Interlocked.Exchange(ref _suppressed, value ? 1 : 0);
        if (previous != (value ? 1 : 0))
        {
            LoggingService.Info($"TextDeliveryGate: suppression {(value ? "enabled" : "cleared")}");
        }
    }
}
