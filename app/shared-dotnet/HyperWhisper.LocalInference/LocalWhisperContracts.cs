using Whisper.net.LibraryLoader;

namespace HyperWhisper.LocalInference;

public enum LocalWhisperBackend
{
    Cpu,
    Vulkan,
    Cuda12,
}

/// <summary>
/// How to load a Whisper model. The options after <paramref name="AllowCpuFallback"/>
/// default to the Linux head's behaviour; the Windows Whisper worker sets them.
/// </summary>
/// <param name="RuntimeOrder">
/// The native runtime order to try, set before the first factory of the process.
/// Null derives it from <paramref name="Backend"/> and <paramref name="AllowCpuFallback"/>.
/// Windows passes Vulkan, Cpu, CpuNoAvx.
/// </param>
/// <param name="UseFlashAttention">Null keeps "on for a GPU backend, off for CPU".</param>
/// <param name="VerifyModelOnLoad">
/// Whisper.net reports a model file it cannot read only when the first builder
/// is created. True creates one during the load, so a broken file fails the load
/// as <see cref="LocalWhisperErrorCode.ModelLoadFailed"/> instead of failing the
/// first transcription.
/// </param>
/// <param name="FactoryOnOwnThread">
/// The Windows ARM64 thread rule at load time: the one factory of this load is
/// made (and verified) on a dedicated thread, and every transcription's builder
/// runs on that thread too. The model is then held once, not once for the load
/// and once more for the requests.
/// </param>
public sealed record LocalWhisperLoadOptions(
    string ModelPath,
    LocalWhisperBackend Backend,
    int GpuDevice = 0,
    bool AllowCpuFallback = true,
    IReadOnlyList<RuntimeLibrary>? RuntimeOrder = null,
    bool? UseFlashAttention = null,
    bool VerifyModelOnLoad = false,
    bool FactoryOnOwnThread = false);

/// <summary>
/// One transcription. The options after <paramref name="IncludeWordTimestamps"/>
/// are decoding deltas; they default off, and each head opts in to the ones it uses.
/// </summary>
/// <param name="MaxLastTextTokensZero">
/// Adds <c>WithMaxLastTextTokens(0)</c>: no window of a long recording is primed
/// with the text decoded before it (a repetition-loop guard). Linux (#1559) sets it
/// on every request; Windows (#1560) on every request that carries no prompt.
/// </param>
/// <param name="CreateFactoryOnRequestThread">
/// The Windows ARM64 thread rule: Whisper.net's <c>CreateBuilder()</c> failed on
/// ARM64 when it ran on another thread than the one that created the factory, so
/// the factory, its builder and the processor are made on one dedicated thread. That
/// thread keeps the factory across requests until the model is reloaded or the
/// service is disposed, so a file sent as many chunks reads the model once. A load
/// with <see cref="LocalWhisperLoadOptions.FactoryOnOwnThread"/> already made that
/// thread; without it, the first such request makes it and drops the load's
/// factory, so the model is never held twice.
/// </param>
/// <param name="Prompt">
/// Adds <c>WithPrompt(...)</c>: the Whisper initial prompt, null or empty for none.
/// It rides the same carried-text channel as past windows' text, so a request that
/// sets it must not also set <paramref name="MaxLastTextTokensZero"/>, which would
/// drop it. Windows sets it for recordings up to 30 s with vocabulary (#1564);
/// Linux never sets it.
/// </param>
public sealed record LocalWhisperRequest(
    string AudioPath,
    string? Language = null,
    int? ThreadCount = null,
    bool SingleSegment = true,
    bool IncludeWordTimestamps = false,
    bool MaxLastTextTokensZero = false,
    bool CreateFactoryOnRequestThread = false,
    string? Prompt = null);

public sealed record LocalWhisperWordTimestamp(
    string Word,
    double Start,
    double End,
    double? Probability = null);

public sealed record LocalWhisperSegmentTimestamp(
    int Id,
    double Start,
    double End,
    string Text);

public sealed record LocalWhisperTimestamps(
    IReadOnlyList<LocalWhisperSegmentTimestamp> Segments,
    IReadOnlyList<LocalWhisperWordTimestamp> Words,
    string RawText);

public enum LocalWhisperErrorCode
{
    InvalidRequest,
    RuntimeUnavailable,
    ModelLoadFailed,
    TranscriptionFailed,
    Cancelled,
}

public sealed record LocalWhisperFailure(LocalWhisperErrorCode Code, string Message);

public sealed record LocalWhisperResult(
    string? Text,
    LocalWhisperFailure? Failure,
    string? Runtime,
    LocalWhisperTimestamps? Timestamps = null)
{
    public bool IsSuccess => Text is not null && Failure is null;

    /// <summary>
    /// The concatenated segment text before the non-speech markers were stripped
    /// and before it was trimmed. A caller that transcribes one file in chunks
    /// joins these and strips once, as a single run would.
    /// </summary>
    public string? UnstrippedText { get; init; }

    /// <summary>The language of the first segment that reported one.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>The number of segments Whisper produced.</summary>
    public int SegmentCount { get; init; }

    public static LocalWhisperResult Success(
        string text,
        string runtime,
        LocalWhisperTimestamps? timestamps = null) =>
        new(text, null, runtime, timestamps);

    public static LocalWhisperResult Failed(LocalWhisperErrorCode code, string message, string? runtime = null) =>
        new(null, new LocalWhisperFailure(code, message), runtime);
}

public interface ILocalWhisperService : IAsyncDisposable
{
    bool IsLoaded { get; }
    string? Runtime { get; }
    Task<LocalWhisperResult> LoadAsync(LocalWhisperLoadOptions options, CancellationToken cancellationToken = default);
    Task<LocalWhisperResult> TranscribeAsync(LocalWhisperRequest request, CancellationToken cancellationToken = default);
}
