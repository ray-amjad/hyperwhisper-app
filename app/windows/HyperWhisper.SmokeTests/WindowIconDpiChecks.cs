// WINDOW ICON AT THE WINDOW'S OWN DPI (issue #1941)
//
// WPF gives a window with no Icon the exe's icon at the SYSTEM DPI's sizes, and
// under Per-Monitor V2 that DPI is the one the process started at. After a live
// 100% -> 150% change, a new window's title-bar icon was a 16 px image stretched
// to 24 px. These checks read back, with WM_GETICON, the icon handles a real
// window holds after WindowIconDpi has run, and measure their bitmaps.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using HyperWhisper.Utilities;

namespace HyperWhisper.SmokeTests;

internal static class WindowIconDpiChecks
{
    private const uint WM_GETICON = 0x007F;
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int SM_CXICON = 11;
    private const int SM_CXSMICON = 49;

    public static void RunAll(Action<string, Action> run, Func<System.Windows.Application> ensureApplication)
    {
        run("window icon: a window gets the icon sizes of the DPI it is on, not the start DPI — issue #1941", () =>
        {
            ensureApplication();
            Assert(File.Exists(WindowIconDpi.IconPath), $"app.ico is not next to the app: {WindowIconDpi.IconPath}");

            var window = new Window();
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                foreach (var (dpi, small, big) in new[] { (96u, 16, 32), (120u, 20, 40), (144u, 24, 48), (192u, 32, 64) })
                {
                    Assert(WindowIconDpi.Apply(window, dpi), $"no icon was sent at {dpi} dpi");
                    AssertIconSize(hwnd, ICON_SMALL, small, $"small icon at {dpi} dpi");
                    AssertIconSize(hwnd, ICON_BIG, big, $"big icon at {dpi} dpi");
                }
            }
            finally
            {
                window.Close();
            }
        });

        run("window icon: App installs it for every window, on Loaded — issue #1941", () =>
        {
            // The App constructor registers the class handler; a plain Window built
            // after it gets the icon when its Loaded event reaches the window itself.
            ensureApplication();

            var window = new Window();
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                // Start from a handle the rule would never pick, so a pass proves it ran.
                var dpi = GetDpiForWindow(hwnd);
                var wantSmall = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
                WindowIconDpi.Apply(window, dpi * 2);
                AssertIconSize(hwnd, ICON_SMALL, GetSystemMetricsForDpi(SM_CXSMICON, dpi * 2), "seeded small icon");

                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
                AssertIconSize(hwnd, ICON_SMALL, wantSmall, $"small icon after Loaded at {dpi} dpi");
                AssertIconSize(hwnd, ICON_BIG, GetSystemMetricsForDpi(SM_CXICON, dpi), $"big icon after Loaded at {dpi} dpi");
            }
            finally
            {
                window.Close();
            }
        });

        run("window icon: a window that sets its own Icon keeps it — issue #1941", () =>
        {
            ensureApplication();

            var window = new Window
            {
                Icon = BitmapFrame.Create(new Uri(WindowIconDpi.IconPath, UriKind.Absolute)),
            };
            new WindowInteropHelper(window).EnsureHandle();
            try
            {
                Assert(!WindowIconDpi.Apply(window, 144), "the rule replaced a window's own Icon");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertIconSize(IntPtr hwnd, int which, int expected, string what)
    {
        var icon = SendMessage(hwnd, WM_GETICON, (IntPtr)which, IntPtr.Zero);
        Assert(icon != IntPtr.Zero, $"{what}: the window holds no icon");
        Assert(GetIconInfo(icon, out var info), $"{what}: GetIconInfo failed");
        try
        {
            var bitmap = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            Assert(GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var bm) != 0, $"{what}: GetObject failed");
            var height = info.hbmColor != IntPtr.Zero ? bm.bmHeight : bm.bmHeight / 2;
            Assert(bm.bmWidth == expected && height == expected,
                $"{what}: expected {expected}x{expected}, the window holds {bm.bmWidth}x{height}");
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
