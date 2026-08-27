using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Transcription;

public sealed record TranscriptionWorkflowSnapshot(
    TranscriptionWorkflowState State,
    string Message,
    string? ErrorCode,
    IReadOnlyList<AudioInputDevice> AudioDevices,
    string? SelectedAudioDeviceId,
    TranscriptionBackendCapability Backend)
{
    public bool CanStartRecording => State is not (TranscriptionWorkflowState.Recording
        or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing
        or TranscriptionWorkflowState.Retrying)
        && Backend.IsAvailable && AudioDevices.Count > 0;

    public bool CanTranscribeFile => State is not (TranscriptionWorkflowState.Recording
        or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing
        or TranscriptionWorkflowState.Retrying)
        && Backend.IsAvailable;

    public bool CanStop => State == TranscriptionWorkflowState.Recording;
    public bool CanCancel => State is TranscriptionWorkflowState.Recording
        or TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing
        or TranscriptionWorkflowState.Retrying;
}
