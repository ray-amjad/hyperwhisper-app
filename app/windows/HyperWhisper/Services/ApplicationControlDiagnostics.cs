using System.Globalization;
using System.Runtime.InteropServices;

namespace HyperWhisper.Services;

/// <summary>
/// Collects privacy-safe metadata about the classifier assembly before its first load.
/// The resolved file path stays local to this class and never enters a snapshot or payload.
/// </summary>
internal static class ApplicationControlDiagnostics
{
    internal const string ClassifierAssemblyName = "HyperWhisper.AppClassification.dll";

    private const int TrustSuccess = 0;
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustENoSignature = unchecked((int)0x800B0100);

    internal sealed record Snapshot(
        bool AssemblyPresent,
        long? AssemblyFileSizeBytes,
        string AuthenticodeStatus,
        int? WinVerifyTrustHResult,
        string ZoneStreamStatus,
        int? ZoneId,
        string InspectionStage)
    {
        internal string AssemblyName => ClassifierAssemblyName;
    }

    internal sealed record Payload(
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyDictionary<string, object> Extras,
        IReadOnlyList<string> Fingerprint);

    /// <summary>
    /// Inspects the fixed classifier assembly. Probe failures never expose the resolved path.
    /// </summary>
    internal static Snapshot InspectClassifierAssembly()
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, ClassifierAssemblyName);
        bool assemblyPresent;

        try
        {
            _ = File.GetAttributes(assemblyPath);
            assemblyPresent = true;
        }
        catch (FileNotFoundException)
        {
            assemblyPresent = false;
        }
        catch (DirectoryNotFoundException)
        {
            assemblyPresent = false;
        }
        catch (Exception ex)
        {
            LogProbeFailure("assembly_presence", ex);
            return LogSnapshot(new Snapshot(
                AssemblyPresent: false,
                AssemblyFileSizeBytes: null,
                AuthenticodeStatus: "check_failed",
                WinVerifyTrustHResult: null,
                ZoneStreamStatus: "unreadable",
                ZoneId: null,
                InspectionStage: "assembly_presence_failed"));
        }

        if (!assemblyPresent)
        {
            return LogSnapshot(new Snapshot(
                AssemblyPresent: false,
                AssemblyFileSizeBytes: null,
                AuthenticodeStatus: "not_checked",
                WinVerifyTrustHResult: null,
                ZoneStreamStatus: "absent",
                ZoneId: null,
                InspectionStage: "assembly_not_found"));
        }

        var inspectionStage = "complete";
        long? fileSizeBytes = null;
        int? trustHResult = null;
        var trustStatus = "check_failed";
        var zoneStatus = "unreadable";
        int? zoneId = null;

        try
        {
            fileSizeBytes = new FileInfo(assemblyPath).Length;
        }
        catch (Exception ex)
        {
            inspectionStage = "file_metadata_failed";
            LogProbeFailure("file_metadata", ex);
        }

        try
        {
            trustHResult = WinTrust.VerifyFile(assemblyPath);
            trustStatus = DescribeTrustStatus(trustHResult);
        }
        catch (Exception ex)
        {
            inspectionStage = "trust_check_failed";
            trustHResult = ex.HResult;
            LogProbeFailure("winverifytrust", ex);
        }

        try
        {
            (zoneStatus, zoneId) = ReadZoneIdentifier(assemblyPath);
        }
        catch (Exception ex)
        {
            inspectionStage = "zone_check_failed";
            zoneStatus = "unreadable";
            LogProbeFailure("zone_identifier", ex);
        }

        return LogSnapshot(new Snapshot(
            assemblyPresent,
            fileSizeBytes,
            trustStatus,
            trustHResult,
            zoneStatus,
            zoneId,
            inspectionStage));
    }

    /// <summary>Maps the WinVerifyTrust result to a fixed, low-cardinality status.</summary>
    internal static string DescribeTrustStatus(int? hResult) => hResult switch
    {
        TrustSuccess => "trusted",
        TrustEProviderUnknown => "unsigned",
        TrustESubjectFormUnknown => "unsigned",
        TrustENoSignature => "unsigned",
        null => "check_failed",
        _ => "untrusted"
    };

    /// <summary>
    /// Reads only a numeric ZoneId from Zone.Identifier content.
    /// URL fields and all other text are ignored.
    /// </summary>
    internal static (string Status, int? ZoneId) ParseZoneId(string streamText)
    {
        using var reader = new StringReader(streamText);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed["ZoneId=".Length..].Trim();
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var zoneId) &&
                zoneId is >= 0 and <= 4)
            {
                return ("present", zoneId);
            }

            return ("invalid", null);
        }

        return ("invalid", null);
    }

    /// <summary>Builds the complete stable Sentry shape without sending an event.</summary>
    internal static Payload BuildPayload(Snapshot snapshot)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["component"] = "application_context",
            ["diagnostic_name"] = "classifier_load_failed",
            ["classifier_assembly_name"] = snapshot.AssemblyName,
            ["classifier_authenticode_status"] = snapshot.AuthenticodeStatus,
            ["classifier_zone_stream_status"] = snapshot.ZoneStreamStatus,
            ["classifier_inspection_stage"] = snapshot.InspectionStage
        };

        var extras = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["classifier_assembly_present"] = snapshot.AssemblyPresent,
            ["classifier_file_size_bytes"] = (object?)snapshot.AssemblyFileSizeBytes ?? "unknown",
            ["classifier_winverifytrust_hresult"] = FormatHResult(snapshot.WinVerifyTrustHResult),
            ["classifier_zone_id"] = (object?)snapshot.ZoneId ?? "unknown"
        };

        return new Payload(
            tags,
            extras,
            new[] { "application-control", "classifier-load-failed" });
    }

    private static (string Status, int? ZoneId) ReadZoneIdentifier(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath + ":Zone.Identifier");
            using var reader = new StreamReader(stream);
            return ParseZoneId(reader.ReadToEnd());
        }
        catch (FileNotFoundException)
        {
            return ("absent", null);
        }
        catch (DirectoryNotFoundException)
        {
            return ("absent", null);
        }
    }

    private static string FormatHResult(int? hResult) =>
        hResult.HasValue
            ? $"0x{unchecked((uint)hResult.Value):X8}"
            : "not_checked";

    private static Snapshot LogSnapshot(Snapshot snapshot)
    {
        LoggingService.Info(
            "ApplicationControlDiagnostics: Classifier assembly inspected " +
            $"(assembly_name={snapshot.AssemblyName}, " +
            $"assembly_present={snapshot.AssemblyPresent}, " +
            $"file_size_bytes={snapshot.AssemblyFileSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
            $"authenticode_status={snapshot.AuthenticodeStatus}, " +
            $"winverifytrust_hresult={FormatHResult(snapshot.WinVerifyTrustHResult)}, " +
            $"zone_stream_status={snapshot.ZoneStreamStatus}, " +
            $"zone_id={snapshot.ZoneId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
            $"inspection_stage={snapshot.InspectionStage})");

        return snapshot;
    }

    private static void LogProbeFailure(string stage, Exception exception)
    {
        LoggingService.Error(
            "ApplicationControlDiagnostics: Assembly inspection probe failed " +
            $"(stage={stage}, exception_type={exception.GetType().Name}, " +
            $"hresult={FormatHResult(exception.HResult)})");
    }

    private static class WinTrust
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionIgnore = 0;
        private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

        internal static int VerifyFile(string filePath)
        {
            var filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfoPointer = IntPtr.Zero;

            try
            {
                var fileInfo = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePathPointer
                };

                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

                var trustData = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    File = fileInfoPointer,
                    StateAction = WtdStateActionIgnore,
                    ProviderFlags = WtdCacheOnlyUrlRetrieval
                };

                return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref trustData);
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(fileInfoPointer);
                }

                Marshal.FreeCoTaskMem(filePathPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            ref WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            internal uint StructSize;
            internal IntPtr FilePath;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            internal uint StructSize;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr File;
            internal uint StateAction;
            internal IntPtr StateData;
            internal IntPtr UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
        }
    }
}
