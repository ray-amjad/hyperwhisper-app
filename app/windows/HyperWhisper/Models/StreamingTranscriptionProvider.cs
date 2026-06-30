namespace HyperWhisper.Models;

/// <summary>
/// Streaming transcription providers supported by the Windows settings surface.
/// Storage values intentionally match macOS AppStorage values for parity.
/// </summary>
public enum StreamingTranscriptionProvider
{
    HyperWhisperCloud,
    Deepgram,
    ElevenLabs,
    OpenAI,
    Xai
}

public static class StreamingTranscriptionProviderExtensions
{
    public static string StorageValue(this StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.HyperWhisperCloud => "hyperwhisperCloud",
        StreamingTranscriptionProvider.Deepgram => "deepgram",
        StreamingTranscriptionProvider.ElevenLabs => "elevenLabs",
        StreamingTranscriptionProvider.OpenAI => "openAI",
        StreamingTranscriptionProvider.Xai => "xai",
        _ => "hyperwhisperCloud"
    };

    public static string DisplayName(this StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.HyperWhisperCloud => "HyperWhisper Cloud",
        StreamingTranscriptionProvider.Deepgram => "Deepgram",
        StreamingTranscriptionProvider.ElevenLabs => "ElevenLabs",
        StreamingTranscriptionProvider.OpenAI => "OpenAI",
        StreamingTranscriptionProvider.Xai => "xAI",
        _ => "HyperWhisper Cloud"
    };

    public static bool RequiresApiKey(this StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.HyperWhisperCloud => false,
        StreamingTranscriptionProvider.Deepgram => true,
        StreamingTranscriptionProvider.ElevenLabs => true,
        StreamingTranscriptionProvider.OpenAI => true,
        StreamingTranscriptionProvider.Xai => true,
        _ => false
    };

    public static bool IsValidStorageValue(string? value) =>
        value is "hyperwhisperCloud" or "deepgram" or "elevenLabs" or "openAI" or "xai";

    public static StreamingTranscriptionProvider FromStorageValue(string? value) => value switch
    {
        "deepgram" => StreamingTranscriptionProvider.Deepgram,
        "elevenLabs" => StreamingTranscriptionProvider.ElevenLabs,
        "openAI" => StreamingTranscriptionProvider.OpenAI,
        "xai" => StreamingTranscriptionProvider.Xai,
        _ => StreamingTranscriptionProvider.HyperWhisperCloud
    };
}
