using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using HyperWhisper.Models;

// Alias WPF clipboard types to avoid ambiguity with WinForms
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

namespace HyperWhisper.Services;

/// <summary>
/// SMART PASTE SERVICE
///
/// Pastes transcription text into the previously focused application:
/// 1. Captures the foreground window before recording starts
/// 2. Captures clipboard content for later restoration
/// 3. After transcription, copies text to clipboard
/// 4. Reactivates the captured window
/// 5. Simulates Ctrl+V to paste
/// 6. Schedules restoration of original clipboard content
///
/// CLIPBOARD PRESERVATION (matches macOS behavior):
/// - Captures ALL clipboard formats at recording start (text, images, RTF, HTML, files, etc.)
/// - Stores raw data instead of objects (Windows clipboard objects can't be reused after clear)
/// - Restores original clipboard after configurable delay (default 10 seconds)
/// - Cancels pending restoration when new recording starts
///
/// USES:
/// - InputSimulator for reliable Ctrl+V simulation
/// - Win32 SetForegroundWindow to reactivate previous app
/// - AllowSetForegroundWindow to bypass foreground restrictions
///
/// FOCUS-AWARE PASTE:
/// - Captures focused child HWND via GetGUIThreadInfo at recording start
/// - Restores focus using AttachThreadInput + SetFocus for cross-process child focus
/// - Polls for focus readiness instead of fixed delays (max 300ms for Chromium, 150ms native)
/// - Detects password fields via UI Automation and skips paste (text stays in clipboard)
/// - Browsers/Electron skip non-text-field UIA gating, but still run secure-field checks
/// - Non-text-field focus still attempts paste (Ctrl+V is harmless, text stays in clipboard)
/// </summary>
public class SmartPasteService : IDisposable
{
    // =========================================================================
    // WIN32 P/INVOKE DECLARATIONS
    // =========================================================================

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    // Special value to allow any process to set foreground window
    private const int ASFW_ANY = -1;

    // =========================================================================
    // PROCESS-SPECIFIC PASTE DELAYS
    // =========================================================================

