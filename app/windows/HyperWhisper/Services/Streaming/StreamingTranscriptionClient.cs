using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.Utilities;
// Aliased, not imported: HyperWhisper.SharedCore also declares
// CloudTranscriptionProvider, which would clash with HyperWhisper.Models.
using PortableLiveUpgradeRefusal = HyperWhisper.SharedCore.PortableLiveUpgradeRefusal;
using SharedCoreBridge = HyperWhisper.SharedCore.SharedCoreBridge;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// Provider-neutral WebSocket client for realtime transcription sessions.
/// </summary>
public sealed class StreamingTranscriptionClient : IAsyncDisposable, IDisposable
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
            await webSocket.ConnectAsync(uri, _sessionCts.Token);
            _webSocket = webSocket;

            if (_strategy.SessionStartsOnWebSocketOpen)
            {
                _receiveTask = Task.Run(() => RunReceiveLoopAsync(webSocket, _sessionCts.Token), CancellationToken.None);
                await SendStartMessagesAsync(_sessionCts.Token);
                _sessionStartedTcs.TrySetResult();

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
        catch (OperationCanceledException)
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
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            return false;
        }
    }

    public async Task SendAudioAsync(byte[] pcmData, CancellationToken cancellationToken = default)
    {
        if (pcmData.Length == 0)
            return;

        var webSocket = _webSocket;
        if (webSocket?.State != WebSocketState.Open)
            return;

        await _strategy.OnAudioSendOpportunityAsync(SendEncodedAsync, cancellationToken);

        var encoded = _strategy.EncodeAudioChunk(pcmData);
        await SendEncodedAsync(encoded.Data, encoded.Type, cancellationToken);
    }

    private async Task SendStartMessagesAsync(CancellationToken cancellationToken)
    {
        foreach (var (data, type) in _strategy.GetStartMessages(_config))
        {
            await SendEncodedAsync(data, type, cancellationToken);
        }
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

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];

        while (!cancellationToken.IsCancellationRequested &&
               webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var message = await ReceiveTextMessageAsync(webSocket, buffer, cancellationToken);
            if (message == null)
                break;

            HandleProviderEvent(_strategy.ParseMessage(message));
        }
    }

    private async Task RunReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var currentWebSocket = webSocket;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReceiveLoopAsync(currentWebSocket, cancellationToken);

                if (cancellationToken.IsCancellationRequested ||
                    State is StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle or StreamingConnectionState.Error ||
                    _receivedTerminalClose)
                {
                    return;
                }

                if (!await TryReconnectAsync(cancellationToken))
                    return;

                currentWebSocket = _webSocket!;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (State is StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle)
                return;

            LoggingService.Warn($"StreamingTranscriptionClient: receive loop failed, attempting reconnect - {ex.Message}");

            if (!await TryReconnectAsync(cancellationToken))
            {
                LoggingService.Error("StreamingTranscriptionClient: receive loop failed", ex);
                Raise(ErrorReceived, ex.Message);

                // TryReconnectAsync's own await chain gives StopAsync a window to move to
                // Disconnecting/Idle concurrently - don't clobber that back to Error.
                TryChangeStateUnless(
                    StreamingConnectionState.Error,
                    StreamingConnectionState.Disconnecting,
                    StreamingConnectionState.Idle);
            }
        }
    }

    /// <summary>
    /// The message for an upgrade the server refused outright, or null when the
    /// failure is one a retry can still answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This head and macOS used to keep the same status table twice. Since issue
    /// #281 the classification is the shared core's
    /// (<c>hw_net::live::upgrade_refusal</c>) and only the two user-facing
    /// sentences below stay here, because they are this app's wording and name
    /// this app's Settings screen. It covers the gap the terminal-close handling
    /// below cannot: that only helps a user who runs out of credits
    /// <em>during</em> a session, while the same user one keypress later never
    /// opens a socket at all — HyperWhisper Cloud requires 30 seconds of balance
    /// and refuses the upgrade with 402 — and that path had only .NET's own
    /// "status code '402' when status code '101' was expected" to show for it.
    /// </para>
    /// <para>
    /// Requires <c>CollectHttpResponseDetails</c>, without which
    /// <c>HttpStatusCode</c> is 0 and this correctly finds nothing — 0 is not one
    /// of the refusal statuses.
    /// </para>
    /// </remarks>
    private static string? TerminalUpgradeMessage(ClientWebSocket? webSocket) => webSocket == null
        ? null
        : SharedCoreBridge.LiveUpgradeRefusal((int)webSocket.HttpStatusCode) switch
        {
            PortableLiveUpgradeRefusal.InsufficientCredits =>
                "Streaming could not start because credits are exhausted. Add more credits in Settings.",
            PortableLiveUpgradeRefusal.Unauthorized =>
                "Streaming could not start because the account key was refused. Check your key in Settings.",
            _ => null
        };

    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        var uri = _strategy.BuildWebSocketUri(_config);
        if (uri == null)
            return false;

        ChangeState(StreamingConnectionState.Reconnecting);
        SentryService.AddBreadcrumb(
            "streaming_reconnect_started",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = _strategy.TranscriptionProviderLabel });

        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested ||
                State is StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle)
            {
                return false;
            }

            ClientWebSocket? webSocket = null;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);

                webSocket = new ClientWebSocket();
                _strategy.ConfigureWebSocket(webSocket, _config);
                webSocket.Options.CollectHttpResponseDetails = true;
                await webSocket.ConnectAsync(uri, cancellationToken);

                _webSocket?.Dispose();
                _webSocket = webSocket;
                await SendStartMessagesAsync(cancellationToken);

                var reconnectedState = _strategy.SessionStartsOnWebSocketOpen
                    ? StreamingConnectionState.Streaming
                    : StreamingConnectionState.Ready;

                // Same race as StartAsync's Connecting -> Streaming transition: something else
                // (e.g. StopAsync moving to Disconnecting, or a concurrent terminal close) can
                // land between the awaits above and here. TryChangeState only commits if we're
                // still in the state this branch expects, and reports whether it landed.
                if (!TryChangeState(StreamingConnectionState.Reconnecting, reconnectedState))
                {
                    LoggingService.Warn(
                        $"StreamingTranscriptionClient: reconnect raced into {State} instead of {reconnectedState} for {_strategy.TranscriptionProviderLabel}");
                    return false;
                }

                SentryService.AddBreadcrumb(
                    "streaming_reconnect_succeeded",
                    "audio.streaming",
                    data: new Dictionary<string, string>
                    {
                        ["provider"] = _strategy.TranscriptionProviderLabel,
                        ["attempt"] = attempt.ToString()
                    });
                LoggingService.Info($"StreamingTranscriptionClient: reconnect succeeded on attempt {attempt}");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Same ownership rule as the catch below: a socket this attempt
                // built and never adopted has no other reference to release it.
                if (webSocket != null && !ReferenceEquals(webSocket, _webSocket))
                    webSocket.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StreamingTranscriptionClient: reconnect attempt {attempt} failed - {ex.Message}");

                var refusal = TerminalUpgradeMessage(webSocket);

                // Release the socket this attempt built, unless the attempt got
                // far enough to adopt it as _webSocket (ConnectAsync succeeded
                // and SendStartMessagesAsync then threw). Disposing it in that
                // case would hand the next attempt, or StopAsync, a dead socket.
                if (webSocket != null && !ReferenceEquals(webSocket, _webSocket))
                    webSocket.Dispose();

                // A balance that ran out mid-session refuses every reconnect the
                // same way, so the remaining attempts are two more seconds spent
                // waiting for an answer that cannot change — ending on
                // "connection was lost", which sends the user to look at their
                // network. Stop on the first refusal and name it instead.
                if (refusal != null)
                {
                    LoggingService.Warn($"StreamingTranscriptionClient: reconnect refused outright - {refusal}");
                    Raise(ErrorReceived, refusal);
                    _sessionStartedTcs?.TrySetException(new InvalidOperationException(refusal));
                    TryChangeStateUnless(
                        StreamingConnectionState.Error,
                        StreamingConnectionState.Disconnecting,
                        StreamingConnectionState.Idle);
                    return false;
                }
            }
        }

        SentryService.AddBreadcrumb(
            "streaming_reconnect_failed",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = _strategy.TranscriptionProviderLabel });
        Raise(ErrorReceived, "Streaming connection was lost and could not be restored.");
        _sessionStartedTcs?.TrySetException(new InvalidOperationException("Streaming connection was lost and could not be restored."));

        // The per-attempt loop above only checks Disconnecting/Idle at the top of each iteration -
        // StopAsync can still win the race in the gap between the last attempt and here.
        TryChangeStateUnless(
            StreamingConnectionState.Error,
            StreamingConnectionState.Disconnecting,
            StreamingConnectionState.Idle);
        return false;
    }

    private async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket webSocket,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                HandleCloseResult(result);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return stream.Length == 0 ? null : Encoding.UTF8.GetString(stream.ToArray());
    }

    // internal (not private): test seam for HyperWhisper.SmokeTests via InternalsVisibleTo (see
    // HyperWhisper.csproj). A freshly constructed client's State defaults to Idle, which
    // HandleCloseResult's own shutdown guard treats as "already torn down" and no-ops on - so
    // exercising HandleCloseResult in isolation needs a way to put the client into a realistic
    // pre-close state (e.g. Streaming) first without going through a real WebSocket connect.
    internal void SetStateForTesting(StreamingConnectionState state) => ChangeState(state);

    // internal (not private): direct-call surface for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility change is intended.
    internal void HandleCloseResult(WebSocketReceiveResult result)
    {
        // No status at all is ambiguous (not a clear provider signal) - let it fall through to
        // the existing reconnect/backoff path in RunReceiveLoopAsync, same as today.
        if (result.CloseStatus is null)
            return;

        var closeCode = (int)result.CloseStatus.Value;

        // A clean close (1000) is never a provider error - it's either a graceful server-side
        // close or the server's echo of our own CloseAsync(NormalClosure, ...) during StopAsync.
        if (closeCode == (int)WebSocketCloseStatus.NormalClosure)
            return;

        // Only genuinely non-recoverable codes end the session immediately. 4001/4002 are
        // HyperWhisper's own app-level codes (credits exhausted / max session duration) and stay
        // here rather than in the strategy since they're not provider-specific. Everything else is
        // delegated to the strategy, whose default covers the standard fatal WebSocket protocol
        // codes (1002, 1003, 1007, 1008, 1009, 1011) - see IStreamingProviderStrategy for the full
        // list and rationale. Standard transient codes (1001 Going Away, 1006 Abnormal/no close
        // frame, 1012 Service Restart, 1013 Try Again Later, and anything else not explicitly known
        // to be fatal) fall through to the existing reconnect/backoff path in RunReceiveLoopAsync.
        if (!(closeCode is 4001 or 4002 || _strategy.IsTerminalCloseCode(closeCode)))
            return;

        // We're already mid/post our own intentional shutdown (StopAsync sets Disconnecting
        // before running the stop sequence, and _sessionCts isn't cancelled until after that
        // completes, so the receive loop can still reach here concurrently). TryChangeStateCore
        // performs the "are we shutting down" check and the write to Error atomically under
        // _stateLock, so it cannot race with StopAsync moving to Disconnecting/Idle in between -
        // and it doubles as the guard against reclassifying our own shutdown as a provider error.
        //
        // The bookkeeping below (_receivedTerminalClose, the failure message, the ErrorReceived
        // event, and the startup-failure exception) is populated from the onSuccess callback,
        // which TryChangeStateCore guarantees runs before StateChanged(Error) is published - a
        // subscriber reacting synchronously to StateChanged, or a caller that just observed
        // State == Error, must never see that state ahead of the reason for it. onSuccess also
        // runs when State is already Error (e.g. a prior in-band provider error already got here
        // first) rather than only on an actual Disconnecting/Idle-guarded transition, so a
        // terminal close arriving after an in-band error still records _receivedTerminalClose
        // instead of silently no-op'ing and losing the last partial transcript.
        TryChangeStateCore(
            current => current is not (StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle),
            StreamingConnectionState.Error,
            onSuccess: () =>
            {
                _receivedTerminalClose = true;
                var message = closeCode switch
                {
                    4001 => "Streaming stopped because credits are exhausted.",
                    4002 => "Streaming stopped because the maximum session duration was reached.",
                    _ => string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                        ? $"Streaming connection was closed by the provider (code {closeCode})."
                        : $"Streaming connection was closed by the provider: {result.CloseStatusDescription}"
                };

                LoggingService.Warn($"StreamingTranscriptionClient: terminal server close {closeCode} ({result.CloseStatusDescription})");
                SentryService.AddBreadcrumb(
                    "streaming_terminal_close",
                    "audio.streaming",
                    data: new Dictionary<string, string>
                    {
                        ["provider"] = _strategy.TranscriptionProviderLabel,
                        ["closeCode"] = closeCode.ToString()
                    });
                Raise(ErrorReceived, message);
                _sessionStartedTcs?.TrySetException(new InvalidOperationException(message));
            });
    }

    // internal (not private): direct-call surface for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj), same seam as HandleCloseResult above - the
    // turn-boundary rule below is decided from State, so pinning it needs a way to drive provider
    // events at a chosen State without standing up a real WebSocket. No other accessibility change
    // is intended; the receive loop is still the only production caller.
    internal void HandleProviderEvent(StreamingProviderEvent? providerEvent)
    {
        switch (providerEvent)
        {
            case null:
                return;

            case StreamingProviderEvent.SessionStarted:
                _sessionStartedTcs?.TrySetResult();

                // For a provider whose first inbound frame after connecting IS the session-started
                // signal (Deepgram's Metadata), this races StartAsync's own Connecting -> Streaming
                // transition from the caller thread. Guard rather than a bare overwrite so this
                // can't clobber a concurrently-recorded Error/Disconnecting/Idle, and so it no-ops
                // cleanly (via TryChangeStateCore's "already there" success) if StartAsync's own
                // transition already landed first.
                TryChangeStateUnless(
                    StreamingConnectionState.Streaming,
                    StreamingConnectionState.Disconnecting,
                    StreamingConnectionState.Idle,
                    StreamingConnectionState.Error);
                return;

            case StreamingProviderEvent.PartialTranscript partial:
                CurrentPartial = TranscriptionTextProcessing.ProcessVoiceCommands(partial.Text);
                Raise(LiveTranscriptChanged, BuildLiveTranscript(CurrentPartial));
                return;

            case StreamingProviderEvent.FinalTranscript final:
                var finalSegment = AppendFinalTranscript(final.Text);
                CurrentPartial = string.Empty;
                var finalText = FinalText;
                if (!string.IsNullOrWhiteSpace(finalSegment))
                    Raise(FinalTranscriptSegmentReceived, finalSegment);
                Raise(FinalTranscriptChanged, finalText);
                Raise(LiveTranscriptChanged, finalText);
                return;

            // ONE FRAME, TWO HALVES - and the completion half answers to the same
            // turn-boundary rule as the standalone SessionComplete below.
            //
            // Gemini answers audio_stream_end with a single serverContent carrying
            // the last committed segment AND generationComplete, and the shared core
            // reports both halves (hw-net live/gemini.rs). Ungated, that same frame
            // arriving mid-dictation would end the session at a pause and drop the
            // last utterance - the exact fault the SessionComplete gate below exists
            // to prevent. The TEXT half is committed either way: a turn's committed
            // segment belongs in the document whether or not the turn was the last.
            //
            // Inert for the other five vendors, which answer
            // CompleteEndsSessionBeforeStop == true.
            case StreamingProviderEvent.FinalTranscriptAndSessionComplete complete:
                var completeSegment = AppendFinalTranscript(complete.Text);
                CurrentPartial = string.Empty;
                var completedFinalText = FinalText;
                if (!string.IsNullOrWhiteSpace(completeSegment))
                    Raise(FinalTranscriptSegmentReceived, completeSegment);
                Raise(FinalTranscriptChanged, completedFinalText);
                if (!_strategy.CompleteEndsSessionBeforeStop &&
                    State != StreamingConnectionState.Disconnecting)
                {
                    LoggingService.Debug(
                        $"StreamingTranscriptionClient: turn boundary from {_strategy.TranscriptionProviderLabel} rode in on a final, session continues");
                    return;
                }
                _sessionCompletedTcs?.TrySetResult();
                Raise(SessionCompleted, complete.DurationSeconds, complete.CreditsUsed);
                return;

            case StreamingProviderEvent.SessionComplete complete:
                // For most vendors this frame is emitted once, at the end of the
                // session, and CompleteEndsSessionBeforeStop (default true) keeps
                // the original unconditional behaviour. Gemini is the exception: it
                // emits its completion frame at every TURN boundary, so before we
                // asked to stop this is "the current utterance is done", not "the
                // session is over" - completing _sessionCompletedTcs there would
                // release the stop sequence's wait early and drop the LAST
                // utterance's final.
                //
                // StopAsync moves State to Disconnecting before it runs a single
                // stop step, so State IS this client's "stop requested" flag - the
                // same condition the backend proxy keys on (ws-streaming-shared.ts,
                // 'complete' arm) and the shared .NET session loop uses
                // (LiveCloudTranscriptionService, `|| state.StopRequested`).
                //
                // Nothing needs flushing on a turn boundary: the turn's own text
                // already arrived as its final beforehand, and CurrentPartial is
                // deliberately left alone so a preview that was never finalized
                // survives to the next frame.
                if (!_strategy.CompleteEndsSessionBeforeStop &&
                    State != StreamingConnectionState.Disconnecting)
                {
                    LoggingService.Debug(
                        $"StreamingTranscriptionClient: turn boundary from {_strategy.TranscriptionProviderLabel}, session continues");
                    return;
                }

                _sessionCompletedTcs?.TrySetResult();
                Raise(SessionCompleted, complete.DurationSeconds, complete.CreditsUsed);
                return;

            case StreamingProviderEvent.Warning warning:
                var warningMessage = warning.RemainingSeconds.HasValue
                    ? $"{warning.Message} ({Math.Ceiling(warning.RemainingSeconds.Value):0} seconds remaining)"
                    : warning.Message;
                LoggingService.Warn($"StreamingTranscriptionClient: provider warning - {warningMessage}");
                Raise(WarningReceived, warningMessage);
                return;

            // NOT classified through SharedCoreBridge.ClassifyLiveErrorMessage, deliberately.
            // macOS needs that split because its client keeps reconnecting after an error frame;
            // this one moves to Error, and RunReceiveLoopAsync's `State is ... Error` guard then
            // ends the session for EVERY provider error frame, terminal or not. So there is no
            // doomed-reconnect fan-out here to suppress - the bias is the opposite one, and
            // wiring the classifier in would LOOSEN termination: a transient frame would start
            // keeping its reconnect. That is a real behaviour change on a shipped path and it
            // belongs to the client rework, not to issue #281's single-sourcing.
            case StreamingProviderEvent.Error error:
                LoggingService.Error($"StreamingTranscriptionClient: provider error - {error.Message}");
                Raise(ErrorReceived, error.Message);
                _sessionStartedTcs?.TrySetException(new InvalidOperationException(error.Message));

                // Don't reclassify an in-flight intentional shutdown (StopAsync already moved to
                // Disconnecting/Idle) as this in-band provider error.
                TryChangeStateUnless(
                    StreamingConnectionState.Error,
                    StreamingConnectionState.Disconnecting,
                    StreamingConnectionState.Idle);
                return;

            case StreamingProviderEvent.Metadata metadata:
                LoggingService.Debug($"StreamingTranscriptionClient: provider metadata - {metadata.Raw}");
                return;
        }
    }

    internal string? AppendFinalTranscript(string text)
    {
        // Mirrors the batch path's order (TranscriptionOrchestrator.RunAsync):
        // RemoveFillerWords -> ProcessVoiceCommands -> (vocabulary is applied earlier,
        // upstream, for streaming). Only confirmed/final deltas reach this method
        // (see HandleProviderEvent's FinalTranscript / FinalTranscriptAndSessionComplete
        // cases) - interim/partial text must never be filler-stripped, to avoid words
        // popping in/out as the partial hypothesis changes.
        //
        // The shared core's remove_filler_words takes the language and no-ops
        // outside en/en-*, so a non-English stream keeps its real words (e.g.
        // German "er"/"um") without a bespoke gate here (issue #278).
        var shouldRemoveFillers = _config.RemoveFillerWords;

        bool isFirstConfirmedDelta;
        lock (_finalTranscriptLock)
        {
            isFirstConfirmedDelta = _finalTranscript.Length == 0;
        }

        var withoutFillers = shouldRemoveFillers
            ? TranscriptionTextProcessing.RemoveFillerWords(text, _config.Language)
            : text;

        // RemoveFillerWords recapitalizes the word after a leading filler on the
        // assumption that it is processing the START of a whole
        // transcript - true for the batch path, and for this session's very
        // first confirmed delta. For later deltas that assumption is wrong: a
        // filler opening a mid-transcript delta (e.g. "um, this works" following
        // an earlier confirmed "I think") is not a sentence start, so undo the
        // recapitalization it applied. Only reverse it when the raw delta itself
        // opened lowercase - if the delta already opened uppercase, leave it.
        if (shouldRemoveFillers &&
            !isFirstConfirmedDelta &&
            text.Length > 0 && char.IsLower(text[0]) &&
            withoutFillers.Length > 0 && char.IsUpper(withoutFillers[0]))
        {
            withoutFillers = char.ToLower(withoutFillers[0]) + withoutFillers.Substring(1);
        }

        var processed = TranscriptionTextProcessing.ProcessVoiceCommands(withoutFillers).Trim();
        if (string.IsNullOrEmpty(processed))
            return null;

        lock (_finalTranscriptLock)
        {
            if (_finalTranscript.Length > 0 &&
                !char.IsWhiteSpace(_finalTranscript[^1]) &&
                !processed.StartsWith('\n'))
            {
                _finalTranscript.Append(' ');
            }

            _finalTranscript.Append(processed);
        }

        return processed;
    }

    private string BuildLiveTranscript(string partial)
    {
        if (string.IsNullOrWhiteSpace(partial))
            return FinalText;

        var finalText = GetFinalTranscriptSnapshot();
        var combined = string.IsNullOrWhiteSpace(finalText)
            ? partial
            : $"{finalText} {partial}";

        return TranscriptionTextProcessing.FinalizeStreamingText(combined);
    }

    private void ClearFinalTranscript()
    {
        lock (_finalTranscriptLock)
        {
            _finalTranscript.Clear();
        }
    }

    private string GetFinalTranscriptSnapshot()
    {
        lock (_finalTranscriptLock)
        {
            return _finalTranscript.ToString();
        }
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

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        await Task.WhenAny(completionTask, timeoutTask);
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

    private async Task SendEncodedAsync(
        byte[] data,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken
    )
    {
        var webSocket = _webSocket;
        if (webSocket?.State != WebSocketState.Open)
            return;

        using var linkedCts = CreateLinkedSessionToken(cancellationToken);
        await _sendLock.WaitAsync(linkedCts.Token);

        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    messageType,
                    endOfMessage: true,
                    linkedCts.Token
                );
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseWebSocketAsync(CancellationToken cancellationToken)
    {
        var webSocket = _webSocket;
        if (webSocket == null)
            return;

        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        using var linkedCts = CreateLinkedSessionToken(cancellationToken);

        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "streaming session ended", linkedCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggingService.Warn($"StreamingTranscriptionClient: close failed - {ex.Message}");
        }
    }

    private CancellationTokenSource CreateLinkedSessionToken(CancellationToken cancellationToken)
    {
        var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        return CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
    }

    // Single chokepoint for every State mutation in this class - the one discipline every call
    // site (ChangeState/TryChangeState/TryChangeStateUnless, and HandleCloseResult's inline guard)
    // routes through instead of each hand-rolling its own lock+check+set+raise body. `guard`
    // decides whether the transition from the current state to `next` is allowed; it is not
    // consulted (and the transition trivially "succeeds") when State already equals `next`, so
    // repeated/racing attempts to reach the same target state are idempotent instead of spuriously
    // failing - this is what lets StartAsync's own Connecting -> Streaming attempt and
    // HandleProviderEvent's SessionStarted-driven attempt for a SessionStartsOnWebSocketOpen
    // provider (e.g. Deepgram's Metadata frame) race safely: whichever lands first wins, and the
    // other observes success rather than a false "conflict".
    //
    // `onSuccess`, if given, runs exactly once when the transition succeeds (whether by actually
    // writing State or via the "already there" no-op) and always BEFORE StateChanged is raised for
    // an actual write - so a caller like HandleCloseResult can use it to finish bookkeeping
    // (failure message, _receivedTerminalClose, the startup-failure exception) that a subscriber
    // reacting to StateChanged, or a caller that just observed State == next, depends on already
    // being populated. It does not run when `guard` rejects the transition.
    private bool TryChangeStateCore(Func<StreamingConnectionState, bool> guard, StreamingConnectionState next, Action? onSuccess = null)
    {
        bool alreadyThere;

        lock (_stateLock)
        {
            alreadyThere = State == next;
            if (!alreadyThere)
            {
                if (!guard(State))
                    return false;

                State = next;
            }
        }

        onSuccess?.Invoke();

        if (!alreadyThere)
            Raise(StateChanged, next);

        return true;
    }

    // Unconditional transition. Safe to call from a single call site that only ever runs on one
    // logical thread of execution at a time (e.g. StopAsync's own sequential shutdown). Anywhere
    // a background thread (the receive loop) can concurrently observe/mutate State while the
    // caller is mid-await, use TryChangeState/TryChangeStateUnless instead so the check-then-act
    // is atomic.
    private void ChangeState(StreamingConnectionState state) => TryChangeStateCore(_ => true, state);

    // Atomically transitions State from `expected` to `next` (or treats State already being `next`
    // as success - see TryChangeStateCore). Returns whether the transition landed - callers that
    // only expected to move State forward from a specific prior state (e.g. StartAsync's
    // Connecting -> Streaming, TryReconnectAsync's Reconnecting -> Streaming/Ready) use the return
    // value to detect that something else (typically HandleCloseResult moving to Error off the
    // receive-loop thread) already moved State elsewhere in the gap since it was last read,
    // instead of blindly clobbering that concurrent transition.
    private bool TryChangeState(StreamingConnectionState expected, StreamingConnectionState next) =>
        TryChangeStateCore(current => current == expected, next);

    // Atomically transitions State to `next` unless State currently equals one of
    // `excludedStates` (or is already `next` - see TryChangeStateCore). Used where the guard is
    // "don't overwrite these specific states" rather than "only proceed from this one expected
    // state" - e.g. HandleCloseResult and the in-band provider-error path moving to Error unless
    // StopAsync has already moved State to Disconnecting/Idle out from under them. Returns whether
    // the transition landed - including the case where State was already `next` (e.g. a terminal
    // close arriving after an in-band provider error already set Error), so bookkeeping that only
    // makes sense once is still driven by the return value, not by "did we actually write".
    private bool TryChangeStateUnless(StreamingConnectionState next, params StreamingConnectionState[] excludedStates) =>
        TryChangeStateCore(current => Array.IndexOf(excludedStates, current) < 0, next);

    // Single chokepoint for every event raise. Drops all callbacks when _suppressDispatch is set
    // (synchronous Dispose() teardown) so no subscriber can marshal back to a blocked caller, and
    // invokes each subscriber independently so one throwing handler cannot starve the rest.
    private void Raise<T>(Action<T>? handler, T arg)
    {
        if (_suppressDispatch || handler == null)
            return;

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((Action<T>)target)(arg);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StreamingTranscriptionClient: event handler threw - {ex.Message}");
            }
        }
    }

    private void Raise(Action<double, double>? handler, double arg1, double arg2)
    {
        if (_suppressDispatch || handler == null)
            return;

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((Action<double, double>)target)(arg1, arg2);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StreamingTranscriptionClient: event handler threw - {ex.Message}");
            }
        }
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
