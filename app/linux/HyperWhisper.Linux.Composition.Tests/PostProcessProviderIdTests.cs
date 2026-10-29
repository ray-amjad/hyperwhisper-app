using System.Net;
using System.Text;
using System.Text.Json;
using HyperWhisper.CloudPostProcessing;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux;
using HyperWhisper.LocalApi;
using HyperWhisper.LocalPostProcessing;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using Microsoft.Data.Sqlite;

// Issue #1158. The Linux head stores and emits the provider ID a run used (`openai`, `local_llm`,
// `custom:<uuid>`), as macOS and Windows do, and keeps the human label for display only. These
// drive the real router, the real cloud service (a stub HTTP handler) and the real local
// post-processor (a fake LLM engine) through the real `/post-process` adapter and live finalizer,
// then check the History badge's ID-to-label converter.
static class PostProcessProviderIdTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-pp-provider-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "LLM"));
        try
        {
            var database = new ApplicationDb(new StaticPaths(root));
            await database.MigrateAsync();
            var settings = new PortableSettingsService(new MissingPrivateFiles(), Path.Combine(root, "settings.json"));
            File.WriteAllText(Path.Combine(root, "LLM", "tiny.gguf"), "fake");
            var handler = new OpenAiStub();
            var engine = new FakeLlmEngine();
            using var router = new LinuxPostProcessingRouter(
                new LinuxLocalPostProcessor(root, settings, database, engine),
                new CloudPostProcessingService(new FixedApiKey(), new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) }),
                settings,
                database);
            var adapter = new LinuxLocalApiPostProcessor(router, new ModeRepository(database));

            // (a) an applied OpenAI run answers `provider: "openai"`, not "OpenAI · gpt-…".
            var openAi = await adapter.ProcessAsync(
                new PostProcessRequest("raw words", null, null, "Fix it", "openai", "gpt-5.6-luna"),
                CancellationToken.None);
            Assert(handler.Calls == 1, "the OpenAI run never reached the stub handler");
            Assert(openAi.PostProcessed && openAi.Text == "Hello Ray", $"the OpenAI run was not applied: {openAi.Text}");
            Assert(openAi.Provider == "openai", $"/post-process provider is '{openAi.Provider}', not 'openai'");
            Assert(!openAi.Provider.Contains(" · ", StringComparison.Ordinal), "/post-process provider is a display label");

            // (b) a local LLM run: the processor's result names `local_llm`, and so does /post-process.
            var local = await router.ProcessAsync(
                "raw words", new Mode { Name = "Local", PostProcessingMode = 2, PostProcessingProvider = "local_llm", LocalPostProcessingModel = "tiny.gguf" }, null);
            Assert(engine.Calls == 1 && local.WasApplied, "the local LLM run was not applied");
            Assert(local.ProviderId == "local_llm", $"the local LLM run's ProviderId is '{local.ProviderId}'");
            Assert(local.Provider?.StartsWith("Local LLM · tiny.gguf", StringComparison.Ordinal) == true,
                $"the local LLM display label changed: {local.Provider}");
            var localApi = await adapter.ProcessAsync(
                new PostProcessRequest("raw words", null, null, "Fix it", "local_llm", "tiny.gguf"),
                CancellationToken.None);
            Assert(localApi.Provider == "local_llm", $"/post-process provider for a local run is '{localApi.Provider}'");

            // The transcript row stores the ID too (what /recordings `postProcessingProvider` reads).
            var transcript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
            var history = new FakeHistory(transcript);
            var mode = new Mode { Name = "Cloud", PostProcessingMode = 1, PostProcessingProvider = "openai", LanguageModel = "gpt-5.6-luna" };
            await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
                "raw words", transcript, mode, null, router, new FakeTextInjection(), history,
                new TranscriptionWorkflowRequest(SelectedMode: mode));
            Assert(history.Updated?.PostProcessingProvider == "openai",
                $"the row stores '{history.Updated?.PostProcessingProvider}', not 'openai'");
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // (c) the History badge maps an ID to a label and leaves an old label alone.
        var custom = $"custom:{Guid.NewGuid():D}";
        foreach (var (stored, expected) in new (string?, string)[]
        {
            ("openai", "OpenAI"),
            ("anthropic", "Anthropic"),
            ("grok", "Grok"),
            ("local_llm", "Local LLM"),
            ("hyperwhispercloud", "HyperWhisper Cloud"),
            ("hyperwhisper", "HyperWhisper Cloud"),
            (custom, "Custom endpoint"),
            ("OpenAI · gpt-5.6-luna", "OpenAI · gpt-5.6-luna"),
            ("Local LLM · tiny.gguf · CPU", "Local LLM · tiny.gguf · CPU"),
            ("some-future-provider", "some-future-provider"),
            (null, ""),
            ("", ""),
        })
        {
            var shown = PostProcessingProviderLabelConverter.Instance.Convert(
                stored, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) as string;
            Assert(shown == expected, $"the badge shows '{shown}' for '{stored}', expected '{expected}'");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedApiKey : IPostProcessingCredentialSource
    {
        public ValueTask<PostProcessingCredential?> GetCredentialAsync(
            CloudPostProcessingProvider provider, Guid? customEndpointId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PostProcessingCredential?>(new(ApiKey: "unused"));
    }

    private sealed class OpenAiStub : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "<<CLEANED>>Hello Ray<<END>>" }, finish_reason = "stop" } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeLlmEngine : ILocalLlmEngine
    {
        public int Calls { get; private set; }

        public ValueTask<LocalLlmGenerationResult> GenerateAsync(
            LocalLlmGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(LocalLlmGenerationResult.Success("<<CLEANED>>Hello Ray<<END>>", "CPU"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
