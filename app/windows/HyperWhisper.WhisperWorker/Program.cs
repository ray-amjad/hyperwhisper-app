using System.Runtime.InteropServices;
using System.Text;
using HyperWhisper.LocalInference;
using HyperWhisper.WhisperWorker;
using Whisper.net.LibraryLoader;

// HyperWhisper.WhisperWorker: the out-of-process home of Windows on-device
// Whisper (issue #1194). The protocol is WhisperWorkerProtocol, one JSON object
// per line. stdout carries the protocol and nothing else; everything a library
// prints goes to stderr, which the app copies into its log.

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var protocolOut = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };
var log = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
Console.SetOut(log);
Console.SetError(log);
var input = new StreamReader(Console.OpenStandardInput(), utf8);

var host = new WorkerHost(
    new LocalWhisperService(),
    protocolOut,
    log,
    WorkerRuntime.Order(Environment.GetEnvironmentVariable(WorkerRuntime.OrderVariable), log),
    requestThreadFactory: RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
    WorkerRuntime.LoadedName);

log.WriteLine($"HyperWhisper.WhisperWorker {typeof(WorkerHost).Assembly.GetName().Version} "
    + $"({RuntimeInformation.ProcessArchitecture}, pid {Environment.ProcessId})");
return await host.RunAsync(input, Environment.ProcessId);

internal static class WorkerRuntime
{
    /// <summary>
    /// Diagnostics only: a comma list of Whisper.net runtimes (for example
    /// "Cpu,CpuNoAvx") that replaces the default order. The tests use it to make
    /// the runtime fail to load.
    /// </summary>
    public const string OrderVariable = "HW_WHISPER_WORKER_RUNTIMES";

    /// <summary>The order the app forced in-process before #1194: Vulkan, then CPU, then CPU without AVX.</summary>
    public static readonly IReadOnlyList<RuntimeLibrary> DefaultOrder =
        [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

    public static IReadOnlyList<RuntimeLibrary> Order(string? overrideValue, TextWriter? log = null)
    {
        if (string.IsNullOrWhiteSpace(overrideValue)) return DefaultOrder;
        var order = new List<RuntimeLibrary>();
        foreach (var part in overrideValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<RuntimeLibrary>(part, ignoreCase: true, out var library)) order.Add(library);
            else log?.WriteLine($"Ignored unknown runtime '{part}' in {OrderVariable}");
        }
        if (order.Count == 0) return DefaultOrder;
        log?.WriteLine($"Runtime order from {OrderVariable}: {string.Join(", ", order)}");
        return order;
    }

    /// <summary>The protocol name of the runtime Whisper.net loaded, or null before any load.</summary>
    public static string? LoadedName() => Name(RuntimeOptions.LoadedLibrary);

    public static string? Name(RuntimeLibrary? library) => library switch
    {
        null => null,
        RuntimeLibrary.Vulkan => "vulkan",
        RuntimeLibrary.Cpu => "cpu",
        RuntimeLibrary.CpuNoAvx => "cpu-noavx",
        var other => other.ToString()!.ToLowerInvariant(),
    };
}
