// PARAKEET DAEMON LAUNCH PLAN
// The pure first half of starting the parakeet-engine daemon: the path guards, the
// engine picked from the model catalog, and the ProcessStartInfo (arguments, working
// directory, stdio encoding). Nothing here starts a process or touches the service's
// state; ParakeetTranscriptionService.InitializeCoreAsync spawns the daemon from it.

using System.Diagnostics;
using System.IO;
using System.Text;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

internal static class ParakeetDaemonLaunchPlan
{
    /// <summary>The daemon binary and the Silero VAD model, both in the app directory.</summary>
    internal sealed record DaemonPaths(string DaemonPath, string VadModelPath);

    /// <summary>
    /// The engine the daemon loads for one model directory, from the model catalog.
    /// <see cref="ModelId"/> is the directory's leaf name, which is the catalog id.
    /// </summary>
    internal sealed record EngineSelection(string ModelId, string EngineArg, bool IsQwen3, bool IsOnline);

    /// <summary>
    /// What the service needs to start the daemon and to describe a failed start.
    /// <see cref="LoadedLanguage"/> is the language the spawn is recorded with (null
    /// becomes "auto"); <see cref="DaemonLanguage"/> is the <c>--language</c> value.
    /// </summary>
    internal sealed record LaunchSpec(
        ProcessStartInfo StartInfo,
        EngineSelection Engine,
        bool SupportsLanguageHint,
        string LoadedLanguage,
        string DaemonLanguage);

