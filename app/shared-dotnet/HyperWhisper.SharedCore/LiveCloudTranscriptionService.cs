using System.Net.WebSockets;
using System.Text;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// Portable realtime STT runner. The shared Rust core owns the five provider
/// wire protocols (issue #281) while the injected WebSocket owns transport:
/// every frame this class puts on the wire, and every event it reads off one,
/// comes from <see cref="ILiveTranscriptionProtocol"/>. What is left here is
/// I/O — connect, send, receive, the bounded close — plus the bounds this head
/// enforces on both directions.
///
/// Diagnostics intentionally contain no URI, headers, audio bytes, provider
/// payloads or transcript text.
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

    /// <summary>
    /// The PCM sample rate the capture graph must produce for this provider.
    /// The five WebSocket providers answer from the shared core (issue #281);
    /// the two local engines keep their literal here, because they are not
    /// WebSocket protocols and the core deliberately has no arm for them.
    /// </summary>
    public static int GetRequiredSampleRate(LiveTranscriptionProvider provider) => provider switch
    {
        LiveTranscriptionProvider.ParakeetLocal
            or LiveTranscriptionProvider.NemotronLocal => 16000,
        LiveTranscriptionProvider.OpenAi
            or LiveTranscriptionProvider.Deepgram
            or LiveTranscriptionProvider.ElevenLabs
            or LiveTranscriptionProvider.Grok
            or LiveTranscriptionProvider.HyperWhisperCloud => SharedCoreBridge.LiveRequiredSampleRate(provider),
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
            // The protocol owns a Rust handle (issue #281) and MUST be disposed:
            // it is an `Arc` this process holds a raw pointer to, and letting it
            // fall out of scope leaks the session until the finalizer runs.
            using var protocol = LiveTranscriptionProtocolFactory.Create(config);
            // A monotonic reading, restarted per session, for everything in the
            // protocol that is time-shaped: Deepgram's keepalive and OpenAI's
            // commit interval. The core reads no clock of its own.
            var clock = System.Diagnostics.Stopwatch.StartNew();
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
                // The send opportunity comes AFTER this chunk, which is the order
                // this head has always used and the opposite of Windows'. It
                // matters for one provider: OpenAI's periodic commit then covers
                // the chunk just appended instead of leaving it for the next one,
                // so no audio is held back a round. Either order is correct on
                // the wire — a commit always follows the appends it claims — and
                // the core cannot tell them apart, because it is told a byte
                // count and a clock reading and nothing else.
                foreach (var control in protocol.AudioOpportunityFrames(clock.ElapsedMilliseconds))
                {
                    await socket.SendAsync(control.Data, control.Type, cancellationToken).ConfigureAwait(false);
                }
            }

            // Whether this session is stopping normally, decided BEFORE the stop
            // sequence runs and reused by the post-close drain below. A session
            // that already failed, or that the provider already finished, has
            // nothing left to drain and must not be charged for waiting.
            var stoppingNormally = state.Failure is null && !state.Completed.Task.IsCompleted;
            if (stoppingNormally)
            {
                Observe(state, "draining");
                await RunStopSequenceAsync(
                    socket,
                    protocol.StopSequence(clock.ElapsedMilliseconds),
                    receiveTask,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }

            using (var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                closeCancellation.CancelAfter(CloseTimeout);
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, closeCancellation.Token).ConfigureAwait(false);
                    await DrainAfterCloseAsync(stoppingNormally, receiveTask, closeCancellation.Token).ConfigureAwait(false);
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

    /// <summary>
    /// Holds the session open, bounded by <c>CloseTimeout</c>, until the receive
    /// loop ends — the last chance a late final transcript has to be counted.
    ///
    /// This exists to close a PHASE SEAM against Windows, not to add a drain the
    /// protocols did not ask for. The per-provider stop steps this head now runs
    /// are the Windows client's shipped values, but the two heads spend the close
    /// itself very differently:
    ///
    /// On Windows, <c>StreamingStopAction.Close</c> is handled inside the stop
    /// sequence by <c>CloseWebSocketAsync</c> → <c>ClientWebSocket.CloseAsync</c>,
    /// the full RFC 6455 handshake: it writes the close frame and then blocks
    /// until the server's close frame comes back. Its receive loop is still live
    /// throughout, because <c>_sessionCts.Cancel()</c> only runs in the
    /// <c>finally</c> after every step. Windows therefore keeps draining right
    /// through the close, and gets that window for free.
    ///
    /// Here the close is write-only. <see cref="LiveStopAction.Close"/> ends the
    /// step loop without closing, and the bounded <c>CloseAsync</c> in
    /// <see cref="TranscribeAsync"/> reaches <c>ClientStreamingWebSocket</c>,
    /// which calls <c>CloseOutputAsync</c> — it returns the moment the close frame
    /// is written and never waits for the peer. Microseconds later
    /// <c>sessionCancellation.Cancel()</c> kills the receive loop. So on this head
    /// the equivalent window has to be taken explicitly, and this is it.
    ///
    /// Without it, a provider whose final frame lands after the last stop step
    /// loses it: ElevenLabs' sequence is a bare <c>Close</c>, so its budget was
    /// zero, and a <c>commit_strategy=vad</c> <c>committed_transcript</c> arriving
    /// 100 ms after the last audio chunk came back as <c>NoSpeech</c>.
    ///
    /// <paramref name="stoppingNormally"/> is the stop-sequence condition, read
    /// before that sequence ran: a session that already failed, or that the
    /// provider already completed, skips the wait entirely rather than paying up
    /// to two seconds while unwinding.
    ///
    /// <c>CloseTimeout</c> (2 s) is left as the budget rather than raised. It is
    /// already more generous than the largest per-provider drain this head used
    /// before the ordered stop sequence existed (ElevenLabs 1 s; Deepgram and
    /// OpenAI 2 s), and those two now spend their in-sequence waits — 500 ms and
    /// 1 s — before ever reaching here, so both end up strictly ahead. The two
    /// providers that wait on the session-complete EVENT keep their own 10 s step
    /// and normally arrive with <paramref name="receiveTask"/> already finished.
    ///
    /// <see cref="Task.WhenAny(Task[])"/> deliberately does not observe
    /// <paramref name="receiveTask"/>'s exceptions: a faulted receive loop must
    /// surface at the <c>await receiveTask</c> in <see cref="TranscribeAsync"/>,
    /// which classifies it, and not be thrown from inside the close block, whose
    /// only <c>catch</c> is for the timeout.
    /// </summary>
    private static async Task DrainAfterCloseAsync(
        bool stoppingNormally,
        Task receiveTask,
        CancellationToken closeToken)
    {
        if (!stoppingNormally || receiveTask.IsCompleted)
        {
            return;
        }
        // receiveTask completing means the provider closed or the session
        // finished; the token firing means CloseTimeout elapsed.
        await Task.WhenAny(receiveTask, Task.Delay(Timeout.Infinite, closeToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the protocol's ordered stop path (issue #281).
    ///
    /// This replaced a flat frame list plus one drain timeout, which could not
    /// express what these protocols do. Deepgram sends <c>Finalize</c>, waits
    /// 500 ms for the flush, then sends <c>CloseStream</c> — sending both back to
    /// back, as this head used to, lets the close be processed before the flush
    /// and loses the finalized tail. HyperWhisper Cloud and xAI wait on the
    /// session-complete EVENT, which is what carries <c>credits_used</c>: a flat
    /// timeout either throws that away or adds ten seconds to every stop.
    ///
    /// <see cref="LiveStopAction.Wait"/> is unconditional, matching the Windows
    /// client this ordering comes from. It is a gap the protocol asked for, not a
    /// deadline to race the session against.
    ///
    /// <see cref="LiveStopAction.Close"/> ends the loop WITHOUT closing here. The
    /// bounded <c>CloseAsync</c> block in <see cref="TranscribeAsync"/> performs
    /// the one close, because it also has to run on the paths that skip the stop
    /// sequence entirely (a failure, or a session the provider already finished),
    /// and it is the only thing that bounds the close with <c>CloseTimeout</c>.
    /// Closing here as well would close twice. That close is write-only, unlike
    /// Windows' blocking handshake, so <see cref="DrainAfterCloseAsync"/> follows
    /// it — see there for why the drain cannot live in this loop.
    /// </summary>
    private static async Task RunStopSequenceAsync(
        IStreamingWebSocket socket,
        IReadOnlyList<LiveStopStep> steps,
        Task receiveTask,
        SessionState state,
        CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            switch (step.Action)
            {
                case LiveStopAction.SendMessage when step.Payload is not null:
                    await socket.SendAsync(step.Payload, step.MessageType, cancellationToken).ConfigureAwait(false);
                    break;
                case LiveStopAction.Wait:
                    await Task.Delay(step.WaitAfter ?? TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                    break;
                case LiveStopAction.WaitForSessionComplete:
                    if (!state.Completed.Task.IsCompleted)
                    {
                        var timeout = Task.Delay(step.WaitAfter ?? TimeSpan.Zero, cancellationToken);
                        await Task.WhenAny(receiveTask, state.Completed.Task, timeout).ConfigureAwait(false);
                    }
                    break;
                case LiveStopAction.Close:
                    return;
                default:
                    break;
            }
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
                if (state.Failure is not null || providerEvent.Kind == LiveProtocolEventKind.Complete)
                {
                    state.Completed.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                // The provider's own wording never reaches the caller — the
                // message below stays fixed and diagnostics stay payload-free.
                // It is read once, here, only to decide whether a reconnect
                // could ever succeed (issue #281).
                state.Failure = new LiveTranscriptionFailure(
                    value.ErrorCode,
                    "The streaming provider rejected the session.",
                    state.Provider,
                    IsTerminal: SharedCoreBridge.ClassifyLiveErrorMessage(value.Text ?? string.Empty)
                        == PortableLiveErrorOutcome.Terminal);
                break;
            case LiveProtocolEventKind.Started:
                Observe(state, "started");
                break;
            case LiveProtocolEventKind.Warning:
                // Only HyperWhisper Cloud warns, and a warning is not a failure —
                // it must never end the session. The wording stays inside this
                // assembly for the same reason an error's does: the diagnostic
                // carries a fixed state name and no provider payload.
                Observe(state, "warning");
                break;
            case LiveProtocolEventKind.Metadata:
                // Deepgram's voice-activity frames. They carry the raw provider
                // JSON, so they are counted and dropped, never logged.
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

    /// <summary>
    /// The single exit for a failed session. A failure the session ALREADY
    /// RECORDED outranks the one named here: <see cref="SessionState.Failure"/>
    /// is what the receive loop concluded while the frames were in front of it,
    /// and every caller of this method is either a guess made while unwinding or
    /// a case where no verdict was ever recorded.
    ///
    /// The path that forced this, no race required: HyperWhisper Cloud answers
    /// <c>{"type":"error","message":"Credit balance exhausted"}</c>. The receive
    /// loop classifies the wording and records
    /// <c>ProviderUnavailable, IsTerminal: true</c>; the audio loop breaks; the
    /// stop sequence is skipped; the bounded <c>CloseAsync</c> then throws
    /// <see cref="WebSocketException"/> on the already-dead socket. The catch arm
    /// used to overwrite that verdict with a fresh <c>Network</c> /
    /// <c>IsTerminal: false</c>, and <c>LiveStreamingSessionController</c> read
    /// the non-terminal flag as permission for two more reconnects into the same
    /// exhausted balance — the exact behaviour the shared terminal-error policy
    /// (issue #281) exists to end.
    ///
    /// Every call site was checked against this rule:
    /// <list type="bullet">
    /// <item>The connect timeout, and the <c>NoSpeech</c> exit, both run where
    /// <c>state.Failure</c> is provably <c>null</c> — before the receive loop is
    /// started, and on the success path that has just tested it — so nothing
    /// moves for them.</item>
    /// <item>The <c>Cancelled</c> arm and the four transport/state catch arms in
    /// <see cref="TranscribeAsync"/> all unwind AFTER the receive loop may have
    /// concluded, and are exactly the arms that were discarding it.</item>
    /// </list>
    ///
    /// The recorded failure keeps its own message, which is fixed wording chosen
    /// in this assembly and carries no provider payload, so preferring it leaks
    /// nothing the discarded message would not have.
    /// </summary>
    private static LiveTranscriptionResult Failed(
        SessionState state,
        LiveTranscriptionFailureCode code,
        string message) =>
        new(
            null,
            state.Failure ?? new LiveTranscriptionFailure(code, message, state.Provider),
            state.AudioChunksSent,
            state.MessagesReceived);

    /// <summary>
    /// The RFC 6455 §7.4.1 non-recoverable close codes, from the shared core
    /// (issue #281) so this head, Windows and macOS cannot drift on the set.
    /// </summary>
    private static bool IsTerminalClose(int value) => SharedCoreBridge.IsTerminalLiveCloseCode(value);

    private sealed class SessionState(LiveTranscriptionProvider provider)
    {
        public LiveTranscriptionProvider Provider { get; } = provider;
        public StringBuilder Transcript { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LiveTranscriptionFailure? Failure { get; set; }
        public int AudioChunksSent { get; set; }
        public int MessagesReceived { get; set; }
    }
}
