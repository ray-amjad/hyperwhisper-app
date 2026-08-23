using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace HyperWhisper.Linux.Overlay;

internal static class LinuxOverlayWindowPolicy
{
    private const long InputHint = 1L;
    private const int PropertyReplace = 0;

    public static void TryApply(Window window)
    {
        IntPtr display = IntPtr.Zero;
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero
                || !string.Equals(handle.HandleDescriptor, "XID", StringComparison.OrdinalIgnoreCase)) return;
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            SetAtomProperty(display, handle.Handle, "_NET_WM_WINDOW_TYPE", ["_NET_WM_WINDOW_TYPE_NOTIFICATION"]);
            SetAtomProperty(display, handle.Handle, "_NET_WM_STATE",
                ["_NET_WM_STATE_ABOVE", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER"]);
            var hints = new XWindowManagerHints { Flags = InputHint, Input = 0 };
            _ = XSetWMHints(display, handle.Handle, ref hints);
            _ = XFlush(display);
        }
        catch { /* Window-manager hints are best-effort. */ }
        finally { if (display != IntPtr.Zero) _ = XCloseDisplay(display); }
    }

    private static void SetAtomProperty(IntPtr display, IntPtr window, string propertyName,
        IReadOnlyList<string> values)
    {
        var property = XInternAtom(display, propertyName, false);
        var atomType = XInternAtom(display, "ATOM", false);
        if (property == IntPtr.Zero || atomType == IntPtr.Zero) return;
        var native = Marshal.AllocHGlobal(values.Count * IntPtr.Size);
        try
        {
            for (var index = 0; index < values.Count; index++)
                Marshal.WriteIntPtr(native, index * IntPtr.Size, XInternAtom(display, values[index], false));
            _ = XChangeProperty(display, window, property, atomType, 32, PropertyReplace, native, values.Count);
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowManagerHints
    {
        public long Flags;
        public int Input;
        public int InitialState;
        public IntPtr IconPixmap;
        public IntPtr IconWindow;
        public int IconX;
        public int IconY;
        public IntPtr IconMask;
        public IntPtr WindowGroup;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr display, IntPtr window,
        IntPtr property, IntPtr type, int format, int mode, IntPtr data, int elementCount);
    [DllImport("libX11.so.6")] private static extern int XSetWMHints(IntPtr display, IntPtr window,
        ref XWindowManagerHints hints);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