    /// <summary>
    /// Resolves the absolute path to the parakeet-engine.exe daemon binary.
    /// </summary>
    private static string GetDaemonPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "parakeet-engine", "parakeet-engine.exe");
    }

    /// <summary>
    /// Resolves the absolute path to the Silero VAD ONNX model used by the daemon.
    /// </summary>
    private static string GetVadModelPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "parakeet-engine", "silero_vad.onnx");
    }

    /// <summary>
    /// The path guards, in their order: a missing daemon binary throws
    /// <see cref="TranscriptionErrorCode.DaemonStartFailed"/>, a missing model directory
    /// <see cref="TranscriptionErrorCode.OnnxModelFileMissing"/>; a missing VAD model is
    /// only logged, and the daemon runs without VAD.
    /// </summary>
    internal static DaemonPaths ValidatePaths(string modelDirectory)
    {
        var daemonPath = GetDaemonPath();
        var vadModelPath = GetVadModelPath();

        // Guard: validate daemon binary exists
        if (!File.Exists(daemonPath))
        {
            LoggingService.Error("ParakeetTranscriptionService: Daemon binary not found in the app directory");
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                // The path is out of the message too, not just the log line: this
                // message travels one frame up and is logged again as ex.Message by
                // TranscriptionRetryHandler and MainViewModel. GetUserMessage() is a
                // fixed sentence for this code, so nothing the user sees changes.
                "Parakeet engine binary not found in the app directory",
                "Parakeet");
        }

        // Guard: validate model directory exists
        if (!Directory.Exists(modelDirectory))
        {
            LoggingService.Error("ParakeetTranscriptionService: Model directory not found");
            throw new TranscriptionException(
                TranscriptionErrorCode.OnnxModelFileMissing,
                // Same reason as above - GetUserMessage() covers this code too.
                "Model directory not found",
                "Parakeet");
        }

        // Guard: validate VAD model exists
        if (!File.Exists(vadModelPath))
        {
            LoggingService.Warn("ParakeetTranscriptionService: VAD model not found in the app directory, proceeding without VAD");
        }

        return new DaemonPaths(daemonPath, vadModelPath);
    }

    /// <summary>
    /// The engine for <paramref name="modelDirectory"/>. A pure catalog lookup: the
    /// service stores <see cref="EngineSelection.IsQwen3"/> and
    /// <see cref="EngineSelection.IsOnline"/> before it calls <see cref="Build"/>.
    /// </summary>
    internal static EngineSelection ResolveEngine(string modelDirectory)
    {
        // Resolve which engine the daemon should load from the model catalog
        // (the model directory's leaf name is the model Id). Falls back to the
        // Parakeet transducer for anything not in the catalog.
        var modelId = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
        var engineArg = modelInfo?.DaemonEngineArg ?? "nemo_transducer";
        return new EngineSelection(
            modelId,
            engineArg,
            IsQwen3: modelInfo?.Engine == ParakeetEngine.Qwen3,
            IsOnline: modelInfo?.Engine == ParakeetEngine.NemotronMl);
    }

    /// <summary>
    /// The daemon's <see cref="ProcessStartInfo"/>: its arguments, working directory
    /// and stdio encoding, and the language the spawn is recorded with.
    /// </summary>
    internal static LaunchSpec Build(DaemonPaths paths, string modelDirectory, string? language, EngineSelection engine)
    {
        var engineArg = engine.EngineArg;
        LoggingService.Info($"  Engine: {engineArg}");

        // The caller passes null for "auto-detect" (MainViewModel converts the
        // "auto" UI value to null). Qwen3 and Nemotron have real auto language
        // handling, so forward "auto" instead of defaulting those engines to English.
        var supportsLanguageHint = engine.IsOnline || engine.IsQwen3;
        var loadedLanguage = language ?? "auto";
        var daemonLanguage = supportsLanguageHint ? loadedLanguage : "auto";
        if (supportsLanguageHint)
        {
            LoggingService.Info($"  Language: {daemonLanguage}");
        }
        else
        {
            LoggingService.Info($"  Requested language '{loadedLanguage}' is auto-detected by Parakeet TDT (not applied)");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = paths.DaemonPath,
            // Pin the daemon's working directory to its own folder. The child inherits
            // HyperWhisper.exe's CWD by default (often C:\Users\<user> when launched from
            // the Start Menu), and the Windows DLL search order probes the CWD before %PATH%.
            // Pinning it to the engine folder — where the legit ONNX Runtime / DirectML
            // native DLLs are installed — prevents DLL planting from a user-writable CWD.
            WorkingDirectory = Path.GetDirectoryName(paths.DaemonPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The daemon speaks raw UTF-8 on stdin/stdout (it puts both in _O_BINARY and
            // passes non-ASCII bytes through unescaped). Without these, redirected-stream
            // encoding defaults to the console code page — a legacy ANSI/OEM page on most
            // non-US Windows locales — which mojibakes accented/Cyrillic/Greek/CJK output
            // from multilingual Parakeet. UTF8Encoding(false) suppresses the BOM so the
            // stdin writer never prepends EF BB BF to the first JSON request line.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };

        // Build arguments via ArgumentList so .NET applies correct Windows
        // quoting/escaping. String concatenation corrupts paths containing a
        // quote or a trailing backslash (e.g. a UNC path like \\server\share\),
        // where CommandLineToArgvW treats the trailing \" as an escaped quote.
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelDirectory);
        if (supportsLanguageHint)
        {
            startInfo.ArgumentList.Add("--language");
            startInfo.ArgumentList.Add(daemonLanguage);
        }
        else if (!string.IsNullOrWhiteSpace(language))
        {
            // Read only by a daemon older than --segment-join below: the
            // committed x64 parakeet-engine.exe predates it, and skips an
            // unknown flag and its value. Drop it once that exe is rebuilt.
            startInfo.ArgumentList.Add("--join-language");
            startInfo.ArgumentList.Add(language);
        }
        // The host, not the daemon, decides the segment join (#902). Nothing is
        // sent for auto: the daemon resolves an auto join itself.
        var segmentJoin = ParakeetTranscriptionService.ResolveSegmentJoinArgument(language);
        if (segmentJoin != null)
        {
            startInfo.ArgumentList.Add("--segment-join");
            startInfo.ArgumentList.Add(segmentJoin);
        }
        startInfo.ArgumentList.Add("--vad-model");
        startInfo.ArgumentList.Add(paths.VadModelPath);
        startInfo.ArgumentList.Add("--engine");
        startInfo.ArgumentList.Add(engineArg);

        // The argument SHAPE, not the command line. ArgumentList holds the model
        // directory and the VAD model path, both under the user's profile, so
        // joining it printed their Windows account name into the log file. The
        // engine, the language hint and the argument count are what a failed
        // start actually needs.
        LoggingService.Debug(
            $"ParakeetTranscriptionService: Starting daemon (engine={engineArg}, " +
            $"language_hint={(supportsLanguageHint ? daemonLanguage : "n/a")}, " +
            $"join_language={(!supportsLanguageHint && !string.IsNullOrWhiteSpace(language) ? language : "n/a")}, " +
            $"segment_join={segmentJoin ?? "n/a"}, " +
            $"arg_count={startInfo.ArgumentList.Count})");

        return new LaunchSpec(startInfo, engine, supportsLanguageHint, loadedLanguage, daemonLanguage);
    }
}
