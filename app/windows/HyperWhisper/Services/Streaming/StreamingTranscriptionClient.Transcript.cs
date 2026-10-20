using System;
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services.Streaming;

// Transcript assembly and the provider-event fan-out.
public sealed partial class StreamingTranscriptionClient
{
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
                OnSessionStarted();
                return;

            case StreamingProviderEvent.PartialTranscript partial:
                OnPartialTranscript(partial);
                return;

            case StreamingProviderEvent.FinalTranscript final:
                OnFinalTranscript(final);
                return;

            case StreamingProviderEvent.FinalTranscriptAndSessionComplete complete:
                OnFinalTranscriptAndSessionComplete(complete);
                return;

            case StreamingProviderEvent.SessionComplete complete:
                OnSessionComplete(complete);
                return;

            case StreamingProviderEvent.Warning warning:
                OnWarning(warning);
                return;

            case StreamingProviderEvent.Error error:
                OnError(error);
                return;

            case StreamingProviderEvent.Metadata metadata:
                OnMetadata(metadata);
                return;
        }
    }

    private void OnSessionStarted()
    {
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
    }

    private void OnPartialTranscript(StreamingProviderEvent.PartialTranscript partial)
    {
        CurrentPartial = TranscriptionTextProcessing.ProcessVoiceCommands(partial.Text);
        Raise(LiveTranscriptChanged, BuildLiveTranscript(CurrentPartial));
    }

    private void OnFinalTranscript(StreamingProviderEvent.FinalTranscript final)
    {
        var finalSegment = AppendFinalTranscript(final.Text);
        CurrentPartial = string.Empty;
        var finalText = FinalText;
        if (!string.IsNullOrWhiteSpace(finalSegment))
            Raise(FinalTranscriptSegmentReceived, finalSegment);
        Raise(FinalTranscriptChanged, finalText);
        Raise(LiveTranscriptChanged, finalText);
    }

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
    private void OnFinalTranscriptAndSessionComplete(StreamingProviderEvent.FinalTranscriptAndSessionComplete complete)
    {
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
    }

    private void OnSessionComplete(StreamingProviderEvent.SessionComplete complete)
    {
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
    }

    private void OnWarning(StreamingProviderEvent.Warning warning)
    {
        var warningMessage = warning.RemainingSeconds.HasValue
            ? $"{warning.Message} ({Math.Ceiling(warning.RemainingSeconds.Value):0} seconds remaining)"
            : warning.Message;
        LoggingService.Warn($"StreamingTranscriptionClient: provider warning - {warningMessage}");
        Raise(WarningReceived, warningMessage);
    }

    // NOT classified through SharedCoreBridge.ClassifyLiveErrorMessage, deliberately.
    // macOS needs that split because its client keeps reconnecting after an error frame;
    // this one moves to Error, and RunReceiveLoopAsync's `State is ... Error` guard then
    // ends the session for EVERY provider error frame, terminal or not. So there is no
    // doomed-reconnect fan-out here to suppress - the bias is the opposite one, and
    // wiring the classifier in would LOOSEN termination: a transient frame would start
    // keeping its reconnect. That is a real behaviour change on a shipped path and it
    // belongs to the client rework, not to issue #281's single-sourcing.
    private void OnError(StreamingProviderEvent.Error error)
    {
        LoggingService.Error($"StreamingTranscriptionClient: provider error - {error.Message}");
        Raise(ErrorReceived, error.Message);
        _sessionStartedTcs?.TrySetException(new InvalidOperationException(error.Message));

        // Don't reclassify an in-flight intentional shutdown (StopAsync already moved to
        // Disconnecting/Idle) as this in-band provider error.
        TryChangeStateUnless(
            StreamingConnectionState.Error,
            StreamingConnectionState.Disconnecting,
            StreamingConnectionState.Idle);
    }

    private void OnMetadata(StreamingProviderEvent.Metadata metadata)
    {
        LoggingService.Debug($"StreamingTranscriptionClient: provider metadata - {metadata.Raw}");
    }

    internal string? AppendFinalTranscript(string text)
    {
        // Mirrors the batch path's order (TranscriptionOrchestrator.RunAsync):
        // RemoveFillerWords -> ProcessVoiceCommands -> (vocabulary is applied earlier,
        // upstream, for streaming). Only confirmed/final deltas reach this method
        // (see OnFinalTranscript / OnFinalTranscriptAndSessionComplete, which
        // HandleProviderEvent dispatches to) - interim/partial text must never be filler-stripped, to avoid words
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
}
