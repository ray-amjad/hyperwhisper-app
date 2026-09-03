using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace HyperWhisper.Views.Controls.Onboarding;

/// <summary>
/// The scrolling stage every onboarding step page sits in. See the XAML header
/// for why the stage scrolls at all.
/// </summary>
[ContentProperty(nameof(StageContent))]
public partial class OnboardingStage : WpfUserControl
{
    public OnboardingStage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The step's own content. A dedicated property rather than the inherited
    /// <see cref="ContentControl.Content"/>, because this control's own Content is
    /// the ScrollViewer and a page setting it would throw the scrolling away.
    /// </summary>
    public static readonly DependencyProperty StageContentProperty =
        DependencyProperty.Register(
            nameof(StageContent),
            typeof(object),
            typeof(OnboardingStage),
            new PropertyMetadata(null));

    public object? StageContent
    {
        get => GetValue(StageContentProperty);
        set => SetValue(StageContentProperty, value);
    }

    /// <summary>
    /// Exposed for the smoke suite, which asserts that every step page's visual
    /// root really is a stage with a ScrollViewer in it. Cheap, and it stops a
    /// ninth page being added later without the scroll wrapper.
    /// </summary>
    public ScrollViewer Scroll => StageScroll;

    // =========================================================================
    // NESTED SCROLLING
    //
    // Three regions inside the stage are unbounded and get their own MaxHeight
    // plus their own scrollbar: the Try It transcript, the Microphone step's
    // device list, and any long error note. In WPF an inner ScrollViewer swallows
    // the mouse wheel at its own scroll limits, so the page silently stops
    // scrolling when the pointer happens to be over one of them.
    //
    // The decision is taken once, here, and applied by putting
    // onboarding:OnboardingStage.BubblesMouseWheel="True" on the inner control:
    // when the inner region cannot use the wheel, the event is re-raised on its
    // parent so the stage gets it.
    // =========================================================================

    public static readonly DependencyProperty BubblesMouseWheelProperty =
        DependencyProperty.RegisterAttached(
            "BubblesMouseWheel",
            typeof(bool),
            typeof(OnboardingStage),
            new PropertyMetadata(false, OnBubblesMouseWheelChanged));

    public static void SetBubblesMouseWheel(DependencyObject element, bool value) =>
        element.SetValue(BubblesMouseWheelProperty, value);

    public static bool GetBubblesMouseWheel(DependencyObject element) =>
        (bool)element.GetValue(BubblesMouseWheelProperty);

    private static void OnBubblesMouseWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.PreviewMouseWheel -= OnInnerPreviewMouseWheel;

        if (e.NewValue is true)
            element.PreviewMouseWheel += OnInnerPreviewMouseWheel;
    }

    private static void OnInnerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element)
            return;

        var inner = FindScrollViewer(element);

        // An inner region that can still scroll in the requested direction keeps
        // the wheel. Only the limits are forwarded, so scrolling a long transcript
        // still works and reaching its end carries on scrolling the page.
        if (inner is not null && CanScroll(inner, e.Delta))
            return;

        if ((element as FrameworkElement)?.Parent is not UIElement parent)
            return;

        e.Handled = true;

        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender
        });
    }

    private static bool CanScroll(ScrollViewer scroll, int delta)
    {
        if (scroll.ScrollableHeight <= 0)
            return false;

        return delta < 0
            ? scroll.VerticalOffset < scroll.ScrollableHeight
            : scroll.VerticalOffset > 0;
    }

    /// <summary>The element's own ScrollViewer, whether it is one or contains one.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer self)
            return self;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
            if (found is not null)
                return found;
        }

        return null;
    }
}
