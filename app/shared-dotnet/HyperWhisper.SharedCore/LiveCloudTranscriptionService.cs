using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// Portable realtime STT runner. Strategies own provider wire protocols while
/// the injected WebSocket owns transport. Diagnostics intentionally contain no
/// URI, headers, audio bytes, provider payloads or transcript text.
/// </summary>
public sealed class LiveCloudTranscriptionService
{
    private const int MaxAudioChunkBytes = 256 * 1024;
    private const int MaxInboundMessageBytes = 1024 * 1024;
    private const int MaxTranscriptChars = 512 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly IStreamingWebSocketFactory _webSockets;
    private readonly ILiveTranscriptSink? _transcripts;
    private readonly ILiveTranscriptionDiagnostics? _diagnostics;

    public LiveCloudTranscriptionService(
        IStreamingWebSocketFactory? webSockets = null,
        ILiveTranscriptSink? transcripts = null,
        ILiveTranscriptionDiagnostics? diagnostics = null)
    {
        _webSockets = webSockets ?? new ClientStreamingWebSocketFactory();
        _transcripts = transcripts;
        _diagnostics = diagnostics;
    }

    public static int GetRequiredSampleRate(LiveTranscriptionProvider provider) => provider switch
    {
        LiveTranscriptionProvider.OpenAi => 24000,
        LiveTranscriptionProvider.Deepgram
            or LiveTranscriptionProvider.ElevenLabs
            or LiveTranscriptionProvider.Grok
            or LiveTranscriptionProvider.GeminiTranscribe
            or LiveTranscriptionProvider.HyperWhisperCloud
            or LiveTranscriptionProvider.ParakeetLocal
            or LiveTranscriptionProvider.NemotronLocal => 16000,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public async Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(audio);
        var state = new SessionState(config.Provider);
        try
        {
            var protocol = LiveTranscriptionProtocolFactory.Create(config);
            await using var socket = _webSockets.Create();
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCancellation.CancelAfter(ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(protocol.ConnectOptions, connectCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Failed(state, LiveTranscriptionFailureCode.Timeout, "The streaming connection timed out.");
                }
            }
            Observe(state, "connected");

            foreach (var frame in protocol.StartFrames)
            {
                await socket.SendAsync(frame.Data, frame.Type, cancellationToken).ConfigureAwait(false);
            }

            var receiveTask = ReceiveLoopAsync(socket, protocol, state, sessionCancellation.Token);

            // Some vendors discard audio that arrives before their setup
            // handshake completes, which costs the opening words of the
            // dictation. Hold the pump until the provider says it is ready — the
            // capture side buffers meanwhile — bounded so a socket that never
            // acknowledges fails cleanly instead of hanging.
            if (protocol.StartTimeout > TimeSpan.Zero)
            {
                await WaitForStartAsync(state, receiveTask, protocol.StartTimeout, cancellationToken).ConfigureAwait(false);
            }

            await foreach (var chunk in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (state.Failure is not null || state.Completed.Task.IsCompleted)
                {
                    break;
                }
                if (chunk.Length == 0)
                {
                    continue;
                }
                if (chunk.Length > MaxAudioChunkBytes)
                {
                    state.Failure = new LiveTranscriptionFailure(
                        LiveTranscriptionFailureCode.BufferLimit,
                        "An audio chunk exceeded the 256 KiB streaming limit.",
                        config.Provider);
                    break;
                }
                if ((chunk.Length & 1) != 0)
                {
                    state.Failure = new LiveTranscriptionFailure(
                        LiveTranscriptionFailureCode.InvalidRequest,
                        "PCM16 audio chunks must contain complete 16-bit samples.",
                        config.Provider);
                    break;
                }
                var frame = protocol.EncodeAudio(chunk.Span);
                await socket.SendAsync(frame.Data, frame.Type, cancellationToken).ConfigureAwait(false);
                state.AudioChunksSent++;
                foreach (var control in protocol.AudioOpportunityFrames())
                {
                    await socket.SendAsync(control.Data, control.Type, cancellationToken).ConfigureAwait(false);
                }
            }

            if (state.Failure is null && !state.Completed.Task.IsCompleted)
            {
                // Publish the stop BEFORE the frames go out, so the receive loop
                // reads a provider "complete" that answers this stop as terminal.
                state.StopRequested = true;
                foreach (var frame in protocol.StopFrames())
                {
                    await socket.SendAsync(frame.Data, frame.Type, cancellationToken).ConfigureAwait(false);
                }
                Observe(state, "draining");
                var drain = Task.Delay(protocol.DrainTimeout, cancellationToken);
                await Task.WhenAny(receiveTask, state.Completed.Task, drain).ConfigureAwait(false);
            }

            using (var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                closeCancellation.CancelAfter(CloseTimeout);
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, closeCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Bounded shutdown already achieved; disposal aborts the socket.
                }
            }
            sessionCancellation.Cancel();
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
            {
            }

            if (state.Failure is not null)
            {
                return new LiveTranscriptionResult(null, state.Failure, state.AudioChunksSent, state.MessagesReceived);
            }
            var finalized = HyperwhisperCoreMethods.FinalizeStreamingText(state.Transcript.ToString());
            if (string.IsNullOrWhiteSpace(finalized))
            {
                return Failed(state, LiveTranscriptionFailureCode.NoSpeech, "No speech was detected in the streaming session.");
            }
            Observe(state, "completed");
            return new LiveTranscriptionResult(finalized, null, state.AudioChunksSent, state.MessagesReceived);
        }
        catch (LiveProtocolException exception)
        {
            return new LiveTranscriptionResult(null, exception.Failure, state.AudioChunksSent, state.MessagesReceived);
        }
        catch (OperationCanceledException)
        {
            return Failed(state, LiveTranscriptionFailureCode.Cancelled, "Streaming transcription was cancelled.");
        }
        catch (JsonException)
        {
            return Failed(state, LiveTranscriptionFailureCode.Protocol, "The streaming provider returned an invalid message.");
        }
        catch (InvalidDataException)
        {
            return Failed(state, LiveTranscriptionFailureCode.BufferLimit, "A streaming provider message exceeded the 1 MiB limit.");
        }
        catch (WebSocketException)
        {
            return Failed(state, LiveTranscriptionFailureCode.Network, "The streaming connection failed.");
        }
        catch (HttpRequestException)
        {
            return Failed(state, LiveTranscriptionFailureCode.Network, "The streaming provider could not be reached.");
        }
        catch (IOException)
        {
            return Failed(state, LiveTranscriptionFailureCode.Network, "The streaming transport failed.");
        }
        catch (InvalidOperationException)
        {
            return Failed(state, LiveTranscriptionFailureCode.Unknown, "The streaming session could not be completed.");
        }
    }

    private async Task ReceiveLoopAsync(
        IStreamingWebSocket socket,
        ILiveTranscriptionProtocol protocol,
        SessionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    int? closeCode = frame.CloseStatus.HasValue ? (int)frame.CloseStatus.Value : null;
                    if (closeCode is not null and not 1000 and not 1001)
                    {
                        state.Failure = new LiveTranscriptionFailure(
                            IsTerminalClose(closeCode.Value)
                                ? LiveTranscriptionFailureCode.Protocol
                                : LiveTranscriptionFailureCode.ProviderUnavailable,
                            "The streaming provider closed the session unexpectedly.",
                            protocol.Provider,
                            closeCode);
                    }
                    state.Completed.TrySetResult();
                    Observe(state, "closed", closeCode);
                    return;
                }
                if (!frame.EndOfMessage || frame.Data.Length > MaxInboundMessageBytes)
                {
                    throw new InvalidDataException("Streaming provider message exceeded the bounded frame contract.");
                }
                if (frame.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                state.MessagesReceived++;
                var providerEvent = protocol.Parse(frame.Data);
                HandleEvent(providerEvent, state);
                if (state.Failure is not null)
                {
                    state.Completed.TrySetResult();
                    return;
                }
                if (providerEvent.Kind == LiveProtocolEventKind.Complete)
                {
                    // A provider "complete" is only terminal once the client has
                    // asked to stop. Before that it is a TURN boundary for the
                    // vendors that emit one there (Gemini's `generationComplete`
                    // fires at every pause): the turn's text is already committed
                    // by HandleEvent, so keep the session alive rather than
                    // reporting success on a half-finished dictation.
                    if (protocol.CompleteEndsSessionBeforeStop || state.StopRequested)
                    {
                        state.Completed.TrySetResult();
                        return;
                    }
                    Observe(state, "turn_complete");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            state.Failure = new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.Protocol,
                "The streaming provider returned an invalid message.",
                protocol.Provider);
            state.Completed.TrySetResult();
        }
        catch (InvalidDataException)
        {
            state.Failure = new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.BufferLimit,
                "A streaming provider message exceeded the 1 MiB limit.",
                protocol.Provider);
            state.Completed.TrySetResult();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            state.Failure = new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.Network,
                "The streaming receive loop failed.",
                protocol.Provider);
            state.Completed.TrySetResult();
        }
    }

    /// <summary>
    /// Waits for the provider's session-started acknowledgement, or for the
    /// session to end first. A provider that never acknowledges leaves a
    /// <see cref="LiveTranscriptionFailureCode.Timeout"/> failure on the state,
    /// which the caller's audio loop reads on its first iteration.
    /// </summary>
    private async Task WaitForStartAsync(
        SessionState state,
        Task receiveTask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (state.Started.Task.IsCompleted)
        {
            return;
        }
        Observe(state, "awaiting_start");
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var expiry = Task.Delay(timeout, startCancellation.Token);
        await Task.WhenAny(state.Started.Task, state.Completed.Task, receiveTask, expiry).ConfigureAwait(false);
        startCancellation.Cancel();
        if (state.Started.Task.IsCompleted
            || state.Failure is not null
            || state.Completed.Task.IsCompleted
            || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        state.Failure = new LiveTranscriptionFailure(
            LiveTranscriptionFailureCode.Timeout,
            "The streaming provider did not acknowledge the session.",
            state.Provider);
    }

    private void HandleEvent(LiveProtocolEvent value, SessionState state)
    {
        switch (value.Kind)
        {
            case LiveProtocolEventKind.Partial:
                PublishTranscript(value.Text, false);
                break;
            case LiveProtocolEventKind.Final:
                AppendFinal(value.Text, state);
                break;
            case LiveProtocolEventKind.Complete:
                AppendFinal(value.Text, state);
                break;
            case LiveProtocolEventKind.Error:
                state.Failure = new LiveTranscriptionFailure(
                    value.ErrorCode,
                    "The streaming provider rejected the session.",
                    state.Provider);
                break;
            case LiveProtocolEventKind.Started:
                state.Started.TrySetResult();
                Observe(state, "started");
                break;
            case LiveProtocolEventKind.Ignore:
            default:
                break;
        }
    }

    private void AppendFinal(string? text, SessionState state)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var additional = text.Length + (state.Transcript.Length == 0 ? 0 : 1);
        if (state.Transcript.Length + additional > MaxTranscriptChars)
        {
            state.Failure = new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.BufferLimit,
                "The streaming transcript exceeded the 512 Ki-character limit.",
                state.Provider);
            return;
        }
        if (state.Transcript.Length > 0)
        {
            state.Transcript.Append(' ');
        }
        state.Transcript.Append(text.Trim());
        PublishTranscript(text.Trim(), true);
    }

    private void PublishTranscript(string? text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        try
        {
            _transcripts?.OnTranscript(new LiveTranscriptUpdate(text, isFinal));
        }
        catch (Exception)
        {
            // A presentation subscriber cannot break provider I/O.
        }
    }

    private void Observe(SessionState state, string value, int? closeStatus = null)
    {
        try
        {
            _diagnostics?.OnDiagnostic(new LiveTranscriptionDiagnostic(
                state.Provider,
                value,
                state.AudioChunksSent,
                state.MessagesReceived,
                closeStatus));
        }
        catch (Exception)
        {
            // Diagnostics are isolated and contain no provider content.
        }
    }

    private static LiveTranscriptionResult Failed(
        SessionState state,
        LiveTranscriptionFailureCode code,
        string message) =>
        new(null, new LiveTranscriptionFailure(code, message, state.Provider), state.AudioChunksSent, state.MessagesReceived);

    private static bool IsTerminalClose(int value) => value is 1002 or 1003 or 1007 or 1008 or 1009 or 1011;

    private sealed class SessionState(LiveTranscriptionProvider provider)
    {
        /// <summary>
        /// Written by the audio pump, read by the receive loop on another
        /// thread, so it is volatile rather than an auto-property.
        /// </summary>
        public volatile bool StopRequested;

        public LiveTranscriptionProvider Provider { get; } = provider;
        public StringBuilder Transcript { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LiveTranscriptionFailure? Failure { get; set; }
        public int AudioChunksSent { get; set; }
        public int MessagesReceived { get; set; }
    }
}
