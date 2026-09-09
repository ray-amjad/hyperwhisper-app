// COMBOBOX WHEEL GUARD
//
// WPF's ComboBox moves its own selection on the mouse wheel even when it is
// CLOSED (ComboBox.OnMouseWheel), and marks the event handled either way. Every
// ComboBox in this app sits inside a ScrollViewer, so those two defaults combine
// into a silent data edit (issue #493):
//
//   the pointer rests over a dropdown the user has just picked a value from - it
//   keeps focus after the pick - the user turns the wheel to reach the next
//   section, the page does not move, and the mode's transcription provider is now
//   two steps down the list, with the model and the credit line following it. Save
//   Changes then writes an engine the user never chose.
//
// The rule is registered once for the TYPE, so it covers every dropdown in the
// process rather than the twelve in the mode editor: the Settings pages
// (Streaming, Shortcuts, Sound), the History page and the custom-endpoint window
// have the same trap, and all of them are inside ScrollViewers too.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HyperWhisper.Utilities;

public static class ComboBoxWheelGuard
{
    private static bool _installed;

    /// <summary>
    /// Registers the rule for every <see cref="WpfComboBox"/> in this process.
    /// Called from <c>App</c>'s constructor rather than OnStartup so that any host
    /// of the app assembly gets it - notably the smoke suite, which builds the real
    /// App but never calls Run(). Idempotent.
    /// </summary>
    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;

        EventManager.RegisterClassHandler(
            typeof(WpfComboBox),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    /// <summary>
    /// The class handler itself. It does one thing beyond reading the dropdown's
    /// open state - see <see cref="Decide"/>, which holds the rule and is what the
    /// smoke suite drives, because a console process cannot host an open Popup.
    /// </summary>
    internal static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is WpfComboBox combo)
            Decide(combo, combo.IsDropDownOpen, e);
    }

    /// <summary>
    /// A closed dropdown gives the wheel to whatever is scrolling around it; an
    /// open one keeps it, because scrolling the list it is showing is the point.
    ///
    /// The open state is a PARAMETER rather than a read off <paramref name="combo"/>
    /// so that both halves of that sentence can be tested. ComboBox coerces
    /// IsDropDownOpen back to false while the control is unloaded, and nothing in
    /// the console smoke host is ever loaded, so a test that set the property would
    /// silently be exercising the closed path under the other name.
    /// </summary>
    internal static void Decide(WpfComboBox combo, bool isDropDownOpen, MouseWheelEventArgs e)
    {
        if (e.Handled || isDropDownOpen)
            return;

        // Handled BEFORE anything else, and whether or not there is something to
        // scroll: marking the PREVIEW event handled is what stops WPF promoting it
        // to the bubbling MouseWheel event, and that bubbling event is the one
        // ComboBox.OnMouseWheel reads to change the selection. A dropdown with no
        // scroller around it must still ignore the wheel rather than edit itself.
        e.Handled = true;

        // Forwarded from the PARENT, not from the ComboBox: starting the bubble
        // above the control keeps it out of the path, so the ScrollViewer gets the
        // wheel and this handler cannot see its own event again.
        if (ParentOf(combo) is UIElement parent)
            MouseWheelForwarding.RaiseOnParent(parent, combo, e);
    }

    /// <summary>
    /// The logical parent, falling back to the visual one: a ComboBox that came
    /// from a control template has no logical parent to hand the wheel to.
    /// </summary>
    private static DependencyObject? ParentOf(WpfComboBox combo) =>
        combo.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(combo);
}
