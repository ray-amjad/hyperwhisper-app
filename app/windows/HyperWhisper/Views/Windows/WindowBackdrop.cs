using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// The Mica backdrop MainWindow and OnboardingWindow share, so the two windows
/// stay the same object to look at (issue #696).
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmSystemBackdropTypeMica = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// Sets the dark-mode caption, round corners and Mica on <paramref name="window"/>.
    /// A no-op before the window has a handle; a failure on an older Windows build is
    /// logged under <paramref name="ownerName"/> and swallowed.
    /// </summary>
    internal static void ApplyMacStyle(Window window, string ownerName)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var darkMode = ThemeService.Instance.IsDarkMode ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, Marshal.SizeOf<int>());

            var cornerPreference = DwmWindowCornerPreferenceRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, Marshal.SizeOf<int>());

            var backdropType = DwmSystemBackdropTypeMica;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, Marshal.SizeOf<int>());
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"{ownerName}: Native backdrop unavailable: {ex.Message}");
        }
    }
}
