namespace HyperWhisper.PortableApplication.Transcription;

public enum TranscriptionWorkflowState
{
    Idle,
    Recording,
    Stopping,
    Transcribing,
    Retrying,
    Completed,
    Cancelled,
    Failed,
}
