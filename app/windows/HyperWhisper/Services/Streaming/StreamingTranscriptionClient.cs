using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// Provider-neutral WebSocket client for realtime transcription sessions.
/// </summary>
/// <remarks>
/// One class in six files (issue #839). This file is the session lifecycle: state, events,
/// start, stop and dispose. <c>.Send.cs</c> writes to the socket, <c>.Receive.cs</c> reads it
/// and classifies closes, <c>.Reconnect.cs</c> is the reconnect policy, <c>.Transcript.cs</c>
/// assembles the transcript and fans provider events out, and <c>.State.cs</c> holds the
/// state guard and the event raise.
/// </remarks>
public sealed partial class StreamingTranscriptionClient : IAsyncDisposable, IDisposable
{
    private const int ReceiveBufferSize = 8192;
    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly IStreamingProviderStrategy _strategy;
    private readonly StreamingSessionConfig _config;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _finalTranscriptLock = new();
    private readonly object _stateLock = new();
    private readonly StringBuilder _finalTranscript = new();

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveTask;
    private TaskCompletionSource? _sessionStartedTcs;
    private TaskCompletionSource? _sessionCompletedTcs;
    private bool _receivedTerminalClose;
    private bool _disposed;
    // Set only by the synchronous Dispose() path. When true, every event raise is suppressed so
    // teardown cannot re-enter the calling thread via a subscriber that marshals back to it with a
    // blocking Dispatcher.Invoke (the WPF UI thread may itself be blocked on Dispose()). volatile:
    // written on the caller thread before teardown is queued, read on the thread-pool/receive threads.
    private volatile bool _suppressDispatch;

    public StreamingTranscriptionClient(IStreamingProviderStrategy strategy, StreamingSessionConfig config)
    {
        _strategy = strategy;
        _config = config;
    }

    public StreamingConnectionState State { get; private set; } = StreamingConnectionState.Idle;
    public string CurrentPartial { get; private set; } = string.Empty;
    public string FinalText
    {
        get => TranscriptionTextProcessing.FinalizeStreamingText(GetFinalTranscriptSnapshot());
    }
    public int AudioSampleRate => _strategy.AudioSampleRate;

