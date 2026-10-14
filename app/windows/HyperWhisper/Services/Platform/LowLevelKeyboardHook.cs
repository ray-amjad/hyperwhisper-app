using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperWhisper.Services.Platform;

/// <summary>
/// One Win32 WH_KEYBOARD_LL hook and the set of virtual keys it has seen go down
/// and not come up. KeyboardShortcutService and PushToTalkMonitor each own one;
/// this class is only the plumbing they used to copy (issue #694). What a service
/// does with a key stays in the service.
///
/// Every key-down is added to <see cref="PressedKeys"/> and every key-up removed
/// from it BEFORE the owner's callback runs, so the callback reads the set as it
/// stands after this event.
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Every hook user goes through this class, so the Win32 entry point is named only here.
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
    private static extern IntPtr InstallHook(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Returns the high-order bit set if the key is physically down at the time of the call.
    // Reliable when called from the UI thread AFTER the hook callback has returned
    // (the async state is updated after the hook chain completes, not inside it).
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly string _ownerName;
    private readonly Func<int, bool, bool> _onKey;
    private readonly HashSet<int> _pressedKeys = new();

    // Held in a field so the GC cannot collect the delegate Windows calls back into.
    private readonly LowLevelKeyboardProc _hookCallback;
    private IntPtr _hookId = IntPtr.Zero;

    /// <summary>One line per process, not one per Ensure call.</summary>
    private bool _loggedHookSuppressed;

    /// <param name="ownerName">The log prefix, so each service keeps its own log lines.</param>
    /// <param name="onKey">
    /// Called as <c>onKey(vkCode, isKeyDown)</c> for every key-down and key-up.
    /// Return true to swallow the key: no later hook and no application sees it.
    /// </param>
    internal LowLevelKeyboardHook(string ownerName, Func<int, bool, bool> onKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentNullException.ThrowIfNull(onKey);

        _ownerName = ownerName;
        _onKey = onKey;
        _hookCallback = HookCallback;
    }

    internal bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>The virtual keys currently tracked as down.</summary>
    internal IReadOnlySet<int> PressedKeys => _pressedKeys;

    /// <summary>Forgets one tracked key, for a key-up the hook never saw.</summary>
    internal void ForgetKey(int vk) => _pressedKeys.Remove(vk);

    /// <summary>Forgets every tracked key, to resynchronize after a reset.</summary>
    internal void ClearPressedKeys() => _pressedKeys.Clear();

    /// <summary>True if GetAsyncKeyState says <paramref name="vk"/> is physically down right now.</summary>
    internal static bool IsKeyPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Installs the hook if it is not installed yet.</summary>
    internal void Ensure()
    {
        if (_hookId != IntPtr.Zero) return;

        // A WH_KEYBOARD_LL hook is per PROCESS and non-exclusive: two instances
        // both get every key, so one press would start two recordings. Only the
        // instance that owns machine-global input installs it. See
        // SingleInstanceGuard.OwnsGlobalInput.
        if (!SingleInstanceGuard.OwnsGlobalInput)
        {
            if (!_loggedHookSuppressed)
            {
                _loggedHookSuppressed = true;
                LoggingService.Info(
                    $"{_ownerName}: low-level keyboard hook suppressed - this is a secondary instance");
            }
            return;
        }

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = InstallHook(
            WH_KEYBOARD_LL,
            _hookCallback,
            GetModuleHandle(curModule?.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            LoggingService.Error($"{_ownerName}: Failed to install hook (error {error})");
        }
        else
        {
            LoggingService.Debug($"{_ownerName}: Low-level keyboard hook installed");
        }
    }

    public void Dispose()
    {
        if (_hookId == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = (int)hookStruct.vkCode;

                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isKeyDown)
                {
                    _pressedKeys.Add(vkCode);
                    if (_onKey(vkCode, true)) return new IntPtr(1);
                }
                else if (isKeyUp)
                {
                    _pressedKeys.Remove(vkCode);
                    if (_onKey(vkCode, false)) return new IntPtr(1);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error($"{_ownerName}: HookCallback error", ex);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
}
