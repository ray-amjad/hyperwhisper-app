// APPLICATION CONTEXT SERVICE
// Captures foreground application context (window title, process name, focused element type,
// app category) for LLM post-processing prompt enrichment.
//
// DESIGN:
// - Singleton with dedicated STA thread for UI Automation calls
// - Win32 P/Invoke for fast window/process info (always works)
// - UIA for focused element type (200ms timeout, graceful degradation)
// - Self-detection: returns null if foreground is HyperWhisper
// - Never throws: partial results on failure, null on self-detection
//
// USAGE:
//   var context = ApplicationContextService.Instance.GatherContext();
//   // Pass to PromptBuilder for <APPLICATION_CONTEXT> XML block

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;
using HyperWhisper.Services.AppClassification;

namespace HyperWhisper.Services;

// =========================================================================
// DATA MODEL
// =========================================================================

/// <summary>
/// Captured context about the foreground application at recording start.
/// Used to enrich LLM post-processing prompts with application-aware context.
/// </summary>
public class ApplicationContext
{
    /// <summary>Process name without extension, e.g., "chrome", "Code".</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>Full window title, e.g., "GitHub - Google Chrome".</summary>
    public string WindowTitle { get; init; } = "";

    /// <summary>Application category, e.g., "Web Browser", "Code Editor".</summary>
    public string Category { get; init; } = "";

    /// <summary>Parsed browser tab title (suffix stripped), or null if not a browser.</summary>
    public string? BrowserTabTitle { get; init; }

    /// <summary>Browser host/domain when available. Kept separate from full URL for privacy.</summary>
    public string? BrowserHost { get; init; }

    /// <summary>Simplified focused element type, e.g., "TextField", "Document".</summary>
    public string? FocusedElementType { get; init; }

    /// <summary>Selected text or element value, truncated to 100 chars. Null if unavailable or password field.</summary>
    public string? FocusedContent { get; init; }

    /// <summary>Inferred text format hint, e.g., "code", "text", "email".</summary>
    public string? TextFormat { get; init; }

    /// <summary>Deterministic app classification used for app-aware formatting.</summary>
    public AppType AppType { get; init; } = AppType.Other;

    /// <summary>Confidence for AppType: strong, medium, weak, unknown.</summary>
    public string AppTypeConfidence { get; init; } = "unknown";

    /// <summary>Signal that produced AppType: processName, title, focusedElement, default.</summary>
    public string AppTypeSource { get; init; } = "default";

    /// <summary>OCR-extracted text from the screen at recording start. Set externally after GatherContext().</summary>
    public string? ScreenOCRText { get; set; }
}

// =========================================================================
// SERVICE
// =========================================================================

