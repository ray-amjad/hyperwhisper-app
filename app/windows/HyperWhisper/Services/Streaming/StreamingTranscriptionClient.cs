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
                // flight. TryChangeState performs the check-and-set atomically under
                // _stateLock, so it cannot clobber a concurrently-recorded Error back to
                // Streaming, and it tells us whether our own transition actually landed.
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

            LoggingService.Info($"StreamingTranscriptionClient: connected to {_strategy.TranscriptionProviderLabel}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _sessionCts?.Cancel();
            webSocket.Dispose();
            CleanupWebSocket();
            CurrentPartial = string.Empty;
            ChangeState(StreamingConnectionState.Idle);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggingService.Error($"StreamingTranscriptionClient: connect failed for {_strategy.TranscriptionProviderLabel}", ex);
            Raise(ErrorReceived, ex.Message);
            ChangeState(StreamingConnectionState.Error);
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
                ChangeState(StreamingConnectionState.Error);
            }
        }
    }

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

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);

                var webSocket = new ClientWebSocket();
                _strategy.ConfigureWebSocket(webSocket, _config);
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
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StreamingTranscriptionClient: reconnect attempt {attempt} failed - {ex.Message}");
            }
        }

        SentryService.AddBreadcrumb(
            "streaming_reconnect_failed",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = _strategy.TranscriptionProviderLabel });
        Raise(ErrorReceived, "Streaming connection was lost and could not be restored.");
        _sessionStartedTcs?.TrySetException(new InvalidOperationException("Streaming connection was lost and could not be restored."));
        ChangeState(StreamingConnectionState.Error);
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

        // Only genuinely non-recoverable codes end the session immediately. Standard transient
        // codes (1001 Going Away, 1006 Abnormal/no close frame, 1012 Service Restart, 1013 Try
        // Again Later, and anything else not explicitly known to be fatal) fall through to the
        // existing reconnect/backoff path in RunReceiveLoopAsync, same as before this class
        // recognized any terminal codes beyond HyperWhisper's own 4001/4002.
        if (!IsTerminalCloseCode(closeCode))
            return;

        // We're already mid/post our own intentional shutdown (StopAsync sets Disconnecting
        // before running the stop sequence, and _sessionCts isn't cancelled until after that
        // completes, so the receive loop can still reach here concurrently). TryChangeStateUnless
        // performs the "are we shutting down" check and the write to Error atomically under
        // _stateLock, so it cannot race with StopAsync moving to Disconnecting/Idle in between -
        // and it doubles as the guard against reclassifying our own shutdown as a provider error.
        if (!TryChangeStateUnless(StreamingConnectionState.Error, StreamingConnectionState.Disconnecting, StreamingConnectionState.Idle))
            return;

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
    }

    // Codes that mean the provider cannot recover the session and it should end immediately
    // instead of burning reconnect attempts. 4001/4002 are HyperWhisper's own app-level codes
    // (credits exhausted / max session duration). 1008 (Policy Violation) and 1011 (Internal
    // Error) are what Deepgram uses for protocol/data errors it cannot recover from. Everything
    // else - including the standard "transient" codes 1001 (Going Away), 1006 (Abnormal, no
    // close frame), 1012 (Service Restart), and 1013 (Try Again Later) - is treated as
    // recoverable and falls through to the existing reconnect/backoff path.
    private static bool IsTerminalCloseCode(int closeCode) => closeCode is 4001 or 4002 or 1008 or 1011;

    private void HandleProviderEvent(StreamingProviderEvent? providerEvent)
    {
        switch (providerEvent)
        {
            case null:
                return;

            case StreamingProviderEvent.SessionStarted:
                _sessionStartedTcs?.TrySetResult();
                ChangeState(StreamingConnectionState.Streaming);
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

            case StreamingProviderEvent.FinalTranscriptAndSessionComplete complete:
                var completeSegment = AppendFinalTranscript(complete.Text);
                CurrentPartial = string.Empty;
                var completedFinalText = FinalText;
                if (!string.IsNullOrWhiteSpace(completeSegment))
                    Raise(FinalTranscriptSegmentReceived, completeSegment);
                Raise(FinalTranscriptChanged, completedFinalText);
                _sessionCompletedTcs?.TrySetResult();
                Raise(SessionCompleted, complete.DurationSeconds, complete.CreditsUsed);
                return;

            case StreamingProviderEvent.SessionComplete complete:
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

            case StreamingProviderEvent.Error error:
                LoggingService.Error($"StreamingTranscriptionClient: provider error - {error.Message}");
                Raise(ErrorReceived, error.Message);
                _sessionStartedTcs?.TrySetException(new InvalidOperationException(error.Message));
                ChangeState(StreamingConnectionState.Error);
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
        // SmartSpacing.RemoveFillerWords is a hardcoded-English regex with no
        // language parameter (unlike the shared Rust remove_filler_words used on
        // macOS/the batch path, which no-ops outside en/en-*) - gate it on the
        // session's language here so a non-English stream doesn't get real words
        // stripped (e.g. German "er"/"um").
        var shouldRemoveFillers = _config.RemoveFillerWords && IsEnglishLanguage(_config.Language);

        bool isFirstConfirmedDelta;
        lock (_finalTranscriptLock)
        {
            isFirstConfirmedDelta = _finalTranscript.Length == 0;
        }

        var withoutFillers = shouldRemoveFillers
            ? SmartSpacing.RemoveFillerWords(text)
            : text;

        // SmartSpacing.RemoveFillerWords recapitalizes the word after a leading
        // filler on the assumption that it is processing the START of a whole
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

    /// <summary>
    /// True when <paramref name="language"/> is English ("en" or a regional
    /// variant like "en-GB"). SmartSpacing.RemoveFillerWords has no language
    /// parameter of its own (unlike the shared Rust remove_filler_words used on
    /// macOS/the batch path) - callers must gate on this before invoking it for
    /// any non-English, unset, or "auto" session.
    /// </summary>
    private static bool IsEnglishLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        return language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
               language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
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

    // Unconditional transition. Safe to call from a single call site that only ever runs on one
    // logical thread of execution at a time (e.g. StopAsync's own sequential shutdown). Anywhere
    // a background thread (the receive loop) can concurrently observe/mutate State while the
    // caller is mid-await, use TryChangeState/TryChangeStateUnless instead so the check-then-act
    // is atomic.
    private void ChangeState(StreamingConnectionState state)
    {
        lock (_stateLock)
        {
            if (State == state)
                return;

            State = state;
        }

        Raise(StateChanged, state);
    }

    // Atomically transitions State from `expected` to `next`, under the same lock ChangeState
    // uses. Returns whether the transition actually happened - callers that only expected to
    // move State forward from a specific prior state (e.g. StartAsync's Connecting -> Streaming,
    // TryReconnectAsync's Reconnecting -> Streaming/Ready) use the return value to detect that
    // something else (typically HandleCloseResult moving to Error off the receive-loop thread)
    // already moved State elsewhere in the gap since it was last read, instead of blindly
    // clobbering that concurrent transition.
    private bool TryChangeState(StreamingConnectionState expected, StreamingConnectionState next)
    {
        lock (_stateLock)
        {
            if (State != expected)
                return false;

            State = next;
        }

        Raise(StateChanged, next);
        return true;
    }

    // Atomically transitions State to `next` unless State currently equals one of
    // `excludedStates`, under the same lock ChangeState/TryChangeState use. Used where the guard
    // is "don't overwrite these specific states" rather than "only proceed from this one
    // expected state" - e.g. HandleCloseResult moving to Error unless StopAsync has already
    // moved State to Disconnecting/Idle out from under it. Returns whether the transition
    // actually happened.
    private bool TryChangeStateUnless(StreamingConnectionState next, params StreamingConnectionState[] excludedStates)
    {
        lock (_stateLock)
        {
            foreach (var excluded in excludedStates)
            {
                if (State == excluded)
                    return false;
            }

            if (State == next)
                return false;

            State = next;
        }

        Raise(StateChanged, next);
        return true;
    }

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
