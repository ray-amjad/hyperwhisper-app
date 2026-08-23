using System.Diagnostics;
using HyperWhisper.Data.Entities;
using HyperWhisper.LocalApi;
using HyperWhisper.ModelManagement;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.Linux;

internal sealed class LinuxLocalApiCapabilityCatalog(
    PortableModelManager models,
    IRecordedAudioTranscriber transcriber) : ILocalApiCapabilityCatalog
{
    public IReadOnlyList<ModelEntry> Models => PortableModelCatalog.All.Select(model => new ModelEntry(
        model.Id, model.Kind == ManagedModelKind.LocalLlm ? "text" : "voice", "local",
        model.DisplayName, models.IsInstalled(model), model.ApproximateSizeBytes / 1_000_000d)).ToArray();

    public IReadOnlyList<ProviderStatus> TranscriptionProviders =>
    [
        new("local", false, transcriber.Capability.IsAvailable,
            transcriber.Capability.IsAvailable ? "ready" : "unavailable"),
    ];

    public IReadOnlyList<ProviderStatus> PostProcessingProviders =>
    [
        new("local_llm", false, PortableModelCatalog.LocalLlm.Any(models.IsInstalled),
            PortableModelCatalog.LocalLlm.Any(models.IsInstalled) ? "ready" : "model_required"),
    ];

    public object LocalModels => new
    {
        whisper = PortableModelCatalog.Whisper.Select(Status).ToArray(),
        parakeet = PortableModelCatalog.Parakeet.Select(Status).ToArray(),
        qwen3_asr = PortableModelCatalog.Parakeet.Where(model => model.Id.StartsWith("qwen3", StringComparison.Ordinal)).Select(Status).ToArray(),
        apple_speech = Array.Empty<object>(),
        local_llm = PortableModelCatalog.LocalLlm.Select(Status).ToArray(),
    };

    private object Status(ManagedModel model) => new { id = model.Id, displayName = model.DisplayName, installed = models.IsInstalled(model) };
}

internal sealed class LinuxLocalApiPostProcessor(
    LinuxLocalPostProcessor processor,
    ModeRepository modes) : ILocalApiPostProcessor
{
    public async ValueTask<PostProcessResult> ProcessAsync(PostProcessRequest request, CancellationToken cancellationToken)
    {
        Mode? mode = null;
        if (Guid.TryParse(request.ModeId, out var id))
            mode = (await modes.ListAsync(cancellationToken)).SingleOrDefault(item => item.Id == id);
        mode ??= new Mode
        {
            Name = "Local API",
            Preset = request.Preset ?? "hyper",
            PostProcessingMode = 2,
            PostProcessingProvider = "local_llm",
            LocalPostProcessingModel = request.Model,
            CustomInstructions = request.Prompt,
        };
        var started = Stopwatch.GetTimestamp();
        var result = await processor.ProcessAsync(request.Text, mode, cancellationToken);
        if (!result.WasApplied || string.IsNullOrWhiteSpace(result.Provider))
            throw new InvalidOperationException("Local post-processing is unavailable for this request.");
        return new(result.Text, result.Provider, mode.LocalPostProcessingModel ?? string.Empty, mode.Preset, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
