using System.Runtime.InteropServices;
using System.Threading;

namespace HyperWhisper.Services;

/// <summary>
/// Prevents multiple instances of HyperWhisper from running simultaneously.
/// Uses a named mutex for detection and RegisterWindowMessage for signaling
/// the existing instance to come to the foreground.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "HyperWhisper_SingleInstance_Mutex";
    private const string MessageName = "HyperWhisper_ShowExistingInstance";

    private static Mutex? _mutex;
    private static uint _wmShowMe;

    /// <summary>
    /// The registered window message ID used to signal the existing instance.
    /// </summary>
    public static uint WM_SHOWME => _wmShowMe;

    /// <summary>
    /// Attempts to acquire the single-instance mutex.
    /// Returns true if this is the first instance; false if another is already running.
    /// </summary>
    public static bool TryAcquire()
    {
        _wmShowMe = RegisterWindowMessage(MessageName);
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        return createdNew;
    }

    /// <summary>
    /// Broadcasts a message to all top-level windows telling the existing instance
    /// to bring itself to the foreground.
    /// </summary>
    public static void SignalExistingInstance()
    {
        var wm = RegisterWindowMessage(MessageName);
        PostMessage(HWND_BROADCAST, wm, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Releases and disposes the mutex. Call from OnExit.
    /// </summary>
    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex was not owned by this thread (already released or never acquired)
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
