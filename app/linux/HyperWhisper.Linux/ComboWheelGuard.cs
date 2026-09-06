using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace HyperWhisper.Linux;

/// <summary>
/// Stops the mouse wheel from silently changing a ComboBox or NumericUpDown value.
///
/// This is a real behavioural divergence, not a cosmetic one. A closed WPF ComboBox never changes
/// its selection on the wheel -- it only swallows the event, which is why the Windows app carries
/// OnboardingStage.BubblesMouseWheel (OnboardingStage.xaml.cs:55-104) to hand the wheel back to
/// the page. Avalonia's closed ComboBox instead steps SelectedIndex. Both the mode editor and the
/// Settings pages are tall scrolling forms full of combos, so on Linux a scroll whose pointer
/// happened to be over one would quietly rewrite the transcription model, the media-control mode
/// or a shortcut -- a change Windows cannot produce at all.
///
/// The wheel is forwarded to the nearest ancestor ScrollViewer so scrolling still feels normal;
/// only the selection change is suppressed. An OPEN dropdown is left alone, because there the
/// wheel legitimately scrolls the item list on both platforms.
/// </summary>
internal static class ComboWheelGuard
{
    /// <summary>Wheel notches to pixels. Matches Avalonia's own ScrollViewer line-scroll feel.</summary>
    private const double LinesToPixels = 50;

    public static void Attach(TopLevel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // Tunnel, so the handler runs before the ComboBox's own bubbling handler consumes it.
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Source is not Visual source) return;

        var target = source.FindAncestorOfType<ComboBox>(includeSelf: true) as Control
            ?? source.FindAncestorOfType<NumericUpDown>(includeSelf: true);
        if (target is null) return;
        if (target is ComboBox { IsDropDownOpen: true }) return;

        e.Handled = true;
        if (target.FindAncestorOfType<ScrollViewer>() is not { } scroll) return;

        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var y = Math.Clamp(scroll.Offset.Y - (e.Delta.Y * LinesToPixels), 0, maximum);
        scroll.Offset = scroll.Offset.WithY(y);
    }
}
