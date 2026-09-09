using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
