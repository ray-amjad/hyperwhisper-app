// MIC SILENCE WARNING WINDOW (#2411)
// "No sound from {mic}": one amber line in a slim pill that sits just above the
// recording overlay while a recording hears nothing. The overlay owns it and is
// the only thing that shows, moves or hides it.
//
// A separate window rather than a row inside the overlay: the overlay is a fixed
// 200x40 pill whose saved position is a ratio of its own size and whose row is
// already full (stop button, mode badge, waveform). Growing it would move the
// stop button under the cursor mid-recording and shift the saved position.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HyperWhisper.Utilities;

namespace HyperWhisper.Views.Windows;

public partial class MicSilenceWarningWindow : Window
{
    /// <summary>The gap between the warning and the overlay below it, in DIP.</summary>
    internal const double GapAboveOverlay = 6;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    public MicSilenceWarningWindow()
    {
        InitializeComponent();
    }

    /// <summary>The line on screen, for the smoke suite.</summary>
    internal System.Windows.Controls.TextBlock Text => WarningText;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never takes focus from the app being dictated into, and clicks fall through.
        var helper = new WindowInteropHelper(this);
        var exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
    }

    /// <summary>Shows <paramref name="text"/> centred just above <paramref name="overlay"/>.</summary>
    public void ShowAbove(Window overlay, string text)
    {
        WarningText.Text = text;
        ToolTip = text;
        if (!IsVisible)
            Show();
        PositionAbove(overlay);
    }

    /// <summary>Follows the overlay when it is dragged or re-placed.</summary>
    public void PositionAbove(Window overlay)
    {
        if (!IsVisible) return;
        MonitorWorkArea.Place(this,
            () => MonitorWorkArea.OfWindow(overlay) ?? MonitorWorkArea.Primary(),
            _ => TopLeftAbove(ToastWindowBehavior.WindowBounds(overlay), Width, Height));
    }

    /// <summary>
    /// Centred over <paramref name="overlay"/>, <see cref="GapAboveOverlay"/> above
    /// it. Pure, so the smoke suite can pin it.
    /// </summary>
    internal static System.Windows.Point TopLeftAbove(Rect overlay, double width, double height) =>
        new(overlay.Left + (overlay.Width - width) / 2, overlay.Top - height - GapAboveOverlay);
}
