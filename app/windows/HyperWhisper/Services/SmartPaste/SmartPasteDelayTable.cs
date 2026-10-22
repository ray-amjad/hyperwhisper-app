using System;
using System.Collections.Generic;

// Alias WPF clipboard types to avoid ambiguity with WinForms
using DataFormats = System.Windows.DataFormats;

namespace HyperWhisper.Services;

/// <summary>
/// The static data SmartPasteService and ClipboardBackup branch on: the
/// browser and Electron process names that get the Chromium paste path, and
/// the clipboard formats a snapshot preserves. Split out of SmartPasteService
/// (#862); the values are unchanged.
/// </summary>
internal static class SmartPasteDelayTable
{
    // =========================================================================
    // PROCESS-SPECIFIC PASTE DELAYS
    // =========================================================================

    /// <summary>
    /// Browser process names — these get longer delays and skip UIA focus detection.
    /// UIA is unreliable inside web content areas.
    /// </summary>
    internal static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    /// <summary>
    /// Electron app process names — need longer delays and skip UIA (unreliable in Chromium).
    /// Treated similarly to browsers: Chromium's multi-process architecture makes
    /// SetForegroundWindow unreliable and UIA focus detection flaky.
    /// </summary>
    internal static readonly HashSet<string> ElectronProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Cursor", "Windsurf", "Simplenote"
    };

    // =========================================================================
    // CLIPBOARD FORMAT SUPPORT
    // =========================================================================

    /// <summary>
    /// List of clipboard formats we attempt to preserve, in priority order.
    /// These cover the most common clipboard content types:
    /// - Text formats: Unicode, RTF, HTML
    /// - Image formats: Bitmap, PNG, JPEG
    /// - File formats: FileDrop (file paths)
    /// - Other: Custom formats are also captured dynamically
    /// </summary>
    internal static readonly string[] SupportedFormats = new[]
    {
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.Rtf,
        DataFormats.Html,
        DataFormats.Bitmap,
        DataFormats.FileDrop,
        DataFormats.CommaSeparatedValue,
    };
}
