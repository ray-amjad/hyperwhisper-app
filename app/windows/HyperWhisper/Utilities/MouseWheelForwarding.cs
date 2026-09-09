// HANDING A WHEEL TURN TO THE PAGE BEHIND A CONTROL
//
// Two places in this app decide that an inner control should not keep a wheel
// turn: OnboardingStage.BubblesMouseWheel, for a scrollable region that has hit
// its limit, and ComboBoxWheelGuard, for a closed dropdown (issue #493). The
// POLICY differs - when to forward, and which parent to forward to - but the
// mechanics of the re-raise must not, and they were copied.
//
// The three details below are the ones a second copy gets wrong: the bubbling
// event rather than the tunnelling one, the parent as the raise target so the
// forwarded event cannot re-enter the handler that forwarded it, and the
// original control kept as Source so a handler upstream can still tell where
// the turn came from.

using System.Windows;
using System.Windows.Input;

namespace HyperWhisper.Utilities;

internal static class MouseWheelForwarding
{
    /// <summary>
    /// Re-raises <paramref name="e"/> as a bubbling MouseWheel starting at
    /// <paramref name="parent"/>, so whatever scrolls around
    /// <paramref name="source"/> receives it.
    /// </summary>
    internal static void RaiseOnParent(UIElement parent, object source, MouseWheelEventArgs e)
    {
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source
        });
    }
}
