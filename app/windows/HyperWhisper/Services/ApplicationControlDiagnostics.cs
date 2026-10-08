using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace HyperWhisper.Services;

internal static class ApplicationControlDiagnostics
{
    internal const string ClassifierAssemblyName = "HyperWhisper.AppClassification.dll";
    private const string ClassifierAssemblySimpleName = "HyperWhisper.AppClassification";
    private static readonly Lazy<Snapshot> ClassifierSnapshot = new(Inspect, LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _registered;
    private static int _reported;

    internal sealed record Snapshot(
        bool AssemblyPresent,
        long? AssemblyFileSizeBytes,
        string AuthenticodeStatus,
        int? WinVerifyTrustHResult,
        string? TrustProbeExceptionType,
        int? TrustProbeExceptionHResult,
        string ZoneStreamStatus,
        int? ZoneId,
        string InspectionStage);

    internal sealed record Payload(
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyDictionary<string, object> Extras);

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args) =>
        HandleFirstChanceException(
            args.Exception,
            () => ClassifierSnapshot.Value,
            ReportFailure,
            Unregister);

    private static void Unregister()
    {
        if (Interlocked.Exchange(ref _registered, 0) == 0)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
    }

    internal static bool IsClassifierLoadFailure(Exception exception)
    {
        if (exception is not FileLoadException fileLoadException)
        {
            return false;
        }

        try
        {
            var unresolvedName = fileLoadException.FileName;
            if (string.IsNullOrWhiteSpace(unresolvedName))
            {
                return false;
            }

            if (string.Equals(unresolvedName, ClassifierAssemblySimpleName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(unresolvedName), ClassifierAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var displayNameParts = unresolvedName.Split(',', StringSplitOptions.TrimEntries);
            if (displayNameParts.Length < 2
                || !string.Equals(displayNameParts[0], ClassifierAssemblySimpleName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var index = 1; index < displayNameParts.Length; index++)
            {
                var attribute = displayNameParts[index];
                var equalsIndex = attribute.IndexOf('=');
                if (equalsIndex <= 0 || equalsIndex == attribute.Length - 1)
                {
                    return false;
                }

                var key = attribute[..equalsIndex].Trim();
                if (key is not ("Version" or "Culture" or "PublicKeyToken" or
                    "ProcessorArchitecture" or "Retargetable" or "ContentType"))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ----- #933: a block on ANY binary, mandatory ones included -----------------
    //
    // Application Control can block HyperWhisper.SharedCore.dll (a managed
    // FileLoadException) or the native hyperwhisper_core.dll (a DllNotFoundException
    // under a TypeInitializationException). Both arrive inside a XamlParseException
    // while WPF builds MainWindow, and both carry a sentence the OS localizes, so
    // the generic crash path splits one fault into one Sentry issue per language.
    // The code below recognises the block by its HRESULT, names the file by its file
    // name only, and builds one fixed report and one fixed notice.
    //
    // Everything here must keep working when SharedCore and the native core are
    // blocked: it names no type from either, and it reads no setting.

    /// <summary>ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION as an HRESULT.</summary>
    internal const int ApplicationControlBlockedHResult = unchecked((int)0x800711C7);

    /// <summary>The resource key of the notice the user sees.</summary>
    internal const string BlockedNoticeKey = "errors.applicationControl.blocked";

    /// <summary>The resource key of the notice title (shared with the generic crash box).</summary>
    internal const string BlockedNoticeTitleKey = "errors.unhandled.title";

    /// <summary>
    /// The English notice, used when the string catalog cannot be read. A blocked
    /// binary can be a satellite resource assembly too, so the lookup can throw.
    /// </summary>
    internal const string BlockedNoticeFallback =
        "Windows Application Control blocked {0}, a file HyperWhisper needs. " +
        "Ask your IT administrator to allow HyperWhisper, or check Smart App Control in Windows Security.";

    internal const string BlockedNoticeTitleFallback = "HyperWhisper Error";

    /// <summary>The file name reported when none can be derived safely.</summary>
    internal const string UnknownBlockedFile = "unknown";

    private const string BlockedHResultText = "0x800711C7";
    private const int MaxChainLength = 32;

    private static readonly Regex SafeFileName =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    private static readonly Regex QuotedLibraryName =
        new(@"'([^']{1,260})'", RegexOptions.CultureInvariant);

    internal sealed record BlockReport(
        string Message,
        string[] Fingerprint,
        string DedupeKey,
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyDictionary<string, object> Extras);

    /// <summary>
    /// True when Application Control blocked a binary anywhere in
    /// <paramref name="exception"/>'s chain: every InnerException, and every inner
    /// exception of an AggregateException.
    /// </summary>
    internal static bool IsApplicationControlBlock(Exception? exception) =>
        FindApplicationControlBlock(exception) != null;

    /// <summary>The exception in the chain that carries the block, or null.</summary>
    internal static Exception? FindApplicationControlBlock(Exception? exception)
    {
        foreach (var candidate in Chain(exception))
        {
            if (IsBlockedLoad(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The blocked binary's FILE NAME, never its directory: the install path holds
    /// the Windows account name. Returns <see cref="UnknownBlockedFile"/> when no
    /// name passes a strict file-name check.
    /// </summary>
    internal static string DescribeBlockedAssembly(Exception exception)
    {
        var blocked = FindApplicationControlBlock(exception) ?? exception;
        try
        {
            var raw = blocked switch
            {
                FileLoadException fileLoad => fileLoad.FileName,
                FileNotFoundException fileNotFound => fileNotFound.FileName,
                BadImageFormatException badImage => badImage.FileName,
                // "Unable to load DLL 'hyperwhisper_core' or one of its dependencies: ..."
                // The runtime part of the message is not localized; the OS sentence
                // after the colon is.
                DllNotFoundException dllNotFound => ReadQuotedLibraryName(dllNotFound.Message),
                _ => null,
            };

            return SanitizeFileName(raw);
        }
        catch
        {
            return UnknownBlockedFile;
        }
    }

    /// <summary>
    /// The one Sentry report for a block. Built only from the file name, the fixed
    /// HRESULT, the stage and exception TYPE names: never a message, never a path,
    /// so every OS language produces the same event and groups as one issue.
    /// </summary>
    internal static BlockReport BuildBlockReport(Exception exception, string stage)
    {
        var blocked = FindApplicationControlBlock(exception) ?? exception;
        var fileName = DescribeBlockedAssembly(exception);
        var hresult = OptionalAssemblyGuard.DescribeHResult(ApplicationControlBlockedHResult);
        var matchedBy = blocked.HResult == ApplicationControlBlockedHResult ? "hresult" : "message_code";

        return new(
            "Application Control blocked a required file",
            ["application-control", "blocked", fileName],
            $"application-control:blocked:{fileName}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["component"] = "application_control",
                ["diagnostic_name"] = "application_control_blocked",
                ["blocked_file_name"] = fileName,
                ["capture_stage"] = stage,
            },
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["blocked_file_name"] = fileName,
                ["blocked_hresult"] = hresult,
                ["blocked_match"] = matchedBy,
                ["blocked_exception_type"] = ExceptionType(blocked),
                ["outer_exception_type"] = ExceptionType(exception),
                ["capture_stage"] = stage,
            });
    }

    /// <summary>
    /// Reports <paramref name="exception"/> through <paramref name="send"/> when it
    /// is a block, and returns whether it was one. A failing reporter is logged and
    /// swallowed: the notice after it must still reach the user.
    /// </summary>
    internal static bool TryReportBlock(Exception? exception, string stage, Action<BlockReport> send)
    {
        if (exception == null || !IsApplicationControlBlock(exception))
        {
            return false;
        }

        BlockReport report;
        try
        {
            report = BuildBlockReport(exception, stage);
        }
        catch (Exception buildException)
        {
            LoggingService.Error(
                $"ApplicationControlDiagnostics: Block report build failed " +
                $"(exception_type={buildException.GetType().Name}, hresult={HResult(buildException.HResult)})");
            return true;
        }

        LoggingService.Error(
            $"ApplicationControlDiagnostics: Application Control blocked a required file " +
            $"(file={report.Tags["blocked_file_name"]}, stage={stage}, " +
            $"hresult={report.Extras["blocked_hresult"]}, match={report.Extras["blocked_match"]})");

        try
        {
            send(report);
        }
        catch (Exception reportException)
        {
            LoggingService.Error(
                $"ApplicationControlDiagnostics: Block report failed " +
                $"(exception_type={reportException.GetType().Name}, hresult={HResult(reportException.HResult)})");
        }

        return true;
    }

    /// <summary>
    /// Sends a block report to Sentry once per file per process. No setting is read
    /// here: SentryService is only initialized when the user opted in, and the
    /// opt-out shuts it down.
    /// </summary>
    internal static void SendBlockReport(BlockReport report) =>
        SentryService.CaptureDiagnosticEvent(
            message: report.Message,
            extras: new(report.Extras, StringComparer.Ordinal),
            tags: new(report.Tags, StringComparer.Ordinal),
            fingerprint: report.Fingerprint,
            dedupeKey: report.DedupeKey);

    /// <summary>
    /// The title and text of the notice. <paramref name="lookup"/> is the string
    /// catalog; when it throws, or returns the key itself, the English text is used.
    /// </summary>
    internal static (string Title, string Message) BuildBlockedNotice(
        string fileName,
        Func<string, string> lookup)
    {
        var title = Lookup(lookup, BlockedNoticeTitleKey, BlockedNoticeTitleFallback);
        var format = Lookup(lookup, BlockedNoticeKey, BlockedNoticeFallback);
        string message;
        try
        {
            message = string.Format(CultureInfo.CurrentCulture, format, fileName);
        }
        catch (FormatException)
        {
            message = string.Format(CultureInfo.InvariantCulture, BlockedNoticeFallback, fileName);
        }

        return (title, message);
    }

    private static string Lookup(Func<string, string> lookup, string key, string fallback)
    {
        try
        {
            var value = lookup(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsBlockedLoad(Exception exception)
    {
        try
        {
            if (!OptionalAssemblyGuard.IsLoadFailure(exception))
            {
                return false;
            }

            if (exception.HResult == ApplicationControlBlockedHResult)
            {
                return true;
            }

            // A DllNotFoundException keeps COR_E_DLLNOTFOUND as its own HResult. The
            // OS error is only in the message, as "(0x800711C7)" after a localized
            // sentence; the hex code itself is the same in every language.
            return exception is DllNotFoundException
                && exception.Message.Contains(BlockedHResultText, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        if (exception == null)
        {
            yield break;
        }

        var pending = new Queue<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        while (pending.Count > 0 && seen.Count < MaxChainLength)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is { } next)
            {
                pending.Enqueue(next);
            }
        }
    }

    private static string? ReadQuotedLibraryName(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var match = QuotedLibraryName.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UnknownBlockedFile;
        }

        // The directory goes first, by hand rather than Path.GetFileName so both
        // separators are handled the same on every OS. A directory can hold a comma
        // ("Doe, Jane"), so the display-name cut comes after it.
        var name = raw.Trim();
        name = name[(name.LastIndexOfAny(['\\', '/']) + 1)..];

        // An assembly display name: "HyperWhisper.SharedCore, Version=1.0.0.0, ..."
        var comma = name.IndexOf(',');
        if (comma >= 0)
        {
            name = name[..comma];
        }

        name = name.Trim();
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".dll";
        }

        return SafeFileName.IsMatch(name) ? name : UnknownBlockedFile;
    }

    internal static bool HandleFirstChanceException(
        Exception exception,
        Func<Snapshot> inspect,
        Action<Payload> report,
        Action unsubscribe)
    {
        if (!IsClassifierLoadFailure(exception) || Volatile.Read(ref _reported) != 0)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _reported, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            Snapshot snapshot;
            try
            {
                snapshot = inspect();
            }
            catch (Exception inspectionException)
            {
                ProbeFailed("inspection", inspectionException);
                snapshot = new(false, null, "check_failed", null, null, null, "unreadable", null, "inspection_failed");
            }

            var payload = BuildPayload(snapshot, exception);
            LoggingService.Error(
                $"ApplicationControlDiagnostics: Classifier load blocked " +
                $"(capture_stage=first_chance_exception, classifier_load_succeeded=false, " +
                $"outer_exception_type={payload.Extras["classifier_outer_exception_type"]}, " +
                $"outer_hresult={payload.Extras["classifier_outer_hresult"]}, " +
                $"innermost_exception_type={payload.Extras["classifier_innermost_exception_type"]}, " +
                $"innermost_hresult={payload.Extras["classifier_innermost_hresult"]})");

            try
            {
                report(payload);
            }
            catch (Exception reportException)
            {
                LoggingService.Error(
                    $"ApplicationControlDiagnostics: Failure report failed " +
                    $"(exception_type={reportException.GetType().Name}, hresult={HResult(reportException.HResult)})");
            }

            return true;
        }
        finally
        {
            try
            {
                unsubscribe();
            }
            catch (Exception unsubscribeException)
            {
                LoggingService.Error(
                    $"ApplicationControlDiagnostics: First-chance handler removal failed " +
                    $"(exception_type={unsubscribeException.GetType().Name}, hresult={HResult(unsubscribeException.HResult)})");
            }
        }
    }

    internal static Snapshot Inspect()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ClassifierAssemblyName);
        var file = new FileInfo(path);
        bool present;
        try
        {
            _ = File.GetAttributes(path);
            present = true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            present = false;
        }
        catch (Exception exception)
        {
            ProbeFailed("assembly_presence", exception);
            return Log(new(false, null, "check_failed", null, null, null, "unreadable", null, "assembly_presence_failed"));
        }

        if (!present)
        {
            return Log(new(false, null, "not_checked", null, null, null, "absent", null, "assembly_not_found"));
        }

        long? size = null;
        int? trustResult = null;
        string? trustProbeExceptionType = null;
        int? trustProbeExceptionHResult = null;
        var trustStatus = "check_failed";
        var zoneStatus = "unreadable";
        int? zoneId = null;
        var inspectionStage = "complete";

        try
        {
            size = file.Length;
        }
        catch (Exception exception)
        {
            inspectionStage = "file_metadata_failed";
            ProbeFailed("file_metadata", exception);
        }

        try
        {
            trustResult = VerifyTrust(path);
            trustStatus = DescribeTrustStatus(trustResult);
        }
        catch (Exception exception)
        {
            inspectionStage = "trust_check_failed";
            trustProbeExceptionType = ExceptionType(exception);
            trustProbeExceptionHResult = exception.HResult;
            ProbeFailed("winverifytrust", exception);
        }

        try
        {
            (zoneStatus, zoneId) = ReadZoneId(path);
        }
        catch (Exception exception)
        {
            inspectionStage = "zone_check_failed";
            ProbeFailed("zone_identifier", exception);
        }

        return Log(new(
            true,
            size,
            trustStatus,
            trustResult,
            trustProbeExceptionType,
            trustProbeExceptionHResult,
            zoneStatus,
            zoneId,
            inspectionStage));
    }

    internal static string DescribeTrustStatus(int? result) => result switch
    {
        0 => "trusted",
        unchecked((int)0x800B0100) => "unsigned",
        unchecked((int)0x800B0001) => "provider_unknown",
        unchecked((int)0x800B0003) => "subject_form_unknown",
        null => "check_failed",
        _ => "untrusted"
    };

    internal static (string Status, int? ZoneId) ParseZoneId(string text) =>
        ParseZoneId(new StringReader(text));

    internal static Payload BuildPayload(
        Snapshot snapshot,
        Exception exception)
    {
        var inner = exception;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }

        return new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["component"] = "application_context",
                ["diagnostic_name"] = "classifier_load_failed",
                ["classifier_assembly_name"] = ClassifierAssemblyName,
                ["classifier_authenticode_status"] = snapshot.AuthenticodeStatus,
                ["classifier_zone_stream_status"] = snapshot.ZoneStreamStatus,
                ["classifier_inspection_stage"] = snapshot.InspectionStage,
                ["capture_stage"] = "first_chance_exception"
            },
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["classifier_assembly_present"] = snapshot.AssemblyPresent,
                ["classifier_file_size_bytes"] = (object?)snapshot.AssemblyFileSizeBytes ?? "unknown",
                ["classifier_winverifytrust_hresult"] = HResult(snapshot.WinVerifyTrustHResult),
                ["classifier_trust_probe_exception_type"] = snapshot.TrustProbeExceptionType ?? "none",
                ["classifier_trust_probe_exception_hresult"] = HResult(snapshot.TrustProbeExceptionHResult),
                ["classifier_zone_id"] = (object?)snapshot.ZoneId ?? "unknown",
                ["classifier_load_succeeded"] = false,
                ["classifier_outer_exception_type"] = ExceptionType(exception),
                ["classifier_outer_hresult"] = HResult(exception.HResult),
                ["classifier_innermost_exception_type"] = ExceptionType(inner),
                ["classifier_innermost_hresult"] = HResult(inner.HResult)
            });
    }

    private static void ReportFailure(Payload payload)
    {
        if (!SettingsService.Instance.EnableErrorLogging)
        {
            return;
        }

        // Do not send the original exception. FileLoadException can put an installed
        // user path in its message and FileName property. The payload retains only
        // the exception type and HRESULT needed to identify the enforcement path.
        SentryService.CaptureDiagnosticEvent(
            message: "Application context classifier load failed",
            extras: new(payload.Extras, StringComparer.Ordinal),
            tags: new(payload.Tags, StringComparer.Ordinal),
            fingerprint: ["application-control", "classifier-load-failed"],
            dedupeKey: "application-control:classifier-load-failed");
    }

    private static (string Status, int? ZoneId) ReadZoneId(string path)
    {
        try
        {
            using var reader = new StreamReader(File.OpenRead(path + ":Zone.Identifier"));
            return ParseZoneId(reader);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ("absent", null);
        }
    }

    private static (string Status, int? ZoneId) ParseZoneId(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.Trim().StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line.Trim()["ZoneId=".Length..].Trim();
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is >= 0 and <= 4
                ? ("present", id)
                : ("invalid", null);
        }

        return ("invalid", null);
    }

    private static Snapshot Log(Snapshot snapshot)
    {
        LoggingService.Info(
            $"ApplicationControlDiagnostics: Classifier assembly inspected " +
            $"(assembly_name={ClassifierAssemblyName}, assembly_present={snapshot.AssemblyPresent}, " +
            $"file_size_bytes={snapshot.AssemblyFileSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
            $"authenticode_status={snapshot.AuthenticodeStatus}, " +
            $"winverifytrust_hresult={HResult(snapshot.WinVerifyTrustHResult)}, " +
            $"trust_probe_exception_type={snapshot.TrustProbeExceptionType ?? "none"}, " +
            $"trust_probe_exception_hresult={HResult(snapshot.TrustProbeExceptionHResult)}, " +
            $"zone_stream_status={snapshot.ZoneStreamStatus}, " +
            $"zone_id={snapshot.ZoneId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
            $"inspection_stage={snapshot.InspectionStage})");
        return snapshot;
    }

    private static void ProbeFailed(string stage, Exception exception) =>
        LoggingService.Error(
            $"ApplicationControlDiagnostics: Assembly probe failed " +
            $"(stage={stage}, exception_type={exception.GetType().Name}, hresult={HResult(exception.HResult)})");

    private static string ExceptionType(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    private static string HResult(int? value) => value.HasValue
        ? $"0x{unchecked((uint)value.Value):X8}"
        : "not_checked";

    private static int VerifyTrust(string path)
    {
        var file = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var marshalled = false;
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            marshalled = true;
            var data = new WinTrustData(filePointer);
            return WinVerifyTrust(IntPtr.Zero, new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), ref data);
        }
        finally
        {
            if (marshalled)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            }
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo(string path)
    {
        internal uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)] internal string FilePath = path;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData(IntPtr file)
    {
        internal uint StructSize = (uint)Marshal.SizeOf<WinTrustData>();
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice = 2;
        internal uint RevocationChecks;
        internal uint UnionChoice = 1;
        internal IntPtr File = file;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags = 0x00001000;
        internal uint UiContext;
    }
}
