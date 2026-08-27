namespace HyperWhisper.PortableApplication.Transcription;

public sealed record BatchAudioPreprocessResult(string TranscriptionPath, string? TrimmedAudioPath, string Reason);

public interface IBatchAudioPreprocessor
{
    Task<BatchAudioPreprocessResult> PreprocessAsync(string path, CancellationToken cancellationToken = default);
}