    public event Action<StreamingConnectionState>? StateChanged;
    public event Action<string>? LiveTranscriptChanged;
    public event Action<string>? FinalTranscriptSegmentReceived;
    public event Action<string>? FinalTranscriptChanged;
    public event Action<string>? WarningReceived;
    public event Action<string>? ErrorReceived;
    public event Action<double, double>? SessionCompleted;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State == StreamingConnectionState.Error)
            await StopAsync(cancellationToken);
        else if (State != StreamingConnectionState.Idle)
            return false;

        var uri = _strategy.BuildWebSocketUri(_config);
        if (uri == null)
            return false;

        ClearFinalTranscript();
        CurrentPartial = string.Empty;
        _receivedTerminalClose = false;
        _sessionStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCompletedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var webSocket = new ClientWebSocket();
        _strategy.ConfigureWebSocket(webSocket, _config);
        // Keep the response of an upgrade that never reached 101, so the catch
        // below can read WHY the server refused instead of only that it did.
        // See TerminalUpgradeMessage.
        webSocket.Options.CollectHttpResponseDetails = true;

        ChangeState(StreamingConnectionState.Connecting);
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            return await ConnectAndStartSessionAsync(webSocket, uri);
        }
        catch (OperationCanceledException)
        {
            AbortCancelledConnect(webSocket);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailConnect(webSocket, ex);
            return false;
        }
    }

    private async Task<bool> ConnectAndStartSessionAsync(ClientWebSocket webSocket, Uri uri)
    {
        // StartAsync assigns _sessionCts and _sessionStartedTcs right before it calls this.
    await webSocket.ConnectAsync(uri, _sessionCts!.Token);
        _webSocket = webSocket;

        if (_strategy.SessionStartsOnWebSocketOpen)
        {
            _receiveTask = Task.Run(() => RunReceiveLoopAsync(webSocket, _sessionCts.Token), CancellationToken.None);
            await SendStartMessagesAsync(_sessionCts.Token);
            _sessionStartedTcs!.TrySetResult();

            // The receive loop (background thread) can concurrently observe a terminal
            // close/error and call HandleCloseResult -> Error while this await was in
            // flight. It can also independently complete this exact Connecting -> Streaming
            // transition itself: for a provider whose first inbound frame after connecting
            // IS the session-started signal (Deepgram's Metadata), HandleProviderEvent's
            // SessionStarted case races this same transition from the receive-loop thread.
            // TryChangeState performs the check atomically under _stateLock and treats
            // "already at Streaming" as success, so it cannot clobber a concurrently-recorded
            // Error back to Streaming, doesn't spuriously fail just because the receive loop
            // already completed the same transition first, and tells us whether the state we
            // end up with is actually Streaming.
            if (!TryChangeState(StreamingConnectionState.Connecting, StreamingConnectionState.Streaming))
            {
                LoggingService.Warn(
                    $"StreamingTranscriptionClient: start raced into {State} instead of Streaming for {_strategy.TranscriptionProviderLabel}");
                return false;
            }
        }
        else
        {
            ChangeState(StreamingConnectionState.Ready);
            _receiveTask = Task.Run(() => RunReceiveLoopAsync(webSocket, _sessionCts.Token), CancellationToken.None);
            await SendStartMessagesAsync(_sessionCts.Token);
            await WaitForSessionStartedAsync(_sessionCts.Token);
        }

        // Closes the remaining slice of the gap Codex flagged: whichever branch above landed
        // us in Connecting/Ready -> Streaming, a terminal close can still land on the
        // receive-loop thread in the instant right after that transition and before we return
        // "started" to the caller - no check-then-return here can fully close a window that a
        // background thread can keep writing into after the check, but re-reading State once
        // more right before we hand success back closes almost all of it. MainViewModel also
        // re-checks State itself after this call returns as defense in depth (see
        // StartStreamingRecordingAsync) for what's left of the window.
        if (State == StreamingConnectionState.Error)
        {
            LoggingService.Warn(
                $"StreamingTranscriptionClient: connected but raced straight into Error for {_strategy.TranscriptionProviderLabel}");
            return false;
        }

        LoggingService.Info($"StreamingTranscriptionClient: connected to {_strategy.TranscriptionProviderLabel}");
        return true;
    }

    private void AbortCancelledConnect(ClientWebSocket webSocket)
    {
        _sessionCts?.Cancel();
        webSocket.Dispose();
        CleanupWebSocket();
        CurrentPartial = string.Empty;

        // The receive loop can already be running by this point (it starts before the awaits
        // that this cancellation unwound through), so a concurrent terminal close/error on
        // that thread can have already recorded Error - don't clobber it back to Idle.
        TryChangeStateUnless(
            StreamingConnectionState.Idle,
            StreamingConnectionState.Error,
            StreamingConnectionState.Disconnecting);
    }

    private void FailConnect(ClientWebSocket webSocket, Exception ex)
    {
        // A REFUSED UPGRADE IS NOT A CONNECTION FAILURE.
        //
        // HyperWhisper Cloud needs 30 seconds of balance to open a streaming
        // session and answers 402 before any socket exists, so the user who
        // has already run out lands here on every attempt. Without the
        // status, all this had to show was .NET's own sentence — "The server
        // returned status code '402' when status code '101' was expected" —
        // which names neither the problem nor the fix. Read up here rather
        // than inline, so it cannot come after the Dispose further down.
        var refusal = TerminalUpgradeMessage(webSocket);

        LoggingService.Error($"StreamingTranscriptionClient: connect failed for {_strategy.TranscriptionProviderLabel}", ex);
        Raise(ErrorReceived, refusal ?? ex.Message);

        // Same reasoning as HandleCloseResult: don't reclassify an in-flight intentional
        // shutdown (StopAsync already moved to Disconnecting/Idle) as this connect failure.
        TryChangeStateUnless(
            StreamingConnectionState.Error,
            StreamingConnectionState.Disconnecting,
            StreamingConnectionState.Idle);
        _sessionCts?.Cancel();
        webSocket.Dispose();
        CleanupWebSocket();
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        if (State is StreamingConnectionState.Idle or StreamingConnectionState.Disconnecting)
            return FinalText;

        ChangeState(StreamingConnectionState.Disconnecting);

        string returnText;

        try
        {
            foreach (var step in _strategy.GetStopSequence())
            {
                await RunStopStepAsync(step, cancellationToken);
            }
        }
        finally
        {
            _sessionCts?.Cancel();

            try
            {
                if (_receiveTask != null)
                {
                    try
                    {
                        await _receiveTask.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        LoggingService.Warn("StreamingTranscriptionClient: receive loop stop wait cancelled");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"StreamingTranscriptionClient: receive loop ended during stop - {ex.Message}");
                    }
                }
            }
            finally
            {
                returnText = _receivedTerminalClose && !string.IsNullOrWhiteSpace(CurrentPartial)
                    ? BuildLiveTranscript(CurrentPartial)
                    : FinalText;
                CleanupWebSocket();
                CurrentPartial = string.Empty;
                ChangeState(StreamingConnectionState.Idle);
            }
        }

        return returnText;
    }

    private async Task RunStopStepAsync(StreamingStopStep step, CancellationToken cancellationToken)
    {
        switch (step.Action)
        {
            case StreamingStopAction.SendMessage when step.Payload != null:
                await SendEncodedAsync(step.Payload, step.MessageType, cancellationToken);
                break;

            case StreamingStopAction.Wait:
                await Task.Delay(step.WaitAfter ?? TimeSpan.Zero, cancellationToken);
                break;

            case StreamingStopAction.WaitForSessionComplete:
                await WaitForSessionCompleteAsync(step.WaitAfter ?? TimeSpan.Zero, cancellationToken);
                break;

            case StreamingStopAction.Close:
                await CloseWebSocketAsync(cancellationToken);
                break;
        }
    }

    private async Task WaitForSessionCompleteAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completionTask = _sessionCompletedTcs?.Task;
        if (completionTask == null || completionTask.IsCompleted)
            return;

        // Only the receive loop can complete _sessionCompletedTcs. Once it has
        // ended (a dead socket, where the stop frame was silently not sent),
        // session_complete cannot arrive, so stop waiting with it (issue #834) -
        // the same race the SharedCore stop runner makes on its receiveTask.
        var receiveTask = _receiveTask;
        if (receiveTask?.IsCompleted == true)
            return;

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        if (receiveTask == null)
            await Task.WhenAny(completionTask, timeoutTask);
        else
            await Task.WhenAny(completionTask, receiveTask, timeoutTask);
    }

    private async Task WaitForSessionStartedAsync(CancellationToken cancellationToken)
    {
        var startedTask = _sessionStartedTcs?.Task;
        if (startedTask == null || startedTask.IsCompletedSuccessfully)
            return;

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        var completedTask = await Task.WhenAny(startedTask, timeoutTask);
        if (completedTask != startedTask)
            throw new TimeoutException("Streaming session did not become ready.");

        await startedTask;
    }

    private void CleanupWebSocket()
    {
        _webSocket?.Dispose();
        _webSocket = null;

        _sessionCts?.Dispose();
        _sessionCts = null;
        _receiveTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        using var disposeCts = new CancellationTokenSource(DisposeTimeout);

        try
        {
            await StopAsync(disposeCts.Token);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            LoggingService.Warn($"StreamingTranscriptionClient: dispose timed out after {DisposeTimeout.TotalSeconds:F0}s");
            CleanupWebSocket();
            CurrentPartial = string.Empty;
            ChangeState(StreamingConnectionState.Idle);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"StreamingTranscriptionClient: dispose cleanup failed - {ex.Message}");
            CleanupWebSocket();
            CurrentPartial = string.Empty;
            ChangeState(StreamingConnectionState.Idle);
        }
        finally
        {
            _disposed = true;
            _sendLock.Dispose();
            // The live strategy owns a handle onto a Rust `Arc` (issue #281) and
            // MUST be released with the client that holds it - a client is built
            // per recording, so leaking one leaks a session per dictation for the
            // life of the process. Not a `using` on the field: the strategy is
            // handed in by the caller and only this class knows when the session
            // is over. A strategy that is not disposable (the smoke suite's
            // no-op) is left alone.
            (_strategy as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Synchronous disposal is a deadlock trap on the WPF UI thread: the awaits inside
        // DisposeAsync/StopAsync capture the caller's SynchronizationContext, and subscribers
        // (e.g. StateChanged) marshal back to the UI thread with a blocking Dispatcher.Invoke -
        // but the UI thread is parked here on GetResult(). Two guards make it deadlock-free:
        //   1. Task.Run moves the teardown's awaits off the caller's SynchronizationContext.
        //   2. _suppressDispatch drops every event raise so no subscriber can marshal back to
        //      the blocked caller (the 5s DisposeTimeout cannot interrupt a synchronous
        //      Dispatcher.Invoke, so suppression - not the timeout - is what prevents the hang).
        // Still bounded by DisposeTimeout for the websocket close/receive-loop join. This path is
        // intentionally silent (no shutdown events); prefer DisposeAsync() when you can await it.
        _suppressDispatch = true;
        Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }
}
