using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SpeechOutput;

namespace HyperWhisper.Linux;

public sealed record LinuxLiveFinalizationOutcome(
    PlatformResult Result,
    TextInjectionOutcome InjectionOutcome);

public static class LinuxLiveTranscriptionFinalizer
{
    public static async Task<LinuxLiveFinalizationOutcome> FinalizeAndPersistAsync(
        string rawTranscript,
        Transcript transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        ITranscriptionPostProcessor postProcessor,
        ITextInjectionService textInjection,
        ITranscriptionHistoryStore history,
        TranscriptionWorkflowRequest outputRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTranscript);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(mode);
        var raw = rawTranscript.Trim();
        var processingInput = raw;
        string? processingProvider = null;
        if (mode.PostProcessingMode == 1
            || (mode.PostProcessingMode == 2
                && string.Equals(mode.PostProcessingProvider, "local_llm", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var post = await postProcessor.ProcessAsync(raw, mode, applicationContext, cancellationToken);
                if (post.WasApplied && !string.IsNullOrWhiteSpace(post.Text) && !string.IsNullOrWhiteSpace(post.Provider))
                {
                    processingInput = post.Text.Trim();
                    processingProvider = post.Provider;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Match the batch workflow: enhancement failure preserves the
                // successful raw transcript and must not strand Processing history.
            }
        }

        var output = SpeechOutputProcessor.Process(new SpeechOutputProcessingRequest(
            processingInput,
            outputRequest.Language ?? mode.Language ?? "auto",
            mode.PostProcessingMode switch
            {
                1 => PortablePostProcessingMode.Cloud,
                2 => PortablePostProcessingMode.Local,
                _ => PortablePostProcessingMode.Off,
            },
            outputRequest.VocabularyReplacements ?? [],
            outputRequest.ModeVocabularyReplacements ?? [],
            outputRequest.OutputOptions ?? new SpeechOutputProcessingOptions(
                RemoveTrailingPeriod: mode.RemoveTrailingPeriod,
                Punctuation: mode.Punctuation,
                Capitalization: mode.Capitalization,
                ProfanityFilter: mode.ProfanityFilter),
            outputRequest.CursorContext));

        TextInjectionOutcome injection;
        try
        {
            injection = await TranscriptionTextDelivery.DeliverAsync(
                textInjection, output.InjectionText, outputRequest.PasteResultText, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { injection = TextInjectionOutcome.Failed; }

        transcript.Text = output.TranscriptText;
        transcript.TranscribedText = raw;
        transcript.PostProcessedText = processingProvider is null ? null : output.TranscriptText;
        transcript.PostProcessingProvider = processingProvider;
        transcript.Status = TranscriptStatus.Completed;
        if (!await history.UpdateAsync(transcript, cancellationToken))
            return new(PlatformResult.Failure(
                "streaming.persistence_failed", "The live transcription could not be saved."), injection);
        return new(PlatformResult.Success(), injection);
    }
}

public static class LinuxRecordingAudioRestorer
{
    public static async ValueTask RestoreAsync(
        IMicrophoneVolumeService microphoneVolume,
        IAudioEnvironmentSession? environment,
        IMicrophoneKeepWarmService keepWarm,
        string? deviceId)
    {
        _ = microphoneVolume.Restore();
        if (environment is not null)
        {
            try { await environment.RestoreAsync(CancellationToken.None); } catch { }
            try { await environment.DisposeAsync(); } catch { }
        }
        keepWarm.ResumeAfterRecording(deviceId);
    }
}
