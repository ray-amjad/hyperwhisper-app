using System.Diagnostics;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using HyperWhisper.SpeechOutput;
using HyperWhisper.TranscriptionRouting;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

public interface ILocalApiCapabilityCatalog
{
    IReadOnlyList<ModelEntry> Models { get; }
    IReadOnlyList<ProviderStatus> TranscriptionProviders { get; }
    IReadOnlyList<ProviderStatus> PostProcessingProviders { get; }
    object LocalModels { get; }
}

public interface ILocalApiPostProcessor
{
    ValueTask<PostProcessResult> ProcessAsync(PostProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Production adapter over portable persistence and workflow services. It only
/// reports capabilities supplied by the composed application and never probes
/// credentials, networks, or models on behalf of an API request.
/// </summary>
public sealed partial class ApplicationLocalApiBackend : ILocalApiBackend
{
    /// <summary>
    /// Storage spellings <c>Mode.CloudProvider</c> may hold.
    /// </summary>
    /// <remarks>
    /// <c>microsoftazurespeech</c> and <c>googlespeech</c> are LEGACY entries. No
    /// LOCAL API request can produce them any more: both the <c>engine</c> field
    /// and a written <c>cloudProvider</c> now fold onto <c>hyperwhisper</c> plus
    /// a tier (issue #575). They stay because this set is also
    /// <see cref="ValidateMode"/>'s bound on the MERGED entity, and a mode that
    /// already holds the raw alias must still accept an unrelated
    /// <c>PATCH {"name": …}</c>. Dropping them would make such a mode
    /// un-patchable forever, naming a field the client never sent, which is the
    /// same fault the <c>sortOrder</c> note below warns about.
    ///
    /// THOSE ROWS ARE NOT ONLY HISTORICAL, and this head does not repair them.
    /// <c>ModesViewModel.CloudProviders</c> still offers both ids to the GUI mode
    /// editor and its save path writes the choice verbatim, so a Linux user can
    /// create one today; and unlike Windows
    /// (<c>ModeService.NormalizeLegacyCloudModeValues</c>, plus the EF migration
    /// <c>20260608120000_NormalizeCloudProviderValues</c>) and macOS
    /// (<c>PersistenceController.normalizeCloudProviderIfNeeded</c>), nothing
    /// folds the stored column at startup. Such a mode cannot transcribe at all
    /// here, for the base-URL reason
    /// <see cref="ApplyTranscriptionOverrides"/> records. Both gaps are outside
    /// the Local API and are filed rather than fixed here.
    /// </remarks>
    private static readonly HashSet<string> CloudProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai", "groq", "deepgram", "assemblyai", "elevenlabs", "mistral",
        "soniox", "hyperwhisper", "gemini", "geminitranscribe", "grok",
        "microsoftazurespeech", "googlespeech", "meta",
    };
    private static readonly HashSet<string> WhisperModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en",
        "large-v3-turbo", "large-v2", "large-v3",
    };
    private static readonly HashSet<string> ParakeetModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "parakeet-v2", "parakeet-v3", "qwen3-asr-0.6b", "nemotron-3.5-ml-560ms",
    };
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly ModeRepository _modes;
    private readonly HistoryRepository _history;
    private readonly TranscriptionWorkflow _workflow;
    private readonly ILocalApiCapabilityCatalog _catalog;
    private readonly ILocalApiPostProcessor? _postProcessor;
    private readonly VocabularyRepository? _vocabulary;
    private readonly Func<Mode?, SpeechOutputProcessingOptions>? _outputOptions;
    private readonly IPrivateFileService _privateFiles;
    private readonly string _recordingsDirectory;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _recordingToggle = new(1, 1);
    private TranscriptionWorkflowRequest? _activeRecordingRequest;

    public ApplicationLocalApiBackend(
        ModeRepository modes,
        HistoryRepository history,
        TranscriptionWorkflow workflow,
        ILocalApiCapabilityCatalog catalog,
        IPrivateFileService privateFiles,
        IAppPaths paths,
        string appVersion,
        ILocalApiPostProcessor? postProcessor = null,
        VocabularyRepository? vocabulary = null,
        Func<Mode?, SpeechOutputProcessingOptions>? outputOptions = null)
    {
        _modes = modes ?? throw new ArgumentNullException(nameof(modes));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _privateFiles = privateFiles ?? throw new ArgumentNullException(nameof(privateFiles));
        ArgumentNullException.ThrowIfNull(paths);
        _recordingsDirectory = paths.RecordingsDirectory;
        _appVersion = appVersion;
        _postProcessor = postProcessor;
        _vocabulary = vocabulary;
        _outputOptions = outputOptions;
    }

    public ValueTask<HealthSnapshot> GetHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new HealthSnapshot(_appVersion, _catalog.TranscriptionProviders, _catalog.PostProcessingProviders, _catalog.LocalModels));
    }

    public ValueTask<IReadOnlyList<ModelEntry>> GetModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_catalog.Models);
    }
}
