// PARAKEET TRANSCRIPTION SERVICE — INITIALIZE
// Starts the daemon for a model and waits for its READY line.

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    /// <param name="advanceTeardownGeneration">
    /// False for the auto-restart in <see cref="TranscribeAsync"/> (reloading the same model
    /// after a crash is not a teardown, so requests queued behind it stay current) and for
    /// <see cref="ReloadWhenIdleAsync"/>, which advanced it already when it found the daemon idle.
    /// </param>
    /// <param name="transcriptionLockHeld">
    /// True only from <see cref="ReloadWhenIdleAsync"/>, which holds the lock: the teardown
    /// must not wait on it or cancel anyone.
    /// </param>
    /// <param name="idleReloadCancellation">
    /// Only from <see cref="ReloadWhenIdleAsync"/>: cancelled by a GUI teardown. It cuts the
    /// old daemon's graceful-exit wait and the READY wait short; the load then stops the
    /// daemon it started and throws <see cref="OperationCanceledException"/>.
    /// </param>
    private async Task InitializeCoreAsync(
        string modelDirectory,
        string? language,
        bool advanceTeardownGeneration,
        bool transcriptionLockHeld = false,
        CancellationToken idleReloadCancellation = default)
    {
        LoggingService.Info("========== INITIALIZING PARAKEET TRANSCRIPTION SERVICE ==========");
        // The model id, not the directory. Models live under
        // %LOCALAPPDATA%\HyperWhisper\Models, so the directory carries the user's
        // Windows account name; the leaf folder IS the model id and is the only part
        // that answers "which model was this".
        LoggingService.Info($"  Model: {Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}");

        // Dispose any existing daemon first
        DisposeModelCore(advanceTeardownGeneration, transcriptionLockHeld, idleReloadCancellation);
        idleReloadCancellation.ThrowIfCancellationRequested();

        // Guards: daemon binary, model directory, VAD model. Outside the try below, so a
        // missing file throws its own code, not "Unexpected error during initialization".
        var paths = ParakeetDaemonLaunchPlan.ValidatePaths(modelDirectory);

        // The daemon THIS load starts. Every kill below targets it, never whatever
        // _daemonProcess holds by then: a failed or cancelled load must not stop a
        // daemon someone else started (#1608 review).
        Process? process = null;
        try
        {
            // STEP 1: Spawn the daemon process
            LoggingService.Info("Step 1: Spawning parakeet-engine daemon...");
            var launch = PlanDaemonLaunch(paths, modelDirectory, language);

            process = new Process { StartInfo = launch.StartInfo, EnableRaisingEvents = true };
            _daemonProcess = process;
            var (sinceStart, stderrCapture) = StartDaemonProcess(process);

            // STEP 3: Wait for the READY signal. ConfigureAwait(false), like every await
            // on this path: see WaitForDaemonReadyAsync (#1608).
            await WaitForDaemonReadyAsync(process, launch, modelDirectory, stderrCapture, sinceStart, idleReloadCancellation)
                .ConfigureAwait(false);

            LoggingService.Info("========== PARAKEET TRANSCRIPTION SERVICE READY ==========");
        }
        catch (TranscriptionException)
        {
            // Re-throw TranscriptionExceptions as-is
            throw;
        }
        catch (OperationCanceledException) when (idleReloadCancellation.IsCancellationRequested)
        {
            // The idle reload's cancel, already handled in WaitForDaemonReadyAsync; not a start failure.
            throw;
        }
        catch (Exception ex)
        {
            throw UnexpectedStartFailure(process, ex);
        }
    }

    /// <summary>
    /// Any other failure of a load: kills the daemon this load started, if any, and
    /// returns the <see cref="TranscriptionErrorCode.DaemonStartFailed"/> to throw.
    /// </summary>
    private static TranscriptionException UnexpectedStartFailure(Process? process, Exception ex)
    {
        LoggingService.Error("ParakeetTranscriptionService: Unexpected error during initialization", ex);
        // Only what this load started; null when it failed before the spawn.
        KillDaemonProcess(process);
        return new TranscriptionException(
            TranscriptionErrorCode.DaemonStartFailed,
            $"Failed to start Parakeet daemon: {ex.Message}",
            "Parakeet",
            ex);
    }

    /// <summary>
    /// The launch plan for this load. The engine flags are stored first, before the
    /// start info is built, exactly where the load always set them.
    /// </summary>
    private ParakeetDaemonLaunchPlan.LaunchSpec PlanDaemonLaunch(
        ParakeetDaemonLaunchPlan.DaemonPaths paths,
        string modelDirectory,
        string? language)
    {
        var engine = ParakeetDaemonLaunchPlan.ResolveEngine(modelDirectory);
        _isQwen3 = engine.IsQwen3;
        _isOnline = engine.IsOnline;
        return ParakeetDaemonLaunchPlan.Build(paths, modelDirectory, language, engine);
    }

    /// <summary>
    /// Starts the daemon this load created (crash detection registered first), takes its
    /// stdio, and starts the stderr pump (STEP 2). Returns the clock started at the spawn.
    /// </summary>
    private (Stopwatch SinceStart, StderrCapture StderrCapture) StartDaemonProcess(Process process)
    {
        // Register crash detection before starting
        process.Exited += OnDaemonExited;

        if (!process.Start())
        {
            LoggingService.Error("ParakeetTranscriptionService: Failed to start daemon process");
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                "Failed to start parakeet-engine process",
                "Parakeet");
        }
        var sinceStart = Stopwatch.StartNew();

        _stdinWriter = process.StandardInput;
        _stdoutReader = process.StandardOutput;

        LoggingService.Info($"  Daemon PID: {process.Id}");

        // STEP 2: Start background stderr reader for diagnostics
        LoggingService.Debug("Step 2: Starting stderr reader thread...");
        var stderrCapture = StartStderrReader(process);
        return (sinceStart, stderrCapture);
    }

    /// <summary>
    /// One start failure's diagnostic fields, from the failure slug, the daemon's exit
    /// code and the time since the spawn. See <see cref="BuildStartFailureFields"/>.
    /// </summary>
    private delegate IReadOnlyList<KeyValuePair<string, object>> StartFailureFieldsBuilder(
        string failure,
        int? exitCode,
        long elapsedMilliseconds);

    /// <summary>
    /// STEP 3: waits for the daemon's READY line, within the engine's READY budget and
    /// an idle reload's teardown token. Every failure stops the daemon this load
    /// started and throws <see cref="TranscriptionException"/>; a cancelled idle reload
    /// rethrows its <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task WaitForDaemonReadyAsync(
        Process process,
        ParakeetDaemonLaunchPlan.LaunchSpec launch,
        string modelDirectory,
        StderrCapture stderrCapture,
        Stopwatch sinceStart,
        CancellationToken idleReloadCancellation)
    {
        // STEP 3: Wait for READY signal on stdout.
        // Qwen3 loads ~1.2 GB of ONNX sessions on a cold cache — give it longer.
        var readySeconds = _isQwen3 ? 90 : (_isOnline ? 45 : 30);
        LoggingService.Info($"Step 3: Waiting for daemon READY signal ({readySeconds}s timeout)...");

        var readyTimeout = TimeSpan.FromSeconds(readySeconds);
        // An idle reload's teardown token ends the wait too (#1608 review); the catch
        // below tells it apart from the READY timeout.
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(idleReloadCancellation);
        readyCts.CancelAfter(readyTimeout);

        // The cause of a start failure, for its ERROR line and its one Sentry
        // report (#854). Called after the branch has its exit code and has
        // stopped the daemon, so the stderr tail is complete.
        IReadOnlyList<KeyValuePair<string, object>> StartFailureFields(string failure, int? exitCode, long elapsedMilliseconds)
        {
            var (lineCount, tail) = stderrCapture.TailSnapshot();
            return BuildStartFailureFields(
                failure,
                exitCode,
                launch.Engine.EngineArg,
                launch.Engine.ModelId,
                launch.SupportsLanguageHint ? launch.DaemonLanguage : null,
                readySeconds,
                elapsedMilliseconds,
                lineCount,
                tail,
                SentryService.RedactUserIdentifiers);
        }

        // WaitAsync, not only the read's token: a read on the daemon's pipe is not
        // guaranteed to observe cancellation, and a cancelled idle reload must hand the
        // lock back at once. The abandoned read ends when the daemon is killed below.
        var readLineTask = _stdoutReader!.ReadLineAsync(readyCts.Token).AsTask();
        try
        {
            // ConfigureAwait(false): ReloadWhenIdleAsync holds the transcription lock
            // across this wait, and DisposeModel can block the UI thread on that lock
            // until this load has seen its cancel, so the continuation must not need
            // the UI thread (#1608).
            var line = await readLineTask.WaitAsync(readyCts.Token).ConfigureAwait(false);

            if (line == null)
            {
                throw await StdoutClosedBeforeReadyAsync(process, modelDirectory, stderrCapture, sinceStart, StartFailureFields)
                    .ConfigureAwait(false);
            }

            await AcceptReadyLineAsync(line, process, launch, modelDirectory, stderrCapture, sinceStart, StartFailureFields)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idleReloadCancellation.IsCancellationRequested)
        {
            // A GUI teardown cancelled this idle reload: stop the daemon it started and
            // hand the lock back at once (ReloadWhenIdleAsync reports Cancelled).
            LoggingService.Info("ParakeetTranscriptionService: Idle reload cancelled while waiting for READY; stopping its daemon");
            StopDaemonInstance(process);
            _ = ObserveInFlightReadAsync(readLineTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw await ReadyTimedOutAsync(process, readLineTask, readySeconds, stderrCapture, sinceStart, StartFailureFields)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw await InvalidReadyJsonAsync(process, ex, stderrCapture, sinceStart, StartFailureFields)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The daemon's first stdout line: READY marks the service ready; an error line
    /// stops the daemon, reports the failure and throws. Malformed JSON throws
    /// <see cref="JsonException"/> for <see cref="WaitForDaemonReadyAsync"/> to report.
    /// </summary>
    private async Task AcceptReadyLineAsync(
        string line,
        Process process,
        ParakeetDaemonLaunchPlan.LaunchSpec launch,
        string modelDirectory,
        StderrCapture stderrCapture,
        Stopwatch sinceStart,
        StartFailureFieldsBuilder startFailureFields)
    {
        LoggingService.Debug($"ParakeetTranscriptionService: Received from daemon: {DescribeDaemonResponse(line)}");

        // Parse the READY JSON
        using var readyDoc = JsonDocument.Parse(line);
        var root = readyDoc.RootElement;

        if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "ready")
        {
            _activeProvider = root.TryGetProperty("provider", out var providerProp)
                ? providerProp.GetString()
                : "cpu";

            _isReady = true;
            _loadedModelId = Path.GetFileName(modelDirectory);
            _lastModelDirectory = modelDirectory;
            _lastLanguage = launch.LoadedLanguage;

            LoggingService.Info($"  Daemon is READY (provider: {_activeProvider})");
        }
        else
        {
            var errorMsg = root.TryGetProperty("error", out var errorProp)
                ? errorProp.GetString() ?? "Unknown error"
                : $"Unexpected response: {line}";

            var elapsedMs = sinceStart.ElapsedMilliseconds;
            var errorExitCode = TryGetEarlyExitCode(process, StartFailureDiagnosticWaitMilliseconds);
            KillDaemonProcess(process);

            // "Failed to load model" alone does not say WHY (#1598 review round 2):
            // the daemon sends it for a missing sherpa-onnx/onnxruntime DLL, a
            // type-initializer fault, OOM, a provider failure and a bad model file
            // alike. Its stderr carries the cause, written before this line, so
            // read that to its end (the daemon is gone) and mark the model broken
            // only when it names a fault in the model's own files.
            await stderrCapture.WaitForEndAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            ReportStartFailure(
                $"ParakeetTranscriptionService: Daemon reported error: {errorMsg}",
                startFailureFields(StartFailureDaemonError, errorExitCode, elapsedMs));
            if (IsModelFileLoadFailure(errorMsg, stderrCapture.Snapshot(), modelDirectory))
            {
                LocalModelHealth.MarkBroken(modelDirectory, "Parakeet engine could not load the model");
            }
            else if (IsModelLoadErrorResponse(errorMsg))
            {
                LoggingService.Warn("ParakeetTranscriptionService: The daemon's stderr names no fault in the model files (a runtime, provider or memory failure); the model is not marked broken");
            }
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                $"Parakeet daemon failed to initialize: {errorMsg}",
                "Parakeet");
        }
    }

    /// <summary>
    /// The daemon closed stdout before READY: stops it, reports the failure, marks the
    /// model broken on a model-load crash, and returns the exception to throw.
    /// </summary>
    private static async Task<TranscriptionException> StdoutClosedBeforeReadyAsync(
        Process process,
        string modelDirectory,
        StderrCapture stderrCapture,
        Stopwatch sinceStart,
        StartFailureFieldsBuilder startFailureFields)
    {
        var elapsedMs = sinceStart.ElapsedMilliseconds;
        var earlyExitCode = TryGetEarlyExitCode(process);
        KillDaemonProcess(process);
        await stderrCapture.WaitForEndAsync(TimeSpan.FromMilliseconds(StartFailureDiagnosticWaitMilliseconds)).ConfigureAwait(false);
        ReportStartFailure(
            "ParakeetTranscriptionService: Daemon closed stdout before sending READY",
            startFailureFields(StartFailureStdoutClosed, earlyExitCode, elapsedMs));

        // The daemon died while ONNX Runtime parsed the model: a damaged
        // ONNX file throws a native exception nothing catches, and the
        // engine fail-fasts with 0xC0000409 before READY (#1598). Mark the
        // model broken so it stops counting as installed and Model Library
        // offers it for download again (the only way out for Qwen3, whose
        // file sizes are not pinned). Any other exit (a missing DLL, an
        // access violation, OOM, a kill, a bad argument, still running)
        // is not something a re-download fixes, so it is left alone.
        if (IsModelLoadCrashExitCode(earlyExitCode))
        {
            LocalModelHealth.MarkBroken(modelDirectory, "Parakeet engine crashed while loading the model");
        }
        else
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Daemon exit code {FormatExitCode(earlyExitCode)} is not a model-load crash; the model is not marked broken");
        }
        return new TranscriptionException(
            TranscriptionErrorCode.DaemonStartFailed,
            "Parakeet daemon closed stdout before sending READY signal",
            "Parakeet");
    }

    /// <summary>
    /// No READY within the budget: stops the daemon, reports the failure, and returns
    /// the exception to throw.
    /// </summary>
    private static async Task<TranscriptionException> ReadyTimedOutAsync(
        Process process,
        Task<string?> readLineTask,
        int readySeconds,
        StderrCapture stderrCapture,
        Stopwatch sinceStart,
        StartFailureFieldsBuilder startFailureFields)
    {
        var elapsedMs = sinceStart.ElapsedMilliseconds;
        var timeoutExitCode = TryGetEarlyExitCode(process, StartFailureDiagnosticWaitMilliseconds);
        KillDaemonProcess(process);
        _ = ObserveInFlightReadAsync(readLineTask);
        await stderrCapture.WaitForEndAsync(TimeSpan.FromMilliseconds(StartFailureDiagnosticWaitMilliseconds)).ConfigureAwait(false);
        ReportStartFailure(
            $"ParakeetTranscriptionService: Daemon did not send READY within {readySeconds} seconds",
            startFailureFields(StartFailureReadyTimeout, timeoutExitCode, elapsedMs));
        return new TranscriptionException(
            TranscriptionErrorCode.DaemonStartFailed,
            $"Parakeet daemon timed out waiting for READY signal ({readySeconds}s)",
            "Parakeet");
    }

    /// <summary>
    /// The first stdout line was not JSON: stops the daemon, reports the failure, and
    /// returns the exception to throw.
    /// </summary>
    private static async Task<TranscriptionException> InvalidReadyJsonAsync(
        Process process,
        JsonException ex,
        StderrCapture stderrCapture,
        Stopwatch sinceStart,
        StartFailureFieldsBuilder startFailureFields)
    {
        var elapsedMs = sinceStart.ElapsedMilliseconds;
        var invalidJsonExitCode = TryGetEarlyExitCode(process, StartFailureDiagnosticWaitMilliseconds);
        KillDaemonProcess(process);
        await stderrCapture.WaitForEndAsync(TimeSpan.FromMilliseconds(StartFailureDiagnosticWaitMilliseconds)).ConfigureAwait(false);
        ReportStartFailure(
            "ParakeetTranscriptionService: Failed to parse daemon READY response",
            startFailureFields(StartFailureInvalidJson, invalidJsonExitCode, elapsedMs),
            ex);
        return new TranscriptionException(
            TranscriptionErrorCode.DaemonStartFailed,
            "Parakeet daemon sent invalid JSON on startup",
            "Parakeet",
            ex);
    }
}
