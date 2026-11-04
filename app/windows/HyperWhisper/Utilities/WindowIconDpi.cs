// WINDOW ICON AT THE WINDOW'S OWN DPI (issue #1941)
//
// No window here sets Window.Icon, so WPF gives each one the exe's icon, taken
// with ExtractIconEx at the SYSTEM DPI's icon sizes. Under Per-Monitor V2 (#1501)
// the system DPI is fixed when the process starts, so after a live scale change
// from 100% to 150% every window opened afterwards still got a 16 px small icon
// (and a 32 px big one), which Windows stretched to 24 px in the title bar: the
// soft icon on Edit Mode. Launched at 150%, the same window got 24 px.
//
// This sends each window app.ico at the sizes its own DPI asks for when it loads
// and again on every WM_DPICHANGED. The rule is registered once for the Window
// TYPE, so it covers every window in the process, the same way ComboBoxWheelGuard
// covers every dropdown.

using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HyperWhisper.Services;

namespace HyperWhisper.Utilities;

public static class WindowIconDpi
{
    private const int SM_CXICON = 11;
    private const int SM_CXSMICON = 49;
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    private static bool _installed;

    // One HICON per pixel size, kept for the life of the process: WM_SETICON does
    // not take ownership, and a window keeps drawing the handle it was given.
    private static readonly Dictionary<int, IntPtr> IconsBySize = new();

    internal static string IconPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");

    /// <summary>
    /// Registers the rule for every <see cref="Window"/> in this process. Called
    /// from <c>App</c>'s constructor, before any window is built. Idempotent.
    /// </summary>
    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(
            typeof(Window),
            Window.DpiChangedEvent,
            new System.Windows.DpiChangedEventHandler(OnDpiChanged));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded bubbles up from every element; only the window's own counts.
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window))
            Apply(window, dpi: 0);
    }

    private static void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (sender is Window window)
            Apply(window, (uint)Math.Round(e.NewDpi.PixelsPerInchX));
    }

    /// <summary>
    /// Sends <paramref name="window"/> the small and big icon sized for
    /// <paramref name="dpi"/>, or for the window's current DPI when it is 0. A
    /// window that set its own Icon keeps it. Returns whether icons were sent.
    /// </summary>
    internal static bool Apply(Window window, uint dpi)
    {
        if (window.Icon is not null)
            return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            if (dpi == 0)
                dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
                return false;

            var small = IconForSize(GetSystemMetricsForDpi(SM_CXSMICON, dpi));
            var big = IconForSize(GetSystemMetricsForDpi(SM_CXICON, dpi));
            if (small == IntPtr.Zero || big == IntPtr.Zero)
                return false;

            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, small);
            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, big);
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // GetDpiForWindow and GetSystemMetricsForDpi are Windows 10 1607+. On
            // anything older WPF's own icon stays, as it always did.
            return false;
        }
    }

    private static IntPtr IconForSize(int size)
    {
        if (size <= 0)
            return IntPtr.Zero;

        lock (IconsBySize)
        {
            if (IconsBySize.TryGetValue(size, out var cached))
                return cached;

            var path = IconPath;
            var icon = File.Exists(path)
                ? LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE)
                : IntPtr.Zero;
            if (icon == IntPtr.Zero)
            {
                LoggingService.Warn($"WindowIconDpi: could not load a {size} px icon from {path}");
                return IntPtr.Zero;
            }

            IconsBySize[size] = icon;
            return icon;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
