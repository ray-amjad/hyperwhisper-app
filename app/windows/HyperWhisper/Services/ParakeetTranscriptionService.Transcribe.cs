// PARAKEET TRANSCRIPTION SERVICE — TRANSCRIBE
// The stdio request/response for one transcription, its auto-restart, and the
// on-device vocabulary pass.

using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    private async Task<string> TranscribeEnteredAsync(
        string audioPath,
        IReadOnlyList<string>? vocabulary,
        long teardownGeneration,
        CancellationToken cancellationToken)
    {
        // Guard: validate audio file exists
        if (!File.Exists(audioPath))
        {
            LoggingService.Error($"ParakeetTranscriptionService: Audio file not found: {LoggingService.DescribePath(audioPath)}");
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {LoggingService.DescribePath(audioPath)}",
                "Parakeet");
        }

        // Log warning for vocabulary — Parakeet TDT does not support it
        if (vocabulary != null && vocabulary.Count > 0)
        {
            LoggingService.Warn("ParakeetTranscriptionService: Parakeet TDT does not support vocabulary boosting — vocabulary will be ignored");
        }

        try
        {
            return await TranscribeInternalAsync(audioPath, teardownGeneration, cancellationToken);
        }
        catch (TranscriptionException ex) when (ex.Code == TranscriptionErrorCode.DaemonCrashed)
        {
            if (!ShouldAutoRestart(ex.Code, teardownGeneration, CurrentTeardownGeneration))
            {
                // A teardown overtook this request: reloading _lastModelDirectory would undo it.
                LoggingService.Info("ParakeetTranscriptionService: Daemon failure after a model teardown; not auto-restarting");
                throw CreateTeardownCancelledException(ex);
            }

            // Auto-restart: attempt to restart daemon and retry once
            LoggingService.Warn("ParakeetTranscriptionService: Daemon crashed during transcription, attempting auto-restart...");

            if (_lastModelDirectory == null)
            {
                LoggingService.Error("ParakeetTranscriptionService: Cannot auto-restart — no previous model directory");
                throw;
            }

            try
            {
                await InitializeCoreAsync(_lastModelDirectory, _lastLanguage, advanceTeardownGeneration: false);
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart successful, retrying transcription...");
                // Same entry generation: a teardown during the restart makes the retry stale.
                return await TranscribeInternalAsync(audioPath, teardownGeneration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart retry cancelled by caller");
                throw;
            }
            catch (TranscriptionException cancelledEx) when (cancelledEx.Code == TranscriptionErrorCode.Cancelled)
            {
                // A model teardown ended the retry; keep it Cancelled, not "restart failed".
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart retry ended by model teardown");
                throw;
            }
            catch (Exception restartEx)
            {
                LoggingService.Error("ParakeetTranscriptionService: Auto-restart failed", restartEx);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon crashed and auto-restart failed",
                    "Parakeet",
                    restartEx);
            }
        }
    }

    /// <summary>
    /// The on-device phonetic vocabulary pass over the raw engine result, via the
    /// shared Rust core (<c>hw-phonetic</c>, issue #283). It corrects a misheard
    /// token towards a spelling-hint row (a vocabulary row with NO replacement);
    /// the core skips every row that carries a replacement.
    ///
    /// <see cref="TranscribeAsync(string, string?, IReadOnlyList{string}?, CancellationToken)"/>
    /// returns the engine's text untouched. <see cref="Transcription.TranscriptionOrchestrator"/>
    /// calls this once, AFTER it has kept that text as <c>RawText</c> (the History
    /// row's raw transcript), and then runs its own <c>\b</c>-anchored
    /// <see cref="VocabularyProcessor"/> pass once over the result (issue #1596).
    ///
    /// There used to be a second local pass here: the unanchored,
    /// diacritic-insensitive <c>ApplySubstringVocabulary</c>, a copy of the macOS
    /// local providers' shape. It is gone on Windows (issue #1596, Ray's decision
    /// of 2026-10-09). It matched inside words ("art" -> "ART" turned "quarterly"
    /// into "quARTerly"), and the orchestrator's <c>\b</c> pass then applied every
    /// replacement row a second time ("fox" -> "fox terrier" gave
    /// "fox terrier terrier"). Replacement rows are now applied by the <c>\b</c>
    /// pass alone, whole words only, exactly as on Whisper. macOS and Linux are
    /// tracked in #1622.
    ///
    /// Why this cannot apply a replacement twice: the phonetic pass only ever
    /// writes a hint row's own spelling, never a replacement value, and the
    /// <c>\b</c> pass runs each replacement row once per transcription
    /// (<c>replace_all</c> never rescans the text it inserted).
    ///
    /// TDT + Nemotron only. Qwen3 gets no phonetic pass on macOS
    /// (Qwen3AsrProvider), so it is gated here too.
    ///
    /// Vocabulary is GLOBAL (all items, no settings gate) — macOS parity. One
    /// core call per transcription; the core owns its own process-wide code
    /// cache, so a fresh read of the vocabulary each time can never be stale.
    /// </summary>
    public string ApplyLocalVocabularyCorrection(string rawText)
        // Read _isQwen3 once: a model switch between the transcription and this
        // call is the only way it could disagree with the engine that produced
        // rawText.
        => ApplyLocalVocabularyCorrection(rawText, VocabularyService.Instance.GetAll, _isQwen3);

    /// <summary>
    /// Describes one daemon response line for the log without the transcript
    /// (#1645). The result line is <c>{"text":"…","duration_ms":N}</c>, and the
    /// log ships in the Export Diagnostics bundle, which promises no transcripts.
    ///
    /// Numbers, booleans and nulls are kept as they are. A string is kept only
    /// for <c>status</c>, <c>provider</c> and <c>error</c>, which the engine fills
    /// from fixed text; every other string, <c>text</c> included, is reduced to
    /// its length. A line that is not a JSON object is reduced to its length.
    /// </summary>
    internal static string DescribeDaemonResponse(string? responseLine)
    {
        if (string.IsNullOrEmpty(responseLine))
        {
            return "(empty)";
        }

        try
        {
            using var document = JsonDocument.Parse(responseLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return $"(JSON {root.ValueKind}, {responseLine.Length} chars)";
            }

            var parts = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                parts.Add(value.ValueKind switch
                {
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                        => $"{property.Name}={value.GetRawText()}",
                    JsonValueKind.String when s_daemonResponsePlainStringKeys.Contains(property.Name)
                        => $"{property.Name}={value.GetString()}",
                    JsonValueKind.String
                        => $"{property.Name}=<{value.GetString()?.Length ?? 0} chars>",
                    _ => $"{property.Name}=<{value.ValueKind}>"
                });
            }

            return parts.Count == 0 ? "{}" : string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return $"(not JSON, {responseLine.Length} chars)";
        }
    }

    private static readonly HashSet<string> s_daemonResponsePlainStringKeys =
        new(StringComparer.Ordinal) { "status", "provider", "error" };

    /// <summary>
    /// <see cref="ApplyLocalVocabularyCorrection(string)"/> with the vocabulary
    /// source and the engine passed in, so a test can drive the real core call
    /// without a loaded model.
    /// </summary>
    internal static string ApplyLocalVocabularyCorrection(
        string rawText,
        Func<IEnumerable<VocabularyItem>> readVocabulary,
        bool isQwen3)
    {
        if (string.IsNullOrEmpty(rawText) || isQwen3)
        {
            return rawText;
        }

        // Crosses the FFI on the transcription hot path; macOS links the core
        // statically and has no equivalent failure mode, so degrade gracefully
        // here rather than failing the transcription.
        try
        {
            var entries = readVocabulary()
                .Select(item => new PortableVocabularyEntry(item.Word ?? string.Empty, item.Replacement))
                .ToList();
            if (entries.Count == 0)
            {
                return rawText;
            }

            var phonetic = SharedCoreBridge.ApplyPhoneticVocabulary(rawText, entries);
            // The count only: a match's token is a word the user spoke (#1645).
            if (phonetic.Matches.Count > 0)
            {
                LoggingService.Debug($"Phonetic match: {phonetic.Matches.Count} token(s) corrected");
            }
            return phonetic.Text;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Local vocabulary pass failed, returning unmatched text: {ex.Message}");
            return rawText;
        }
    }

    /// <summary>
    /// Internal transcription implementation that handles the stdio protocol.
    /// Separated from TranscribeAsync to allow retry logic in the caller.
    /// </summary>
    private async Task<string> TranscribeInternalAsync(string audioPath, long teardownGeneration, CancellationToken cancellationToken)
    {
        // Acquire the transcription lock — stdio is serial.
        //
        // Every await in this method uses ConfigureAwait(false). DisposeModel blocks its
        // caller (the UI thread, on a mode switch) on this same lock; a continuation that
        // needed that thread could not run to release the lock, so teardown would always
        // sit out its whole wait.
        await _transcriptionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Cleared by ReadResponseLineAsync when a caller cancel hands the lock to the
        // background drain; the finally below then leaves the release to the drain.
        var releaseLockInFinally = new StrongBox<bool>(true);
        var teardownCts = BeginActiveRequest();

        try
        {
            ThrowIfRequestCannotRun(teardownGeneration);

            // The daemon instance this request talks to. Every kill below targets this
            // instance, never whatever _daemonProcess holds by then: if a teardown gave up
            // on the lock and a new model loaded, this request must not kill the new daemon.
            var daemonProcess = _daemonProcess;

            var stopwatch = Stopwatch.StartNew();
            LogTranscriptionStart(audioPath);

            // STEP 1: Write the audio path to stdin as JSON
            await WriteRequestAsync(audioPath).ConfigureAwait(false);

            // STEP 2: Read the response line, within a timeout that grows with the audio.
            var responseLine = await ReadResponseLineAsync(
                audioPath, daemonProcess, teardownCts, releaseLockInFinally, cancellationToken).ConfigureAwait(false);

            // STEP 3: Parse the JSON response
            var (transcribedText, durationMs) = ParseTranscriptionResponse(responseLine, daemonProcess);
            return FinishTranscription(transcribedText, durationMs, stopwatch);
        }
        catch (Exception ex) when (IsEndedByTeardown(
            ex,
            teardownCts.IsCancellationRequested || IsStaleTeardownGeneration(teardownGeneration, CurrentTeardownGeneration),
            cancellationToken.IsCancellationRequested))
        {
            // Teardown cancelled (or began after) this request. Whatever ended it (the kill
            // in ReadResponseLineAsync, or a stream disposed if teardown ever had to give up
            // on the lock), report it as Cancelled so the auto-restart never undoes the teardown.
            throw CreateTeardownCancelledException(ex);
        }
        finally
        {
            EndActiveRequest(teardownCts);
            if (releaseLockInFinally.Value)
            {
                _transcriptionLock.Release();
            }
        }
    }

    /// <summary>
    /// The guards a request passes once it holds the lock: no teardown since it entered,
    /// and a running, ready daemon.
    /// </summary>
    private void ThrowIfRequestCannotRun(long teardownGeneration)
    {
        // Guard: a teardown ran while this request waited for the lock (#1562 review).
        // It was meant for the model that teardown unloaded, so it ends here as
        // Cancelled: it must not read daemon state, and its failure must not reach the
        // auto-restart (which would reload the old model and undo a mode switch).
        if (IsStaleTeardownGeneration(teardownGeneration, CurrentTeardownGeneration))
        {
            LoggingService.Info("ParakeetTranscriptionService: Model was torn down while this transcription waited; cancelling it");
            throw CreateTeardownCancelledException(null);
        }

        // Guard: daemon must be ready
        if (!IsAvailable)
        {
            LoggingService.Error("ParakeetTranscriptionService: Daemon is not running or not ready");
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonCrashed,
                "Parakeet daemon is not running",
                "Parakeet");
        }
    }

    private void LogTranscriptionStart(string audioPath)
    {
        LoggingService.Info("========== STARTING PARAKEET TRANSCRIPTION ==========");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");
        LoggingService.Info($"  Provider: {_activeProvider ?? "unknown"}");
        LoggingService.Info($"  Model: {_loadedModelId ?? "unknown"}");

        // Log audio file info
        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  Audio file size: {fileInfo.Length:N0} bytes");
    }

    /// <summary>STEP 1: writes the <c>audio_path</c> request line to the daemon's stdin.</summary>
    private async Task WriteRequestAsync(string audioPath)
    {
        var request = JsonSerializer.Serialize(new { audio_path = audioPath }, s_requestJsonOptions);
        LoggingService.Debug($"ParakeetTranscriptionService: Sending request: {request}");

        await _stdinWriter!.WriteLineAsync(request).ConfigureAwait(false);
        await _stdinWriter.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// STEP 2: reads the daemon's one reply line. A model teardown kills the daemon, a
    /// timeout kills it and throws <see cref="TranscriptionErrorCode.DaemonTimeout"/>, and
    /// a caller cancel hands the read and the transcription lock to the background drain:
    /// it clears <paramref name="releaseLockInFinally"/> so the caller does not release it.
    /// </summary>
    private async Task<string?> ReadResponseLineAsync(
        string audioPath,
        Process? daemonProcess,
        CancellationTokenSource teardownCts,
        StrongBox<bool> releaseLockInFinally,
        CancellationToken cancellationToken)
    {
        // STEP 2: Read the response from stdout with a model-aware timeout that
        // grows with the audio length (#1562): the daemon replies once for the
        // whole file, so a fixed ceiling failed every long Transcribe File.
        var audioSeconds = TryGetAudioDurationSeconds(audioPath);
        var responseTimeout = ComputeResponseTimeout(ResponseFloorSeconds(_isQwen3, _isOnline), audioSeconds);
        var responseSeconds = (long)responseTimeout.TotalSeconds;
        var responseDeadlineTicks = Environment.TickCount64 + (long)responseTimeout.TotalMilliseconds;
        var audioLabel = audioSeconds is { } knownSeconds ? $"{knownSeconds:F1}s" : "unknown";
        LoggingService.Debug($"ParakeetTranscriptionService: Waiting for transcription response ({responseSeconds}s timeout, audio {audioLabel})...");
        SetActiveRequestBudget(teardownCts, responseTimeout);

        var readTimeoutCts = new CancellationTokenSource(responseTimeout);
        var responseReadTask = _stdoutReader!.ReadLineAsync(readTimeoutCts.Token).AsTask();
        var readTimeoutTransferredToDrain = false;
        // The wait ends on a caller cancel OR a model teardown; the read's own token
        // stays the response timeout, so the catch filters below tell the three apart.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, teardownCts.Token);

        string? responseLine;
        try
        {
            responseLine = await responseReadTask.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (teardownCts.IsCancellationRequested)
        {
            // DisposeModel is unloading the model (#1562). End this request on purpose:
            // the daemon is mid-decode and cannot read a quit until it finishes, so kill
            // it now. The read then ends, this request releases the lock, and teardown
            // takes the lock normally instead of disposing the streams under a live
            // read. The outer catch turns this into Cancelled, never DaemonCrashed, so
            // TranscribeAsync does not auto-restart the old model.
            LoggingService.Info("ParakeetTranscriptionService: Model teardown ended the in-flight transcription; killing daemon");
            _ = ObserveInFlightReadAsync(responseReadTask);
            StopDaemonInstance(daemonProcess);
            throw;
        }
        catch (OperationCanceledException) when (readTimeoutCts.IsCancellationRequested)
        {
            // Timeout — kill the daemon
            LoggingService.Error($"ParakeetTranscriptionService: Transcription timed out after {responseSeconds} seconds");
            _ = ObserveInFlightReadAsync(responseReadTask);
            StopDaemonInstance(daemonProcess);
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonTimeout,
                $"Parakeet daemon did not respond within {responseSeconds} seconds",
                "Parakeet");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !readTimeoutCts.IsCancellationRequested)
        {
            // The request was already written to stdin, so the daemon will still produce
            // exactly one result line. Rather than SIGKILL the daemon — which forces a
            // 5-30s cold-start (model + DirectML reload) on the next recording — drain and
            // discard that in-flight line so stdout stays aligned and the daemon survives.
            // Transfer _transcriptionLock ownership to the background drain: cancellation
            // returns to the caller immediately, while the next transcription still waits
            // until stdout is aligned.
            LoggingService.Info("ParakeetTranscriptionService: Transcription cancelled by caller, draining in-flight result in background to keep daemon alive");
            releaseLockInFinally.Value = false;
            readTimeoutTransferredToDrain = true;
            throw HandOffCancelledReadToDrain(
                responseReadTask, readTimeoutCts, teardownCts, daemonProcess, responseDeadlineTicks, cancellationToken);
        }
        finally
        {
            if (!readTimeoutTransferredToDrain)
            {
                readTimeoutCts.Dispose();
            }
        }

        return responseLine;
    }

    /// <summary>
    /// Bounds the drain of a caller-cancelled read and starts it; the drain now owns the
    /// read's timeout CTS and the transcription lock. Returns the cancellation to throw.
    /// </summary>
    private OperationCanceledException HandOffCancelledReadToDrain(
        Task<string?> responseReadTask,
        CancellationTokenSource readTimeoutCts,
        CancellationTokenSource teardownCts,
        Process? daemonProcess,
        long responseDeadlineTicks,
        CancellationToken cancellationToken)
    {
        // The drain holds the lock, so the next dictation waits on it. A long file's
        // budget can be hours (#1562); bound the drain to the engine floor so a
        // cancelled long job is killed instead of blocking the next request.
        var remainingBudget = TimeSpan.FromMilliseconds(
            Math.Max(0, responseDeadlineTicks - Environment.TickCount64));
        var drainBudget = ComputeDrainBudget(ResponseFloorSeconds(_isQwen3, _isOnline), remainingBudget);
        readTimeoutCts.CancelAfter(drainBudget);
        StartInFlightDrain(responseReadTask, readTimeoutCts, teardownCts, daemonProcess);
        // Throw the standard cancellation shape so UI/API callers reach their
        // dedicated cancel handlers instead of showing a transcription error.
        return new OperationCanceledException("Parakeet transcription was cancelled", cancellationToken);
    }

    /// <summary>
    /// STEP 3: the reply's text and the daemon's inference time. A closed stdout or an
    /// error reply throws <see cref="TranscriptionErrorCode.DaemonCrashed"/>.
    /// </summary>
    private (string Text, long DurationMs) ParseTranscriptionResponse(string? responseLine, Process? daemonProcess)
    {
        if (responseLine == null)
        {
            LoggingService.Error("ParakeetTranscriptionService: Daemon closed stdout during transcription (crashed?)");
            if (ReferenceEquals(_daemonProcess, daemonProcess))
            {
                _isReady = false;
            }
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonCrashed,
                "Parakeet daemon closed stdout unexpectedly",
                "Parakeet");
        }

        LoggingService.Debug($"ParakeetTranscriptionService: Received response: {DescribeDaemonResponse(responseLine)}");

        // STEP 3: Parse the JSON response
        string transcribedText;
        long durationMs = 0;

        try
        {
            using var responseDoc = JsonDocument.Parse(responseLine);
            var root = responseDoc.RootElement;

            // Check for error response from daemon
            if (root.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString() ?? "Unknown daemon error";
                LoggingService.Error($"ParakeetTranscriptionService: Daemon returned error: {errorMsg}");
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    $"Parakeet daemon error: {errorMsg}",
                    "Parakeet");
            }

            transcribedText = root.TryGetProperty("text", out var textProp)
                ? textProp.GetString() ?? ""
                : "";

            if (root.TryGetProperty("duration_ms", out var durationProp))
            {
                durationMs = durationProp.GetInt64();
            }
        }
        catch (JsonException ex)
        {
            LoggingService.Error($"ParakeetTranscriptionService: Failed to parse daemon response: {DescribeDaemonResponse(responseLine)}", ex);
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonCrashed,
                "Parakeet daemon returned invalid JSON response",
                "Parakeet",
                ex);
        }

        return (transcribedText, durationMs);
    }

    /// <summary>The Qwen3 cleanup and the completion log; returns the final text.</summary>
    private string FinishTranscription(string transcribedText, long durationMs, Stopwatch stopwatch)
    {
        // Qwen3-only cleanup: repair truncated UTF-8 (U+FFFD) and collapse
        // decoder repetition loops. Also warn on wrong-script hallucinations.
        if (_isQwen3)
        {
            if (Qwen3TextPostProcessor.LooksLikeWrongScript(transcribedText, _lastLanguage))
            {
                LoggingService.Warn($"ParakeetTranscriptionService: Qwen3 produced no CJK characters but language is '{_lastLanguage}' — possible wrong-script hallucination");
            }
            transcribedText = Qwen3TextPostProcessor.Clean(transcribedText);
        }

        stopwatch.Stop();

        // Log performance summary
        LoggingService.Info("========== PARAKEET TRANSCRIPTION COMPLETE ==========");
        LoggingService.Info($"  Characters: {transcribedText.Length}");
        LoggingService.Info($"  Daemon inference time: {durationMs}ms");
        LoggingService.Info($"  Total round-trip time: {stopwatch.ElapsedMilliseconds}ms");

        if (string.IsNullOrWhiteSpace(transcribedText))
        {
            LoggingService.Warn("ParakeetTranscriptionService: Transcription returned empty text — audio may be silent or unrecognizable");
        }

        return transcribedText;
    }
}