    /// <summary>
    /// Browser process names — these get longer delays and skip UIA focus detection.
    /// UIA is unreliable inside web content areas.
    /// </summary>
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    /// <summary>
    /// Electron app process names — need longer delays and skip UIA (unreliable in Chromium).
    /// Treated similarly to browsers: Chromium's multi-process architecture makes
    /// SetForegroundWindow unreliable and UIA focus detection flaky.
    /// </summary>
    private static readonly HashSet<string> ElectronProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Cursor", "Windsurf", "Simplenote"
    };

    /// <summary>
    /// JetBrains IDE process names.
    /// </summary>
    private static readonly HashSet<string> JetBrainsProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "idea64", "rider64", "webstorm64", "pycharm64", "phpstorm64", "clion64", "goland64", "datagrip64", "rubymine64"
    };

    // =========================================================================
    // CLIPBOARD FORMAT SUPPORT
    // =========================================================================

    /// <summary>
    /// List of clipboard formats we attempt to preserve, in priority order.
    /// These cover the most common clipboard content types:
    /// - Text formats: Unicode, RTF, HTML
    /// - Image formats: Bitmap, PNG, JPEG
    /// - File formats: FileDrop (file paths)
    /// - Other: Custom formats are also captured dynamically
    /// </summary>
    private static readonly string[] SupportedFormats = new[]
    {
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.Rtf,
        DataFormats.Html,
        DataFormats.Bitmap,
        DataFormats.FileDrop,
        DataFormats.CommaSeparatedValue,
    };

    // =========================================================================
    // INSTANCE FIELDS
    // =========================================================================

    private readonly InputSimulator _inputSimulator;
    private IntPtr _previousForegroundWindow;
    private uint _previousThreadId;
    private IntPtr _previousFocusedChild;

    // Clipboard preservation state
    // We store clipboard data as a dictionary of format -> data
    // because IDataObject instances cannot be reused after Clipboard.Clear()
    private Dictionary<string, object>? _savedClipboardData;
    private CancellationTokenSource? _restorationCts;
    private bool _isInRecordingSession;
    private readonly object _clipboardLock = new();
    private bool _disposed;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public SmartPasteService()
    {
        _inputSimulator = new InputSimulator();
    }

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Captures the current foreground window.
    /// Call this BEFORE showing recording overlay or any UI.
    /// </summary>
    public void CaptureForegroundWindow()
    {
        _previousForegroundWindow = GetForegroundWindow();
        _previousThreadId = 0;
        _previousFocusedChild = IntPtr.Zero;

        if (_previousForegroundWindow != IntPtr.Zero)
        {
            // Get the thread that owns the foreground window
            _previousThreadId = GetWindowThreadProcessId(_previousForegroundWindow, out _);

            // Get the focused child control within that thread's input queue
            if (_previousThreadId != 0)
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(_previousThreadId, ref info))
                {
                    _previousFocusedChild = info.hwndFocus;
                    LoggingService.Debug($"SmartPasteService: Captured foreground window: {_previousForegroundWindow}, thread: {_previousThreadId}, focusedChild: {_previousFocusedChild}");
                }
                else
                {
                    LoggingService.Debug($"SmartPasteService: Captured foreground window: {_previousForegroundWindow}, thread: {_previousThreadId}, GetGUIThreadInfo failed");
                }
            }
        }
    }

    /// <summary>
    /// Returns whether the captured paste target still exists and is not minimized.
    /// Streaming uses this to stop automatically if the pinned target disappears.
    /// </summary>
    public bool IsCapturedTargetAvailable()
    {
        return _previousForegroundWindow != IntPtr.Zero &&
               IsWindow(_previousForegroundWindow) &&
               !IsIconic(_previousForegroundWindow);
    }

    /// <summary>
    /// Starts a recording session.
    /// Captures the current clipboard content for later restoration.
    /// Must be called at the beginning of recording, before any clipboard modifications.
    ///
    /// CLIPBOARD CAPTURE FLOW:
    /// 1. Cancel any pending restoration from a previous recording
    /// 2. Extract data from ALL clipboard formats (not just text)
    /// 3. Store raw data for later restoration
    ///
    /// WHY EXTRACT DATA INSTEAD OF STORING IDATAOBJECT?
    /// Windows IDataObject instances are tied to the clipboard they came from.
    /// After Clipboard.SetDataObject() is called, the old IDataObject becomes invalid.
    /// We must extract the raw data (bytes, strings, file lists) to restore later.
    /// </summary>
    public void StartRecordingSession()
    {
        LoggingService.Info("SmartPasteService: Starting recording session");

        // Cancel any pending restoration from a previous recording
        CancelPendingRestore();

        lock (_clipboardLock)
        {
            _savedClipboardData = null;

            try
            {
                // Extract data from clipboard for each supported format.
                // An empty (or fully-unsupported) clipboard simply yields an empty
                // dataDict and is handled by the Count check below. We intentionally
                // do NOT pre-screen with a hand-maintained format list here, because
                // it can drift from SupportedFormats and silently skip capture for
                // clipboards that hold only Html/Csv/Text (no Unicode-text fallback),
                // which would destroy that content on paste with nothing to restore.
                var dataDict = new Dictionary<string, object>();
                var capturedFormats = new List<string>();

                foreach (var format in SupportedFormats)
                {
                    try
                    {
                        if (Clipboard.ContainsData(format))
                        {
                            var data = Clipboard.GetData(format);
                            if (data != null)
                            {
                                // Special handling for different data types
                                // We need to make copies of mutable data
                                if (format == DataFormats.FileDrop && data is string[] files)
                                {
                                    // Copy file path array
                                    dataDict[format] = (string[])files.Clone();
                                    capturedFormats.Add("FileDrop");
                                }
                                else if (format == DataFormats.Bitmap && data is BitmapSource bitmap)
                                {
                                    // Convert to PNG bytes for reliable restoration
                                    using var stream = new MemoryStream();
                                    var encoder = new PngBitmapEncoder();
                                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                    encoder.Save(stream);
                                    dataDict[format] = stream.ToArray();
                                    capturedFormats.Add("Bitmap");
                                }
                                else if (data is string strData)
                                {
                                    // String data can be stored directly
                                    dataDict[format] = strData;
                                    capturedFormats.Add(format);
                                }
                                else if (data is MemoryStream ms)
                                {
                                    // Memory stream: copy the bytes
                                    dataDict[format] = ms.ToArray();
                                    capturedFormats.Add(format);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue - some formats may fail due to app-specific issues
                        LoggingService.Debug($"SmartPasteService: Failed to capture format {format}: {ex.Message}");
                    }
                }

                if (dataDict.Count > 0)
                {
                    _savedClipboardData = dataDict;
                    LoggingService.Info($"SmartPasteService: Saved clipboard with {dataDict.Count} format(s): {string.Join(", ", capturedFormats)}");
                }
                else
                {
                    LoggingService.Info("SmartPasteService: No supported clipboard formats found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"SmartPasteService: Failed to capture clipboard: {ex.Message}");
                _savedClipboardData = null;
            }

            _isInRecordingSession = true;
        }
    }

    /// <summary>
    /// Ends the recording session.
    /// Call this after transcription and paste are complete.
    /// </summary>
    public void EndRecordingSession()
    {
        _isInRecordingSession = false;
        LoggingService.Debug("SmartPasteService: Ended recording session");
    }

    /// <summary>
    /// Cancels any pending clipboard restoration.
    /// Called when a new recording starts to prevent stale restorations.
    /// </summary>
    public void CancelPendingRestore()
    {
        if (_restorationCts != null)
        {
            _restorationCts.Cancel();
            _restorationCts.Dispose();
            _restorationCts = null;
            LoggingService.Debug("SmartPasteService: Cancelled pending clipboard restoration");
        }
    }

    public bool HasPendingClipboardRestore
    {
        get
        {
            lock (_clipboardLock)
            {
                return _restorationCts != null && _savedClipboardData is { Count: > 0 };
            }
        }
    }

    /// <summary>
    /// Schedules restoration of the original clipboard content.
    /// Called after successful paste to restore user's previous clipboard.
    ///
    /// RESTORATION FLOW:
    /// 1. Wait for configurable delay (allows user to paste transcription again if needed)
    /// 2. Clear current clipboard
    /// 3. Restore original clipboard content with ALL formats
    ///
    /// The restoration is cancellable - if a new recording starts before
    /// the delay expires, the restoration is cancelled.
    /// </summary>
    public void ScheduleClipboardRestore()
    {
        // Check settings - is restoration enabled?
        if (!SettingsService.Instance.RestoreClipboardAfterPaste)
        {
            LoggingService.Debug("SmartPasteService: Clipboard restoration disabled in settings");
            return;
        }

        // Check if we have content to restore
        lock (_clipboardLock)
        {
            if (_savedClipboardData == null || _savedClipboardData.Count == 0)
            {
                LoggingService.Debug("SmartPasteService: No clipboard content to restore");
                return;
            }
        }

        var delay = SettingsService.Instance.ClipboardRestoreDelaySeconds;
        LoggingService.Info($"SmartPasteService: Scheduling clipboard restoration in {delay} seconds");

        // Cancel any existing restoration timer
        CancelPendingRestore();

        // Create new cancellation token for this restoration
        _restorationCts = new CancellationTokenSource();
        var token = _restorationCts.Token;

        // Schedule the restoration on a background thread
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                // Restore must happen on STA thread (UI thread)
                // Use Application.Current.Dispatcher to marshal to UI thread
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    RestoreClipboard();
                });
            }
            catch (TaskCanceledException)
            {
                // Expected when restoration is cancelled
                LoggingService.Debug("SmartPasteService: Clipboard restoration was cancelled");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"SmartPasteService: Clipboard restoration failed: {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    /// Restores saved clipboard content immediately, used during app shutdown before disposal.
    /// </summary>
    public void RestoreClipboardImmediately()
    {
        if (!SettingsService.Instance.RestoreClipboardAfterPaste)
        {
            LoggingService.Debug("SmartPasteService: Clipboard restoration disabled in settings");
            return;
        }

        CancelPendingRestore();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RestoreClipboard);
            return;
        }

        RestoreClipboard();
    }

    /// <summary>
    /// Restores the saved clipboard content.
    /// Must be called on the UI thread (STA thread for clipboard access).
    ///
    /// RACE CONDITION PROTECTION:
    /// The entire restoration logic is protected by _clipboardLock to prevent
    /// race conditions between:
    /// 1. Scheduled restoration callback (after delay)
    /// 2. New recording session starting (CancelPendingRestore + StartRecordingSession)
    /// 3. Disposal of the service
    ///
    /// Without this lock, a restoration could run while a new recording is capturing
    /// clipboard, leading to corrupted state or restoring the wrong content.
    /// </summary>
    private void RestoreClipboard()
    {
        lock (_clipboardLock)
        {
            // Check if disposed - don't restore after cleanup
            if (_disposed)
            {
                LoggingService.Debug("SmartPasteService: Cannot restore clipboard - service disposed");
                return;
            }

            // Check if we have data to restore
            if (_savedClipboardData == null || _savedClipboardData.Count == 0)
            {
                LoggingService.Debug("SmartPasteService: No clipboard content to restore");
                return;
            }

            // Make a local copy of the data to restore
            var dataToRestore = _savedClipboardData;

            // Clear saved data if not in a recording session
            // This prevents double-restoration and allows new recordings to capture fresh clipboard
            if (!_isInRecordingSession)
            {
                _savedClipboardData = null;
            }

            try
            {
                // Create new DataObject with all saved formats
                var dataObject = new DataObject();
                var restoredFormats = new List<string>();

                foreach (var kvp in dataToRestore)
                {
                    try
                    {
                        var format = kvp.Key;
                        var data = kvp.Value;

                        if (format == DataFormats.Bitmap && data is byte[] imageBytes)
                        {
                            // Restore image from PNG bytes
                            using var stream = new MemoryStream(imageBytes);
                            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0)
                            {
                                dataObject.SetData(format, decoder.Frames[0]);
                                restoredFormats.Add("Bitmap");
                            }
                        }
                        else if (data is byte[] streamBytes)
                        {
                            // Restore memory stream data
                            dataObject.SetData(format, new MemoryStream(streamBytes));
                            restoredFormats.Add(format);
                        }
                        else
                        {
                            // Direct data (strings, file arrays)
                            dataObject.SetData(format, data);
                            restoredFormats.Add(format);
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"SmartPasteService: Failed to restore format {kvp.Key}: {ex.Message}");
                    }
                }

                if (restoredFormats.Count > 0)
                {
                    Clipboard.SetDataObject(dataObject, true);
                    LoggingService.Info($"SmartPasteService: Restored clipboard with {restoredFormats.Count} format(s): {string.Join(", ", restoredFormats)}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"SmartPasteService: Failed to restore clipboard: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copies text to clipboard without attempting to paste.
    /// Use this when auto-paste is disabled.
    /// </summary>
    /// <returns>True if clipboard copy succeeded.</returns>
    public bool CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            LoggingService.Warn("SmartPasteService: Empty text, nothing to copy");
            return false;
        }

        try
        {
            SetClipboardText(text);
            LoggingService.Info("SmartPasteService: Text copied to clipboard (auto-paste disabled)");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error("SmartPasteService: Failed to set clipboard", ex);
            return false;
        }
    }

    /// <summary>
    /// Pastes text by copying to clipboard, reactivating the previous window,
    /// and simulating Ctrl+V. Uses focus-aware detection for password field safety
    /// and app-specific paste delays.
    /// </summary>
    /// <returns>SmartPasteResult indicating what happened</returns>
    public SmartPasteResult SmartPaste(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            LoggingService.Warn("SmartPasteService: Empty text, nothing to paste");
            return SmartPasteResult.Failed;
        }

        LoggingService.Info("SmartPasteService: Starting smart paste...");

        // Step 1: Copy text to clipboard (always do this as fallback)
        try
        {
            SetClipboardText(text);
            LoggingService.Debug("SmartPasteService: Text copied to clipboard");
        }
        catch (Exception ex)
        {
            LoggingService.Error("SmartPasteService: Failed to set clipboard", ex);
            return SmartPasteResult.Failed;
        }

        // Step 2: Reactivate previous window
        if (_previousForegroundWindow == IntPtr.Zero)
        {
            LoggingService.Warn("SmartPasteService: No previous window captured, text is in clipboard only");
            return SmartPasteResult.CopiedToClipboard;
        }

        var processName = GetProcessNameFromWindow(_previousForegroundWindow);
        bool isBrowser = !string.IsNullOrEmpty(processName) && BrowserProcessNames.Contains(processName);
        bool isElectron = !string.IsNullOrEmpty(processName) && ElectronProcessNames.Contains(processName);
        bool isChromium = isBrowser || isElectron;

        LoggingService.Debug($"SmartPasteService: Process='{processName}', isChromium={isChromium}");

        // Step 3: Restore focus to the target window + child control
        RestoreFocus(isChromium);

        var (_, isFocusedPassword) = DetectFocusedField();
        if (isFocusedPassword)
        {
            LoggingService.Info("SmartPasteService: Password field detected - skipping paste, text in clipboard");
            return SmartPasteResult.SecureFieldSkipped;
        }

        // Step 4: Browser and Electron fast-path skips text-field detection only.
        // UIA is unreliable inside Chromium content areas (both browsers and Electron apps).
        // Secure-field detection above still runs for Chromium targets before this fast path.
        if (!isChromium)
        {
            // Step 5: Focus detection for native apps only
            var (isTextField, isPassword) = DetectFocusedField();
            if (isPassword)
            {
                LoggingService.Info("SmartPasteService: Password field detected — skipping paste, text in clipboard");
                return SmartPasteResult.SecureFieldSkipped;
            }

            if (!isTextField)
            {
                LoggingService.Debug("SmartPasteService: No text field detected, attempting paste anyway");
            }
        }

        // Step 6: Simulate Ctrl+V to paste
        try
        {
            _inputSimulator.Keyboard.ModifiedKeyStroke(
                VirtualKeyCode.CONTROL,
                VirtualKeyCode.VK_V);

            LoggingService.Info("SmartPasteService: Sent Ctrl+V to paste text");
            return SmartPasteResult.Pasted;
        }
        catch (Exception ex)
        {
            LoggingService.Error("SmartPasteService: Failed to simulate Ctrl+V", ex);
            return SmartPasteResult.Failed;
        }
    }

    // =========================================================================
    // FOCUS RESTORATION
    // =========================================================================

    /// <summary>
    /// Restores focus to the previously captured window and child control.
    ///
    /// For Chromium apps (browsers, Electron), SetForegroundWindow alone only activates
    /// the outer window — it does NOT restore keyboard focus to the renderer/text field.
    /// This method uses AttachThreadInput + SetFocus to restore the focused child control,
    /// then polls for readiness instead of using a fixed sleep.
    /// </summary>
    private void RestoreFocus(bool isChromium)
    {
        var currentForeground = GetForegroundWindow();
        bool alreadyForeground = currentForeground == _previousForegroundWindow;

        if (!alreadyForeground)
        {
            // Target window lost foreground — need to reactivate it
            AllowSetForegroundWindow(ASFW_ANY);

            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = GetWindowThreadProcessId(currentForeground, out _);
            bool attached = false;

            try
            {
                // Attach our input queue to the current foreground thread so we can
                // call SetForegroundWindow reliably
                if (currentThreadId != foregroundThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                BringWindowToTop(_previousForegroundWindow);
                SetForegroundWindow(_previousForegroundWindow);
                LoggingService.Debug("SmartPasteService: Activated target window via AttachThreadInput + SetForegroundWindow");
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }
        else
        {
            LoggingService.Debug("SmartPasteService: Target already foreground, skipping SetForegroundWindow");
        }

        // Restore the focused child control if we captured one
        if (_previousFocusedChild != IntPtr.Zero && IsWindow(_previousFocusedChild))
        {
            uint currentThreadId = GetCurrentThreadId();
            bool attached = false;

            try
            {
                // Attach to the target thread so SetFocus works cross-process
                if (currentThreadId != _previousThreadId && _previousThreadId != 0)
                {
                    attached = AttachThreadInput(currentThreadId, _previousThreadId, true);
                }

                SetFocus(_previousFocusedChild);
                LoggingService.Debug($"SmartPasteService: Restored focus to child {_previousFocusedChild}");
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, _previousThreadId, false);
                }
            }
        }

        // Wait for focus to be ready instead of a fixed sleep
        WaitForFocusReady(isChromium);
    }

    /// <summary>
    /// Polls until the target window is foreground and has keyboard focus,
    /// or until the timeout is reached. Replaces fixed Thread.Sleep delays.
    /// </summary>
    private void WaitForFocusReady(bool isChromium)
    {
        // Max wait: 300ms for Chromium, 150ms for native apps
        int maxWaitMs = isChromium ? 300 : 150;
        const int pollIntervalMs = 15;
        int waited = 0;

        while (waited < maxWaitMs)
        {
            // Check if foreground window matches
            if (GetForegroundWindow() == _previousForegroundWindow)
            {
                // Check if the target thread has keyboard focus
                if (_previousThreadId != 0)
                {
                    var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                    if (GetGUIThreadInfo(_previousThreadId, ref info) && info.hwndFocus != IntPtr.Zero)
                    {
                        LoggingService.Debug($"SmartPasteService: Focus ready after {waited}ms (focus={info.hwndFocus})");
                        return;
                    }
                }
                else
                {
                    // No thread ID captured — foreground match is good enough
                    LoggingService.Debug($"SmartPasteService: Foreground match after {waited}ms (no thread info)");
                    return;
                }
            }

            Thread.Sleep(pollIntervalMs);
            waited += pollIntervalMs;
        }

        LoggingService.Debug($"SmartPasteService: Focus wait timed out after {maxWaitMs}ms, proceeding with paste");
    }

    // =========================================================================
    // FOCUS DETECTION HELPERS
    // =========================================================================

    /// <summary>
    /// Resolves the process name from a window handle.
    /// </summary>
    private static string GetProcessNameFromWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SmartPasteService: GetProcessNameFromWindow failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Detects whether the focused UI element is a text field or password field
    /// using UI Automation. Runs on the UI thread with a 200ms timeout.
    ///
    /// Returns (isTextField, isPassword). On any failure returns (false, false)
    /// to fall back to blind paste.
    /// </summary>
    private static (bool isTextField, bool isPassword) DetectFocusedField()
    {
        try
        {
            // Run UIA call with timeout via Dispatcher.Invoke
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return (false, false);

            var result = dispatcher.Invoke(() =>
            {
                try
                {
                    var focused = AutomationElement.FocusedElement;
                    if (focused == null) return (false, false);

                    var controlType = focused.Current.ControlType;
                    bool isTextField = controlType.Id == ControlType.Edit.Id ||
                                       controlType.Id == ControlType.Document.Id;

                    bool isPassword = false;
                    try
                    {
                        isPassword = (bool)focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty);
                    }
                    catch
                    {
                        // IsPassword not supported for this element
                    }

                    return (isTextField, isPassword);
                }
                catch
                {
                    return (false, false);
                }
            }, TimeSpan.FromMilliseconds(200));

            return ((bool, bool))result;
        }
        catch (TimeoutException)
        {
            LoggingService.Debug("SmartPasteService: Focus detection timed out (200ms)");
            return (false, false);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SmartPasteService: Focus detection failed: {ex.Message}");
            return (false, false);
        }
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    /// <summary>
    /// Sets text on the clipboard, optionally excluding it from Windows clipboard
    /// history (Win+V) and third-party clipboard managers.
    ///
    /// Uses the "ExcludeClipboardContentFromMonitorProcessing" clipboard format,
    /// which is the standard Windows mechanism for hiding sensitive clipboard data.
    /// This is the same approach used by password managers (1Password, KeePass, etc.)
    /// and is the Windows equivalent of macOS's org.nspasteboard.ConcealedType.
    /// </summary>
    private static void SetClipboardText(string text)
    {
        if (SettingsService.Instance.HideFromClipboardHistory)
        {
            var dataObject = new DataObject();
            dataObject.SetData(DataFormats.UnicodeText, text);
            dataObject.SetData("ExcludeClipboardContentFromMonitorProcessing", "");
            Clipboard.SetDataObject(dataObject, true);
        }
        else
        {
            Clipboard.SetText(text);
        }
    }

    // =========================================================================
    // IDISPOSABLE IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Disposes of the service and cleans up resources.
    ///
    /// CLEANUP RESPONSIBILITIES:
    /// 1. Cancel any pending clipboard restoration
    /// 2. Dispose of the CancellationTokenSource
    /// 3. Clear saved clipboard data
    /// 4. Set disposed flag to prevent further operations
    ///
    /// This ensures proper cleanup when the app closes and prevents
    /// restoration callbacks from running after disposal.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_clipboardLock)
        {
            if (_disposed)
                return;

            _disposed = true;

            // Cancel any pending clipboard restoration
            CancelPendingRestore();

            // Clear saved clipboard data
            _savedClipboardData = null;

            LoggingService.Debug("SmartPasteService: Disposed");
        }
    }
}