/// <summary>
/// Singleton service that captures foreground application context for LLM prompt enrichment.
///
/// USAGE:
///   var context = ApplicationContextService.Instance.GatherContext();
///   // context is null if foreground app is HyperWhisper itself
///   // context has partial data if UIA times out or fails
///
/// LIFECYCLE:
///   - Instance is created lazily on first access
///   - STA thread starts immediately and persists for the app lifetime
///   - Call Dispose() on app shutdown to clean up the STA thread
/// </summary>
public class ApplicationContextService : IDisposable
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static ApplicationContextService? _instance;
    private static readonly object _lock = new();

    public static ApplicationContextService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new ApplicationContextService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // WIN32 P/INVOKE DECLARATIONS
    // =========================================================================

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    // =========================================================================
    // APP CATEGORY MAPPING
    // =========================================================================

    /// <summary>
    /// Maps process exe names (case-insensitive) to application categories.
    /// Covers the most commonly used Windows applications.
    /// </summary>
    private static readonly Dictionary<string, string> AppCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Web Browsers
        { "chrome", "Web Browser" },
        { "msedge", "Web Browser" },
        { "firefox", "Web Browser" },
        { "brave", "Web Browser" },
        { "opera", "Web Browser" },
        { "vivaldi", "Web Browser" },
        { "arc", "Web Browser" },

        // Code Editors
        { "Code", "Code Editor" },
        { "Cursor", "Code Editor" },
        { "Windsurf", "Code Editor" },
        { "devenv", "Code Editor" },

        // Email Clients
        { "OUTLOOK", "Email Client" },
        { "thunderbird", "Email Client" },

        // Communication
        { "slack", "Communication" },
        { "Discord", "Communication" },
        { "Teams", "Communication" },

        // Office Applications
        { "WINWORD", "Word Processor" },
        { "POWERPNT", "Presentation" },
        { "EXCEL", "Spreadsheet" },

        // Note Taking
        { "notepad", "Note Taking" },

        // Terminals
        { "WindowsTerminal", "Terminal" },
        { "cmd", "Terminal" },
        { "powershell", "Terminal" },
        { "pwsh", "Terminal" },
        { "wt", "Terminal" },

        // JetBrains IDEs
        { "idea64", "IDE" },
        { "webstorm64", "IDE" },
        { "pycharm64", "IDE" },
        { "rider64", "IDE" },
        { "phpstorm64", "IDE" },
        { "goland64", "IDE" },
        { "clion64", "IDE" },
        { "rubymine64", "IDE" },
        { "datagrip64", "IDE" },
    };

    // =========================================================================
    // TEXT FORMAT INFERENCE
    // =========================================================================

    /// <summary>
    /// Maps app categories to text format hints for the LLM.
    /// </summary>
    private static readonly Dictionary<string, string> TextFormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Code Editor", "code" },
        { "IDE", "code" },
        { "Terminal", "command" },
        { "Email Client", "email" },
        { "Web Browser", "url" },
    };

    // =========================================================================
    // BROWSER SUFFIX STRIPPING
    // =========================================================================

    /// <summary>
    /// Known browser suffixes to strip from window titles to extract tab titles.
    /// Ordered longest-first to avoid partial matches.
    /// Includes both hyphen-minus (-) and em-dash (\u2014) variants.
    /// </summary>
    private static readonly string[] BrowserSuffixes =
    [
        " \u2014 Google Chrome",
        " - Google Chrome",
        " \u2014 Microsoft Edge",
        " - Microsoft Edge",
        " \u2014 Mozilla Firefox",
        " - Mozilla Firefox",
        " \u2014 Brave",
        " - Brave",
        " \u2014 Opera",
        " - Opera",
        " \u2014 Vivaldi",
        " - Vivaldi",
        " - Arc",
    ];

    // =========================================================================
    // ELEMENT TYPE MAPPING
    // =========================================================================

    /// <summary>
    /// Maps UIA ControlType IDs to simplified element type categories.
    /// Uses ControlType.Id (int) for efficient lookup.
    /// </summary>
    private static readonly Dictionary<int, string> ElementTypeMap = new()
    {
        { ControlType.Edit.Id, "TextField" },
        { ControlType.Document.Id, "Document" },
        { ControlType.Text.Id, "Text" },
        { ControlType.ComboBox.Id, "TextField" },
        { ControlType.DataGrid.Id, "Spreadsheet" },
        { ControlType.Table.Id, "Spreadsheet" },
        { ControlType.Button.Id, "Button" },
        { ControlType.ListItem.Id, "ListItem" },
        { ControlType.TreeItem.Id, "ListItem" },
    };

    /// <summary>
    /// Set of browser process names for Pane -> WebContent mapping.
    /// </summary>
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    private static readonly Dictionary<string, string[]> AddressBarHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = ["Address and search bar"],
        ["msedge"] = ["Address and search bar"],
        ["brave"] = ["Address and search bar"],
        ["firefox"] = ["Search with Google or enter address", "Search or enter address", "urlbar-input"],
        ["arc"] = ["Address"],
        ["opera"] = ["Address field", "Address and search bar"],
        ["vivaldi"] = ["Address field", "Address and search bar"],
    };

    // =========================================================================
    // INSTANCE FIELDS
    // =========================================================================

    private Thread? _staThread;
    private Dispatcher? _staDispatcher;
    private readonly ManualResetEventSlim _staReady = new(false);
    private bool _disposed;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private ApplicationContextService()
    {
        StartStaThread();
    }

    /// <summary>
    /// Starts the persistent STA background thread with a Dispatcher message pump.
    /// The thread stays alive for the app lifetime to avoid repeated thread creation.
    /// </summary>
    private void StartStaThread()
    {
        _staThread = new Thread(() =>
        {
            try
            {
                // Capture the dispatcher for this STA thread
                _staDispatcher = Dispatcher.CurrentDispatcher;
                _staReady.Set();

                // Run the message pump — blocks until Dispatcher.InvokeShutdown() is called
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                LoggingService.Error("ApplicationContextService: STA thread crashed", ex);
            }
        })
        {
            Name = "ApplicationContextService-STA",
            IsBackground = true,
        };

        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        // Wait for the STA thread to be ready (Dispatcher created)
        if (!_staReady.Wait(TimeSpan.FromSeconds(5)))
        {
            LoggingService.Warn("ApplicationContextService: STA thread did not start within 5 seconds");
        }
        else
        {
            LoggingService.Info("ApplicationContextService: STA thread started successfully");
        }
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Gathers context about the current foreground application.
    ///
    /// CALL TIMING: Must be called BEFORE showing the recording overlay,
    /// because the overlay will steal focus and change the foreground window.
    ///
    /// RETURNS:
    /// - ApplicationContext with as much data as could be gathered
    /// - null if the foreground app is HyperWhisper itself (self-detection)
    /// - Partial data on timeout or failure (process name + window title always available)
    ///
    /// PERFORMANCE: Typically completes in 5-50ms. Hard timeout of 200ms on UIA calls.
    /// </summary>
    public ApplicationContext? GatherContext()
    {
        try
        {
            // -----------------------------------------------------------------
            // Step 1: Get foreground window handle (Win32, fast, always works)
            // -----------------------------------------------------------------
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                LoggingService.Debug("ApplicationContextService: No foreground window");
                return null;
            }

            // -----------------------------------------------------------------
            // Step 2: Get window title (Win32)
            // -----------------------------------------------------------------
            var windowTitle = GetWindowTitleText(hwnd);

            // -----------------------------------------------------------------
            // Step 3: Get process name (Win32 + ApplicationFrameHost resolution)
            // -----------------------------------------------------------------
            var processName = GetProcessFromWindow(hwnd);
            if (string.IsNullOrEmpty(processName))
            {
                LoggingService.Debug("ApplicationContextService: Could not resolve process name");
                return null;
            }

            // -----------------------------------------------------------------
            // Step 4: Self-detection — skip if foreground is HyperWhisper
            // -----------------------------------------------------------------
            if (processName.Equals("HyperWhisper", StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.Debug("ApplicationContextService: Foreground is HyperWhisper, skipping context");
                return null;
            }

            // -----------------------------------------------------------------
            // Step 5: Categorize the application
            // -----------------------------------------------------------------
            var category = CategorizeApp(processName);

            // -----------------------------------------------------------------
            // Step 6: Parse browser tab title (if applicable)
            // -----------------------------------------------------------------
            var browserTabTitle = ParseBrowserTabTitle(windowTitle, category);
            var browserHost = ExtractBrowserHost(hwnd, processName, category);

            // -----------------------------------------------------------------
            // Step 7: Get focused element type + content via UIA (200ms timeout)
            // -----------------------------------------------------------------
            var focusedInfo = GetFocusedElementInfo(processName);
            var focusedElementType = focusedInfo?.ElementType;
            var focusedContent = focusedInfo?.Content;

            // -----------------------------------------------------------------
            // Step 8: Infer text format from category
            // -----------------------------------------------------------------
            var textFormat = InferTextFormat(category);

            // Override text format if webmail detected in browser tab
            if (string.Equals(category, "Web Browser", StringComparison.OrdinalIgnoreCase)
                && browserTabTitle != null
                && IsWebmail(browserTabTitle))
            {
                textFormat = "email";
            }

            var appClassification = AppTypeClassifier.Shared.Classify(
                processName,
                browserHost,
                browserHost == null ? "unknown" : "medium",
                windowTitle,
                browserTabTitle,
                focusedElementType,
                focusedContent);

            if (appClassification.AppType == AppType.Other
                && string.Equals(category, "Web Browser", StringComparison.OrdinalIgnoreCase)
                && browserTabTitle != null
                && IsWebmail(browserTabTitle))
            {
                appClassification = new AppClassificationResult(
                    AppType.Email,
                    "weak",
                    "webmailTitleFallback",
                    null);
            }

            if (appClassification.AppType != AppType.Other)
            {
                category = appClassification.AppType.ToCategory();
                textFormat = appClassification.AppType.ToTextFormat();
            }

            var context = new ApplicationContext
            {
                ProcessName = processName,
                WindowTitle = windowTitle,
                Category = category,
                BrowserTabTitle = browserTabTitle,
                BrowserHost = browserHost,
                FocusedElementType = focusedElementType,
                FocusedContent = focusedContent,
                TextFormat = textFormat,
                AppType = appClassification.AppType,
                AppTypeConfidence = appClassification.Confidence,
                AppTypeSource = appClassification.Source,
            };

            LoggingService.Info($"ApplicationContextService: {processName}, {category}" +
                $", appType={appClassification.AppType.ToPromptValue()}/{appClassification.Confidence}/{appClassification.Source}" +
                (focusedElementType != null ? $", {focusedElementType}" : "") +
                (focusedContent != null ? $", content_len={focusedContent.Length}" : "") +
                (browserHost != null ? $", host=\"{browserHost}\"" : "") +
                (browserTabTitle != null ? $", tab=\"{browserTabTitle}\"" : ""));

            return context;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ApplicationContextService: GatherContext failed", ex);
            return null;
        }
    }

    // =========================================================================
    // WIN32 HELPERS
    // =========================================================================

    /// <summary>
    /// Gets the window title text via Win32 GetWindowText.
    /// </summary>
    private static string GetWindowTitleText(IntPtr hwnd)
    {
        try
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return "";

            var buffer = new char[length + 1];
            int copied = GetWindowText(hwnd, buffer, buffer.Length);
            return copied > 0 ? new string(buffer, 0, copied) : "";
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: GetWindowTitleText failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Resolves the process name from a window handle.
    /// Handles the ApplicationFrameHost case for UWP apps by enumerating child windows.
    ///
    /// APPLICATION FRAME HOST:
    /// UWP apps run inside ApplicationFrameHost.exe. The actual app process owns a child
    /// window. We enumerate children to find one owned by a different process.
    /// </summary>
    private static string GetProcessFromWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return "";

            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;

            // Handle UWP apps hosted in ApplicationFrameHost
            if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                var resolvedName = ResolveUwpProcessName(hwnd, pid);
                if (!string.IsNullOrEmpty(resolvedName))
                {
                    return resolvedName;
                }
            }

            return processName;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: GetProcessFromWindow failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// For ApplicationFrameHost windows, enumerates child windows to find the actual
    /// UWP app process. Returns the process name of the first child window owned by
    /// a different process, or null if none found.
    /// </summary>
    private static string? ResolveUwpProcessName(IntPtr parentHwnd, uint parentPid)
    {
        string? resolvedName = null;

        try
        {
            EnumChildWindows(parentHwnd, (childHwnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(childHwnd, out uint childPid);
                    if (childPid != 0 && childPid != parentPid)
                    {
                        using var childProcess = Process.GetProcessById((int)childPid);
                        resolvedName = childProcess.ProcessName;
                        return false; // Stop enumeration
                    }
                }
                catch
                {
                    // Skip this child window and continue
                }
                return true; // Continue enumeration
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: ResolveUwpProcessName failed: {ex.Message}");
        }

        return resolvedName;
    }

    // =========================================================================
    // APP CATEGORIZATION
    // =========================================================================

    /// <summary>
    /// Maps a process name to an application category.
    /// Returns empty string if the process is not recognized.
    /// </summary>
    private static string CategorizeApp(string processName)
    {
        return AppCategoryMap.TryGetValue(processName, out var category) ? category : "";
    }

    // =========================================================================
    // BROWSER TAB TITLE PARSING
    // =========================================================================

    /// <summary>
    /// Extracts the browser tab title by stripping known browser suffixes from the window title.
    /// Returns null if:
    /// - The app is not a browser
    /// - The window title is empty
    /// - The entire title is just the browser suffix (no actual tab title)
    /// </summary>
    private static string? ParseBrowserTabTitle(string windowTitle, string category)
    {
        if (!category.Equals("Web Browser", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrEmpty(windowTitle))
            return null;

        foreach (var suffix in BrowserSuffixes)
        {
            if (windowTitle.EndsWith(suffix, StringComparison.Ordinal))
            {
                var tabTitle = windowTitle[..^suffix.Length];

                // If the title equals the suffix exactly (no tab title), return null
                return string.IsNullOrWhiteSpace(tabTitle) ? null : tabTitle;
            }
        }

        // No known suffix found — return the full title as a fallback
        return windowTitle;
    }

    /// <summary>
    /// Best-effort browser host extraction via UI Automation. Some browsers expose the
    /// address bar as an Edit control even when page content has focus.
    /// </summary>
    private string? ExtractBrowserHost(IntPtr hwnd, string processName, string category)
    {
        if (!category.Equals("Web Browser", StringComparison.OrdinalIgnoreCase)
            || !BrowserProcessNames.Contains(processName)
            || _staDispatcher == null
            || _disposed)
        {
            return null;
        }

        try
        {
            return (string?)_staDispatcher.Invoke(() =>
            {
                return ExtractBrowserHostOnStaThread(hwnd, processName);
            }, TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
            LoggingService.Debug("ApplicationContextService: Browser host UIA scan timed out (200ms)");
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: Browser host extraction failed: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractBrowserHostOnStaThread(IntPtr hwnd, string processName)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            if (!AddressBarHints.TryGetValue(processName, out var names))
                return null;

            foreach (var name in names)
            {
                var element = root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, name)));
                if (element == null)
                    continue;

                var host = ExtractHostFromText(GetValueViaValuePattern(element));
                if (host != null)
                    return host;
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: Browser host UIA query failed: {ex.Message}");
        }

        return null;
    }

    private static readonly Regex EmailLike = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    private static string? ExtractHostFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (EmailLike.IsMatch(trimmed))
            return null;

        if (!trimmed.Contains('.') || trimmed.Contains(' '))
            return null;

        var candidate = trimmed.Contains("://") ? trimmed : "https://" + trimmed;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }

        return null;
    }

    // =========================================================================
    // UI AUTOMATION — FOCUSED ELEMENT
    // =========================================================================

    /// <summary>Carries both element type and content from a single STA dispatch.</summary>
    private record FocusedElementResult(string? ElementType, string? Content);

    /// <summary>
    /// Gets the type and content of the currently focused UI element via UI Automation.
    /// Runs on the STA thread with a 200ms timeout.
    ///
    /// TIMEOUT BEHAVIOR:
    /// If the UIA call takes longer than 200ms (e.g., hung application), returns null.
    /// The caller receives partial context (process name + window title) without the
    /// focused element info, which is acceptable for prompt enrichment.
    /// </summary>
    private FocusedElementResult? GetFocusedElementInfo(string processName)
    {
        if (_staDispatcher == null || _disposed)
        {
            LoggingService.Debug("ApplicationContextService: STA dispatcher not available");
            return null;
        }

        try
        {
            return (FocusedElementResult?)_staDispatcher.Invoke(() =>
            {
                return GetFocusedElementInfoOnStaThread(processName);
            }, TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
            LoggingService.Debug("ApplicationContextService: UIA call timed out (200ms)");
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: GetFocusedElementInfo failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Actual UIA work that runs on the STA thread.
    /// Gets the focused element, maps its ControlType, and captures content in one pass.
    /// </summary>
    private static FocusedElementResult? GetFocusedElementInfoOnStaThread(string processName)
    {
        try
        {
            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement == null)
                return null;

            var controlType = focusedElement.Current.ControlType;
            if (controlType == null)
                return null;

            // Determine element type
            string elementType;
            if (controlType.Id == ControlType.Pane.Id &&
                BrowserProcessNames.Contains(processName))
            {
                elementType = "WebContent";
            }
            else if (ElementTypeMap.TryGetValue(controlType.Id, out var simplified))
            {
                elementType = simplified;
            }
            else
            {
                elementType = "Other";
            }

            // Capture content
            var content = GetFocusedContent(focusedElement);

            return new FocusedElementResult(elementType, content);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: UIA element query failed: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // UI AUTOMATION — FOCUSED CONTENT
    // =========================================================================

    /// <summary>
    /// Gets the selected text or value of the focused element.
    /// Returns null for password fields, unsupported elements, or on any error.
    /// </summary>
    private static string? GetFocusedContent(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword)
                return null;
        }
        catch
        {
            // IsPassword check failed — safe to continue, content capture may still work
        }

        // Try selected text first (more useful than full value)
        var selected = GetSelectedTextViaTextPattern(element);
        if (!string.IsNullOrEmpty(selected))
            return Truncate(selected, 100);

        // Fall back to element value
        var value = GetValueViaValuePattern(element);
        if (!string.IsNullOrEmpty(value))
            return Truncate(value, 100);

        return null;
    }

    /// <summary>
    /// Attempts to get the selected text via TextPattern.GetSelection().
    /// </summary>
    private static string? GetSelectedTextViaTextPattern(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
                pattern is TextPattern textPattern)
            {
                var selection = textPattern.GetSelection();
                if (selection.Length > 0)
                {
                    var text = selection[0].GetText(-1);
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: TextPattern failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Attempts to get the element value via ValuePattern.
    /// </summary>
    private static string? GetValueViaValuePattern(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value;
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: ValuePattern failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Truncates a string to maxLength characters, appending "..." if truncated.
    /// </summary>
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        return text[..maxLength] + "...";
    }

    // =========================================================================
    // TEXT FORMAT INFERENCE
    // =========================================================================

    private static readonly string[] WebmailKeywords =
    [
        "gmail", "inbox", "mail.google",
        "outlook.live", "outlook.office",
        "mail.yahoo", "yahoo mail",
        "protonmail", "proton mail",
        "hey.com",
        "fastmail",
        "icloud.com/mail", "icloud mail",
        "zoho mail",
        "aol mail"
    ];

    private static bool IsWebmail(string tabTitle)
    {
        var lower = tabTitle.ToLowerInvariant();
        return WebmailKeywords.Any(keyword => lower.Contains(keyword));
    }

    /// <summary>
    /// Infers the text format hint from the application category.
    /// Returns "text" as the default for unrecognized categories.
    /// </summary>
    private static string InferTextFormat(string category)
    {
        if (string.IsNullOrEmpty(category))
            return "text";

        return TextFormatMap.TryGetValue(category, out var format) ? format : "text";
    }

    // =========================================================================
    // IDISPOSABLE
    // =========================================================================

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // Shut down the STA thread's Dispatcher, which will cause Dispatcher.Run() to return
            _staDispatcher?.InvokeShutdown();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: Error shutting down STA dispatcher: {ex.Message}");
        }

        try
        {
            // Wait for the STA thread to finish
            if (_staThread != null && _staThread.IsAlive)
            {
                if (!_staThread.Join(TimeSpan.FromSeconds(2)))
                {
                    LoggingService.Warn("ApplicationContextService: STA thread did not stop within 2 seconds");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ApplicationContextService: Error joining STA thread: {ex.Message}");
        }

        _staDispatcher = null;
        _staThread = null;
        _staReady.Dispose();

        LoggingService.Info("ApplicationContextService: Disposed");

        GC.SuppressFinalize(this);
    }
}
