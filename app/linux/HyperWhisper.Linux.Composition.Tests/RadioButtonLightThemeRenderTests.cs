using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;

// Issue #2514. The shared RadioButton theme painted its disc a hard-coded #FFFFFF, copied from the
// Windows DARK page, so on the light #FCFCFD card an unchecked option drew nothing and a checked one
// only its 5 px centre. This renders an unchecked and a checked RadioButton on an HwCardBackground
// card under the real App, once per theme, and reads the pixels: in Light the disc must reach 3:1
// against the card (WCAG 1.4.11) and the checked centre 3:1 against the disc; in Dark the pair must
// still be exactly Windows' #FFFFFF / #212121.
static class RadioButtonLightThemeRenderTests
{
    public const string ProbeArgument = "--probe-radio-button-theme";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2514 RadioButton theme probe", "en", "1280x800");

    /// <summary>The child half. Exit 0 only if both themes draw a visible radio glyph.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var application = Application.Current!;
            var unchecked_ = new RadioButton { Content = "Light", GroupName = "Probe" };
            var checked_ = new RadioButton { Content = "System default", GroupName = "Probe", IsChecked = true };
            var stack = new StackPanel { Spacing = 16, Children = { checked_, unchecked_ } };
            var card = new Border { Padding = new Thickness(24), Child = stack };
            card[!Border.BackgroundProperty] = card.GetResourceObservable("HwCardBackgroundBrush").ToBinding();
            var window = new Window { Width = 320, Height = 160, Content = card };
            window.Show();

            var failures = new List<string>();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                application.RequestedThemeVariant = variant;
                Pump(window);
                using var bitmap = Render(window);
                var cardColour = Pixel(bitmap, window, card, new Point(6, 6));
                var discOff = Pixel(bitmap, window, Disc(unchecked_), new Point(6, 6));
                var discOn = Pixel(bitmap, window, Disc(checked_), new Point(6, 2));
                var centreOn = Pixel(bitmap, window, Disc(checked_), new Point(6, 6));
                Console.WriteLine($"{variant}: card {Hex(cardColour)}, unchecked disc {Hex(discOff)} ({Contrast(discOff, cardColour):F2}:1), " +
                    $"checked disc {Hex(discOn)} ({Contrast(discOn, cardColour):F2}:1), checked centre {Hex(centreOn)} ({Contrast(centreOn, discOn):F2}:1 on the disc)");

                if (variant == ThemeVariant.Light)
                {
                    if (Contrast(discOff, cardColour) < 3) failures.Add($"Light: the unchecked disc {Hex(discOff)} is {Contrast(discOff, cardColour):F2}:1 on the card {Hex(cardColour)}, under 3:1");
                    if (Contrast(discOn, cardColour) < 3) failures.Add($"Light: the checked disc {Hex(discOn)} is {Contrast(discOn, cardColour):F2}:1 on the card {Hex(cardColour)}, under 3:1");
                    if (Contrast(centreOn, discOn) < 3) failures.Add($"Light: the checked centre {Hex(centreOn)} is {Contrast(centreOn, discOn):F2}:1 on its disc {Hex(discOn)}, under 3:1");
                }
                else
                {
                    if (!Near(discOff, Colors.White)) failures.Add($"Dark: the unchecked disc is {Hex(discOff)}, not Windows' #FFFFFF");
                    if (!Near(discOn, Colors.White)) failures.Add($"Dark: the checked disc is {Hex(discOn)}, not Windows' #FFFFFF");
                    if (!Near(centreOn, Color.Parse("#212121"))) failures.Add($"Dark: the checked centre is {Hex(centreOn)}, not Windows' #212121");
                }
            }
            window.Close();
            Pump(window);
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    private static Ellipse Disc(RadioButton radio) =>
        radio.GetVisualDescendants().OfType<Ellipse>().FirstOrDefault(ellipse => ellipse.Name == "Disc")
        ?? throw new InvalidOperationException("the RadioButton template has no Ellipse#Disc");

    private static RenderTargetBitmap Render(Window window)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        return bitmap;
    }

    // The colour at a point inside a control, read from the window's render.
    private static Color Pixel(RenderTargetBitmap bitmap, Window window, Visual target, Point local)
    {
        var at = target.TranslatePoint(local, window) ?? throw new InvalidOperationException($"{target} is not in the window");
        var pixels = new byte[4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect((int)at.X, (int)at.Y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        // RenderTargetBitmap is premultiplied BGRA; every sampled surface here is opaque.
        return bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromRgb(pixels[0], pixels[1], pixels[2])
            : Color.FromRgb(pixels[2], pixels[1], pixels[0]);
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }

    private static bool Near(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;

    private static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
