// TEXT FIELD CONTEXT HELPER
//
// UIA-based probe for the focused text field's cursor context.
// Used by Autocapitalize Insert to decide whether the caret is at the start
// of a sentence (leave text alone) or mid-sentence (lowercase first letter).
//
// Mirrors macOS AccessibilityHelper.cursorContextOfFocusedElement().
// On any UIA failure or unsupported app, returns Unknown so the caller
// passes the text through unchanged.

using System;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using HyperWhisper.Services;

namespace HyperWhisper.Utilities;

public static class TextFieldContextHelper
{
    /// <summary>
    /// Sentence-terminal characters. If the last non-whitespace character
    /// before the caret is one of these, the caret is treated as
    /// start-of-sentence.
    /// </summary>
    private static readonly char[] SentenceTerminators =
        { '.', '!', '?', '…', '¡', '¿', ';', '\n', '\r' };

    /// <summary>
    /// Probe the focused text element for cursor context.
    /// Runs UIA calls on the WPF dispatcher with a 200ms timeout (UIA can
    /// hang on misbehaving apps; reuse the SmartPasteService pattern).
    ///
    /// Returns Unknown on any failure: no dispatcher, no focused element,
    /// non-text element, no TextPattern, or read failure. Callers should
    /// treat Unknown as "leave the text alone".
    /// </summary>
    public static TextFieldContext GetFocusedElementContext()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return TextFieldContext.Unknown;

            // Run the UIA probe on the dispatcher with a 200ms cap; matches the
            // pattern in SmartPasteService.DetectFocusedField().
            var result = dispatcher.Invoke(
                new Func<TextFieldContext>(ProbeFocusedElement),
                TimeSpan.FromMilliseconds(200));

            return (TextFieldContext)(result ?? TextFieldContext.Unknown);
        }
        catch (TimeoutException)
        {
            LoggingService.Debug("TextFieldContextHelper: UIA call timed out (200ms)");
            return TextFieldContext.Unknown;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"TextFieldContextHelper: UIA read failed: {ex.Message}");
            return TextFieldContext.Unknown;
        }
    }

    private static TextFieldContext ProbeFocusedElement()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                LoggingService.Debug("TextFieldContextHelper: No focused element");
                return TextFieldContext.Unknown;
            }

            var controlType = focused.Current.ControlType;
            bool isTextField = controlType.Id == ControlType.Edit.Id ||
                               controlType.Id == ControlType.Document.Id;
            if (!isTextField)
            {
                LoggingService.Debug($"TextFieldContextHelper: Focused element is not a text field (controlType={controlType.ProgrammaticName})");
                return TextFieldContext.Unknown;
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) ||
                patternObj is not TextPattern textPattern)
            {
                LoggingService.Debug("TextFieldContextHelper: TextPattern unavailable on focused element");
                return TextFieldContext.Unknown;
            }

            var selections = textPattern.GetSelection();
            if (selections == null || selections.Length == 0)
            {
                LoggingService.Debug("TextFieldContextHelper: No selection range available");
                return TextFieldContext.Unknown;
            }

            var caret = selections[0];
            var probeRange = caret.Clone();
            // Collapse range to its Start endpoint (insertion-point semantics:
            // if there's a real selection, paste replaces it, and the effective
            // caret is the selection's start).
            probeRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, probeRange, TextPatternRangeEndpoint.Start);
            // Move start backward by up to 64 characters; the end stays at the
            // caret. We only need the last non-whitespace character before the
            // caret, so 64 chars is plenty of slack for trailing whitespace.
            probeRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -64);

            var preceding = probeRange.GetText(-1) ?? string.Empty;

            LoggingService.Debug($"TextFieldContextHelper: UIA accessible (preceding length={preceding.Length})");
            return ClassifyPreceding(preceding);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"TextFieldContextHelper: UIA probe failed: {ex.Message}");
            return TextFieldContext.Unknown;
        }
    }

    /// <summary>
    /// Pure helper exposed for testability. Walks back over whitespace; the
    /// first non-whitespace char decides:
    /// - sentence terminator -> StartOfSentence
    /// - any other char       -> MidSentence
    /// - only whitespace      -> StartOfSentence
    /// </summary>
    public static TextFieldContext ClassifyPreceding(string preceding)
    {
        if (string.IsNullOrEmpty(preceding)) return TextFieldContext.StartOfSentence;

        for (int i = preceding.Length - 1; i >= 0; i--)
        {
            char c = preceding[i];
            if (char.IsWhiteSpace(c)) continue;
            if (Array.IndexOf(SentenceTerminators, c) >= 0)
            {
                return TextFieldContext.StartOfSentence;
            }
            return TextFieldContext.MidSentence;
        }
        return TextFieldContext.StartOfSentence;
    }
}
