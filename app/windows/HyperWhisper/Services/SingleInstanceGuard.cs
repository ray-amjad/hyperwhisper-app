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

    /// <summary>
    /// Opt back in to machine-global input for a secondary instance. See
    /// <see cref="OwnsGlobalInput"/>: set it only with every other HyperWhisper
    /// closed, because it re-creates exactly the collision the split exists to
    /// prevent.
    /// </summary>
    internal const string GlobalInputOverrideEnvironmentVariable =
        "HYPERWHISPER_WINDOWS_GLOBAL_INPUT";

    /// <summary>
    /// Whether THIS process may take the machine-global input resources: the
    /// WH_KEYBOARD_LL hooks in <see cref="KeyboardShortcutService"/> and
    /// <see cref="PushToTalkMonitor"/>, and every <c>RegisterHotKey</c>.
    ///
    /// The mutex above is per PROFILE, which is right - it guards one app-data
    /// root, and two roots share none of it. But a global keyboard hook is not
    /// scoped by anything at all. It is per PROCESS and non-exclusive, so with
    /// the mutex relaxed, one press of the default "Ctrl+Alt" toggle started a
    /// recording in BOTH processes: two microphone opens, two transcriptions,
    /// two History rows, two pastes into whatever had focus. The chords that go
    /// through RegisterHotKey instead fail with Win32 1409 in the second
    /// process, which the shell then reports as a shortcut conflict with
    /// itself.
    ///
    /// So the two facts are separated. Relaxing the mutex lets a scratch profile
    /// BOOT beside the user's own app, which is the whole point and is what
    /// every dev box GUI test depends on. Owning the keyboard is a different
    /// question, and the answer for a scratch profile is no.
    ///
    /// DETERMINISTIC, not first-come. An earlier draft had each instance race
    /// for a machine-global "input owner" mutex, so a scratch instance started
    /// first would keep the hooks and the user's real app would launch with no
    /// hotkeys at all. That trades a developer-only bug for a user-facing one on
    /// the same machine. Here a production instance - one whose app-data root is
    /// NOT overridden - always owns input, unconditionally and with no probe, so
    /// a real user's process is bit-for-bit unchanged.
    ///
    /// The escape hatch is for the case the rule costs: testing hotkey
    /// registration itself from a scratch profile, with nothing else running.
    /// It is opt-in, it is logged loudly, and it restores today's behaviour
    /// exactly.
    /// </summary>
    public static bool OwnsGlobalInput
    {
        get
        {
            if (!AppPaths.IsAppDataRootOverridden)
                return true;

            var optIn = Environment.GetEnvironmentVariable(GlobalInputOverrideEnvironmentVariable);
            return EvaluateGlobalInputOverride(optIn);
        }
    }

    /// <summary>
    /// The override's parse rule, split out so the smoke suite can pin it without
    /// writing to the environment of whoever runs it. "1", "true" and "yes" are
    /// accepted, case-insensitively; everything else - including an empty value,
    /// which is how PowerShell spells "unset this" - is a no.
    /// </summary>
    internal static bool EvaluateGlobalInputOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim() is "1"
            || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Log the input-ownership decision once, at startup, so a scratch instance
    /// whose hotkeys "do not work" says why in its own log rather than looking
    /// broken.
    /// </summary>
    public static void LogGlobalInputDecision()
    {
        if (!AppPaths.IsAppDataRootOverridden)
            return;

        if (OwnsGlobalInput)
        {
            LoggingService.Warn(
                "SingleInstanceGuard: this is a SECONDARY (overridden app-data root) instance and "
                + $"{GlobalInputOverrideEnvironmentVariable} is set, so it WILL install the global "
                + "keyboard hooks and register hotkeys. If another HyperWhisper is running, one key "
                + "press will fire in both.");
        }
        else
        {
            LoggingService.Info(
                "SingleInstanceGuard: this is a secondary (overridden app-data root) instance, so the "
                + "global keyboard hooks and RegisterHotKey are suppressed. Set "
                + $"{GlobalInputOverrideEnvironmentVariable}=1, with every other HyperWhisper closed, "
                + "to test hotkeys from this profile.");
        }
    }

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
