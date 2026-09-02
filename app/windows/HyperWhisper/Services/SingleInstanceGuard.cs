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
    private const string BaseMutexName = "HyperWhisper_SingleInstance_Mutex";
    private const string BaseMessageName = "HyperWhisper_ShowExistingInstance";

    /// <summary>
    /// The mutex this process competes for, and the window message a loser
    /// broadcasts to raise the winner.
    ///
    /// PER PROFILE, NOT PER PRODUCT. The guard exists to stop two instances
    /// fighting over ONE app-data root - the settings file, the Modes database,
    /// the model directory. It is not a licence check and it is not a lock on
    /// the executable. A process started with
    /// HYPERWHISPER_WINDOWS_APPDATA_ROOT pointing somewhere else shares none of
    /// that state, so refusing it protects nothing and costs the only way this
    /// head has of reaching first run while the user's own copy is running:
    /// the scratch-profile instance lived seven seconds and exited without ever
    /// writing "APPLICATION STARTING" to its log.
    ///
    /// The suffix is <see cref="AppPaths.AppDataRootHash"/>, the same 16 hex
    /// digits <see cref="AppPaths.CredentialResource"/> already appends, so
    /// there is one scheme for "which profile is this" rather than two. When
    /// the root is NOT overridden both names are byte-identical to what
    /// shipped, so nothing changes for a real user - including the ability of
    /// an old build and a new one to see each other.
    /// </summary>
    internal static string MutexName => Decorate(BaseMutexName);

    internal static string MessageName => Decorate(BaseMessageName);

    private static string Decorate(string baseName) =>
        AppPaths.IsAppDataRootOverridden
            ? $"{baseName}.Test.{AppPaths.AppDataRootHash}"
            : baseName;

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
