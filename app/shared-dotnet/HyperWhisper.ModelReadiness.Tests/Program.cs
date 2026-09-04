using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using HyperWhisper.ModelManagement;
using HyperWhisper.ModelReadiness;

var tests = new (string Name, Func<Task> Run)[]
{
    ("local catalog maps every managed model", TestLocalCatalogAsync),
    ("cloud STT maps every provider model", TestCloudSttCoverageAsync),
    ("cloud STT rows never over-claim a sibling model's languages", TestCloudSttPerModelLanguagesAsync),
    ("every stated languageCount matches the model it describes", TestCloudSttLanguageCountAsync),
    ("a provider whose models differ states languageCount or is a named exception", TestCloudSttLanguageCountCoverageAsync),
    ("streaming catalog maps every supported provider", TestStreamingCoverageAsync),
    ("post-processing maps shared model catalogs", TestPostProcessingCoverageAsync),
    ("custom endpoints are isolated rows", TestCustomEndpointsAsync),
    ("missing credential never invokes probe", TestMissingCredentialAsync),
    ("health outcomes and checking state map exactly", TestHealthStatesAsync),
    ("timeout and transport failure are bounded", TestFailuresAsync),
    ("health diagnostics redact and bound secrets", TestRedactionAsync),
    ("health request cannot carry user content", TestRequestSurfaceAsync),
    ("credential lookup is provider scoped", TestCredentialScopeAsync),
    ("credential change hook is scoped", TestCredentialChangeAsync),
    ("provider metadata probes use fixed content-free requests", TestMetadataProbeRequestsAsync),
    ("provider metadata outcomes are bounded and explicit", TestMetadataProbeOutcomesAsync),
    ("provider metadata cancellation propagates", TestMetadataProbeCancellationAsync),
    ("Meta readiness reports configured without a probe", TestMetaReadinessAsync),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"Model readiness: {tests.Length}/{tests.Length} tests passed.");

static IReadOnlyList<ModelCapability> Load(CustomEndpointDefinition[]? custom = null) =>
    UnifiedModelCatalog.LoadBundled(custom);

static Task TestLocalCatalogAsync()
{
    var rows = Load().Where(x => x.Deployment == ModelDeployment.Local).ToArray();
    Equal(PortableModelCatalog.All.Count + PortableModelCatalog.All.Count(x => x.SupportsStreaming), rows.Length);
    foreach (var model in PortableModelCatalog.All)
    {
        var modelRows = rows.Where(x => x.ModelId == model.Id).ToArray();
        Equal(model.SupportsStreaming ? 2 : 1, modelRows.Length);
        var row = modelRows.Single(x => x.Surface != ModelSurface.StreamingTranscription);
        Equal(model.ApproximateSizeBytes, row.ApproximateSizeBytes);
        Equal(model.RecommendedVramBytes, row.RecommendedVramBytes);
        True(row.Runtime is "whisper.cpp" or "sherpa-onnx" or "llama.cpp");
        Equal(model.IsEnglishOnly, row.IsEnglishOnly);
        True(!row.RequiresCredential);
    }
    var localLive = rows.Where(x => x.Surface == ModelSurface.StreamingTranscription).ToArray();
    Equal(3, localLive.Length);
    True(localLive.Count(x => x.ProviderId == "parakeetLocal") == 2);
    var nemotron = localLive.Single(x => x.ProviderId == "nemotronLocal");
    Equal(32, nemotron.SupportedLanguages.Count);
    True(nemotron.SupportedLanguages.Contains("en-US") && nemotron.SupportedLanguages.Contains("zh-CN"));
    return Task.CompletedTask;
}

static Task TestCloudSttCoverageAsync()
{
    using var json = OpenCatalog("cloud-stt-catalog.json");
    using var doc = JsonDocument.Parse(json);
    var expected = doc.RootElement.GetProperty("providers").EnumerateArray()
        .Sum(provider => provider.GetProperty("models").GetArrayLength());
    var rows = Load().Where(x => x.Surface == ModelSurface.BatchTranscription
        && x.Deployment == ModelDeployment.Cloud).ToArray();
    Equal(expected, rows.Length);
    foreach (var row in rows)
    {
        True(row.Workload == ModelWorkload.Voice && row.CredentialAccount is not null);
        True(row.CloudTierEligible || row.ByokEligible);
    }
    return Task.CompletedTask;
}

/// <summary>
/// `cloud-stt-catalog.json`'s `languages.codes` is PROVIDER-level. For Azure MAI
/// it is the UNION of the two models' sets, so publishing its length as every
/// model's language count — which is what this builder used to do — told a
/// MAI-Transcribe 1.5 user the model speaks 18 languages it cannot transcribe.
/// The per-model split lives in `shared-models/models-catalog.json` and reaches
/// a row as `ModelLanguageCount`.
///
/// This case pins BOTH halves of that contract, because the first fix for it
/// broke the second one: `SupportedLanguages` must stay in the CATALOG's code
/// space for every cloud row, and the per-model figure must travel as a count
/// beside it rather than as a second, differently-spelled code list inside it.
/// </summary>
static Task TestCloudSttPerModelLanguagesAsync()
{
    var rows = Load().Where(x => x.Surface == ModelSurface.BatchTranscription
        && x.Deployment == ModelDeployment.Cloud).ToArray();

    var v15 = rows.Single(x => x.ModelId == "mai-transcribe-1.5");
    var v2 = rows.Single(x => x.ModelId == "mai-transcribe-2");

    // ONE CODE SPACE. Both Azure rows carry the catalog's own upstream codes, so
    // `SupportedLanguages` answers the same question for every cloud vendor.
    // `nb`/`fil` are the codes that prove it: the catalog writes those, the
    // picker space writes `no`/`tl`, and an earlier cut of this fix silently
    // swapped the two rows into the picker space.
    foreach (var row in new[] { v15, v2 })
    {
        True(row.SupportedLanguages.Contains("nb"),
            "an Azure row lost the catalog's `nb` - SupportedLanguages is in the picker code space again");
        True(!row.SupportedLanguages.Contains("no"),
            "an Azure row gained the picker's `no` - SupportedLanguages is in the picker code space again");
        True(row.SupportedLanguages.Contains("fil") && !row.SupportedLanguages.Contains("tl"),
            "an Azure row swapped `fil` for the picker's `tl`");
    }
    True(!v15.SupportsAllLanguages && !v2.SupportsAllLanguages);

    // Every cloud row, every vendor: the list is the provider's, unnarrowed.
    foreach (var group in rows.GroupBy(x => x.ProviderId, StringComparer.Ordinal))
    {
        foreach (var row in group)
        {
            Equal(group.First().SupportedLanguages.Count, row.SupportedLanguages.Count);
        }
    }

    // PER-MODEL COUNT. The split travels here instead, and only where the models
    // really differ: `null` means "the provider list is this model's list", so a
    // vendor with one table adds no noise.
    Equal(41, v15.ModelLanguageCount);
    Equal(59, v2.ModelLanguageCount);
    foreach (var row in rows.Where(x => x.ProviderId != "azure-mai"))
    {
        True(row.ModelLanguageCount is null,
            $"{row.Key} states a per-model language count but its provider is not in PerModelLanguageProviders");
    }
    return Task.CompletedTask;
}

/// <summary>
/// `models[].languageCount` in `cloud-stt-catalog.json` is the vendor's published
/// figure for ONE model, and /choosing-a-model publishes it. Nothing in the .NET
/// or Rust decoders reads it, so before this case its only consumer was a nextjs
/// suite that no workflow runs — the field could drift to any value and every
/// gate in the repo would stay green.
///
/// This is that gate. It runs in `linux-ci.yml`, whose path filter includes
/// `shared-app-classification/**`, so it executes on exactly the edits that can
/// break it. A sibling case in `hw-catalog` enforces the all-or-none rule on the
/// catalog side; this one is the cross-file half, which only a caller holding
/// BOTH catalogs can check.
/// </summary>
static Task TestCloudSttLanguageCountAsync()
{
    using var json = OpenCatalog("cloud-stt-catalog.json");
    using var doc = JsonDocument.Parse(json);
    var rows = Load().Where(x => x.Surface == ModelSurface.BatchTranscription
        && x.Deployment == ModelDeployment.Cloud).ToArray();
    var stated = 0;

    foreach (var provider in doc.RootElement.GetProperty("providers").EnumerateArray())
    {
        var providerId = provider.GetProperty("id").GetString()!;
        var languages = provider.TryGetProperty("languages", out var block) ? block : default;
        var providerCount = languages.ValueKind == JsonValueKind.Object
            && languages.TryGetProperty("count", out var countValue)
            && countValue.ValueKind == JsonValueKind.Number
                ? countValue.GetInt32()
                : (int?)null;

        var models = provider.GetProperty("models").EnumerateArray().ToArray();
        var declared = models
            .Select(model => model.TryGetProperty("languageCount", out var value)
                && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : (int?)null)
            .ToArray();

        if (declared.All(count => count is null)) continue;

        // ALL OR NONE. A provider that states the figure on some models leaves
        // the others silently inheriting the union, so the split is only half
        // declared and the page publishes the union for the narrow model.
        True(declared.All(count => count is not null),
            $"{providerId} states languageCount on some models but not all");

        // The union is the widest model's table, so the largest per-model figure
        // must be the provider's own. Otherwise `languages.codes` holds codes no
        // model supports, or a model claims more than the union it came from.
        True(providerCount is not null, $"{providerId} states languageCount but no provider count");
        Equal(providerCount, declared.Max());

        for (var index = 0; index < models.Length; index++)
        {
            var modelId = models[index].GetProperty("id").GetString()!;
            var row = rows.SingleOrDefault(x => x.ModelId == modelId)
                ?? throw new InvalidDataException($"no capability row for {providerId}/{modelId}");
            stated++;

            // The vendor figure is an UPPER BOUND on what the app can offer: the
            // picker can only show a language `hw-catalog`'s language table
            // knows, so Azure's 60 becomes 59 (no Odia row) and 42 becomes 41.
            // The app must never claim MORE than the vendor publishes.
            var appCount = row.ModelLanguageCount ?? row.SupportedLanguages.Count;
            True(appCount <= declared[index],
                $"{providerId}/{modelId} offers {appCount} languages but the vendor publishes {declared[index]}");
            True(appCount >= declared[index] - 2,
                $"{providerId}/{modelId} offers {appCount} of the vendor's {declared[index]} languages — more than " +
                "two dropped means the per-model list and the vendor count have drifted apart, not that the app " +
                "lacks a language row");
        }
    }

    True(stated > 0, "no model states languageCount - this gate is asserting nothing");
    return Task.CompletedTask;
}

/// <summary>
/// The other direction, and the one the guard test in the nextjs mirror is blind
/// to because it `continue`s when a provider declares nothing: a provider whose
/// models demonstrably do NOT share a language table must either state each
/// model's `languageCount` or be a NAMED exception here.
///
/// Without this, the convention in `shared-app-classification/AGENTS.md` was
/// false on the entry directly below the one that introduced it —
/// `nova-3-medical` is English-only and `nova-3-general` is not — and nothing
/// could see it. Two providers are grandfathered below with the reason; every
/// future one trips this case.
/// </summary>
static Task TestCloudSttLanguageCountCoverageAsync()
{
    // PRE-EXISTING over-claims, not exemptions on the merits. Both publish one
    // vendor figure for a family whose models differ, and neither vendor
    // publishes a per-model count we could source — so /choosing-a-model shows
    // 64 for Deepgram's English-only medical rows and 98 for AssemblyAI's
    // 18-language Universal-3.5 Pro. Fixing that means sourcing six vendor
    // numbers, which is a content change, not a schema one. Named here so the
    // rule can be stated truthfully rather than stated falsely.
    string[] grandfathered = ["deepgramNova3", "assemblyAI"];

    using var sttJson = OpenCatalog("cloud-stt-catalog.json");
    using var sttDoc = JsonDocument.Parse(sttJson);
    using var modelsJson = OpenCatalog("models-catalog.json");
    using var modelsDoc = JsonDocument.Parse(modelsJson);

    // Language "signature" per model id: English-only, or the sorted code list,
    // or null when the file states no explicit support for that model.
    var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var model in modelsDoc.RootElement.GetProperty("models").EnumerateArray())
    {
        if (!model.TryGetProperty("id", out var idValue)) continue;
        var id = idValue.GetString();
        if (string.IsNullOrEmpty(id)) continue;
        if (model.TryGetProperty("isEnglishOnly", out var english) && english.ValueKind == JsonValueKind.True)
        {
            signatures[id] = "en-only";
            continue;
        }
        if (!model.TryGetProperty("supportedLanguages", out var codes)
            || codes.ValueKind != JsonValueKind.Array || codes.GetArrayLength() == 0) continue;
        signatures[id] = string.Join(",", codes.EnumerateArray()
            .Select(code => code.GetString()).OrderBy(code => code, StringComparer.Ordinal));
    }

    var checkedProviders = 0;
    foreach (var provider in sttDoc.RootElement.GetProperty("providers").EnumerateArray())
    {
        var providerId = provider.GetProperty("id").GetString()!;
        var models = provider.GetProperty("models").EnumerateArray().ToArray();
        if (models.Length < 2) continue;

        var known = models
            .Select(model => model.GetProperty("id").GetString()!)
            .Where(signatures.ContainsKey)
            .Select(id => signatures[id])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // Fewer than two KNOWN signatures proves nothing either way: the file may
        // simply not carry per-model lists for this vendor.
        if (known.Length < 2) continue;

        checkedProviders++;
        if (grandfathered.Contains(providerId, StringComparer.Ordinal)) continue;

        var declares = models.All(model => model.TryGetProperty("languageCount", out var value)
            && value.ValueKind == JsonValueKind.Number);
        True(declares,
            $"{providerId}'s models do not share a language table (models-catalog.json gives them " +
            $"{known.Length} different sets) but its rows state no languageCount, so /choosing-a-model " +
            "publishes the provider figure for every one of them. State each model's own count, or add " +
            "the provider to the grandfathered list in this test with the reason.");
    }

    True(checkedProviders > 0, "no provider had comparable per-model language sets - this gate is asserting nothing");
    return Task.CompletedTask;
}

static Task TestStreamingCoverageAsync()
{
    using var json = OpenCatalog("cloud-stt-catalog.json");
    using var doc = JsonDocument.Parse(json);
    var catalogProviders = doc.RootElement.GetProperty("providers").EnumerateArray()
        .Where(provider => provider.GetProperty("features").GetProperty("streaming").GetBoolean())
        .Select(provider => provider.GetProperty("sttProvider").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var rows = Load().Where(x => x.Surface == ModelSurface.StreamingTranscription).ToArray();
    foreach (var provider in catalogProviders.Where(provider =>
        !provider.Equals("meta", StringComparison.OrdinalIgnoreCase)))
        True(rows.Any(x => x.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase)));
    foreach (var provider in new[] { "deepgram", "elevenlabs", "openai", "grok", "hyperwhisper" })
        True(rows.Any(x => x.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase)));
    True(rows.All(x => !x.ProviderId.Equals("meta", StringComparison.OrdinalIgnoreCase)));
    True(rows.All(x => x.SupportsStreaming && x.Workload == ModelWorkload.Voice));
    return Task.CompletedTask;
}

static Task TestPostProcessingCoverageAsync()
{
    var rows = Load().Where(x => x.Surface == ModelSurface.PostProcessing && x.Deployment == ModelDeployment.Cloud).ToArray();
    using var modelsJson = OpenCatalog("models-catalog.json");
    using var models = JsonDocument.Parse(modelsJson);
    var expectedByok = models.RootElement.GetProperty("models").EnumerateArray()
        .Count(x => x.GetProperty("kind").GetString() == "text" && x.GetProperty("provider").GetString() != "localLLM");
    using var ppJson = OpenCatalog("cloud-pp-catalog.json");
    using var pp = JsonDocument.Parse(ppJson);
    var expectedTier = pp.RootElement.GetProperty("providers").EnumerateArray()
        .Where(p => !p.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean())
        .Sum(p => p.GetProperty("models").EnumerateArray().Count(m => !m.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()));
    Equal(expectedByok, rows.Count(x => x.Key.StartsWith("cloud/pp-byok/", StringComparison.Ordinal)));
    Equal(expectedTier, rows.Count(x => x.Key.StartsWith("cloud/pp-tier/", StringComparison.Ordinal)));
    True(rows.All(x => x.Workload == ModelWorkload.Text));
    return Task.CompletedTask;
}

static Task TestCustomEndpointsAsync()
{
    var id = Guid.NewGuid();
    var rows = Load([new(id, "Loopback", new Uri("http://127.0.0.1:11434/v1/chat/completions"), "model", $"CustomEndpoint_{id:D}")]);
    var row = rows.Single(x => x.Surface == ModelSurface.CustomEndpoint);
    Equal(id.ToString("D"), row.Key["custom/".Length..]);
    Equal("CustomEndpoint_" + id.ToString("D"), row.CredentialAccount);
    Throws<InvalidDataException>(() => Load([new(Guid.NewGuid(), "Bad", new Uri("file:///tmp/model"), "m", "account")]));
    var noAuth = Load([new(Guid.NewGuid(), "No auth", new Uri("http://localhost:11434/v1/models"), "m", "", false)])
        .Single(x => x.Surface == ModelSurface.CustomEndpoint);
    True(!noAuth.RequiresCredential);
    return Task.CompletedTask;
}

static async Task TestMissingCredentialAsync()
{
    var probe = new FakeProbe();
    var service = Service(new FakeCredentials(), probe);
    var result = await service.CheckAsync(CloudRow());
    Equal(ReadinessState.MissingCredential, result.State);
    Equal(0, probe.Requests.Count);

    var endpoint = Load([new(Guid.NewGuid(), "No auth", new Uri("http://localhost:11434/v1/models"), "m", "", false)])
        .Single(x => x.Surface == ModelSurface.CustomEndpoint);
    result = await service.CheckAsync(endpoint);
    Equal(ReadinessState.Healthy, result.State);
    Equal(1, probe.Requests.Count);
    True(!probe.Requests[0].Credential.IsPresent);
}

static async Task TestMetaReadinessAsync()
{
    var row = Load().Single(x => x.ProviderId.Equals("meta", StringComparison.OrdinalIgnoreCase)
        && x.ModelId == "muse-voice-transcribe-1.0"
        && x.Surface == ModelSurface.BatchTranscription);
    Equal("MetaApiKey", row.CredentialAccount);
    True(row.ByokEligible && row.CloudTierEligible);
    var probe = new FakeProbe();
    var missing = await Service(new FakeCredentials(), probe).CheckAsync(row);
    Equal(ReadinessState.MissingCredential, missing.State);
    var configured = await Service(new FakeCredentials(("MetaApiKey", "meta-secret")), probe).CheckAsync(row);
    Equal(ReadinessState.Healthy, configured.State);
    Equal("Key saved; validated on first transcription.", configured.Detail);
    Equal(0, probe.Requests.Count);
}

static async Task TestHealthStatesAsync()
{
    foreach (var item in new[]
    {
        (ProviderHealthOutcome.Healthy, ReadinessState.Healthy),
        (ProviderHealthOutcome.Unauthorized, ReadinessState.Unauthorized),
        (ProviderHealthOutcome.RateLimited, ReadinessState.RateLimited),
        (ProviderHealthOutcome.Unreachable, ReadinessState.Unreachable),
        (ProviderHealthOutcome.Malformed, ReadinessState.Malformed),
        (ProviderHealthOutcome.Unsupported, ReadinessState.Unsupported),
    })
    {
        var probe = new FakeProbe { Response = new(item.Item1) };
        var service = Service(new FakeCredentials(("OpenAIApiKey", "secret")), probe);
        var states = new List<ReadinessState>();
        service.ReadinessChanged += (_, args) => states.Add(args.Readiness.State);
        var result = await service.CheckAsync(CloudRow());
        Equal(item.Item2, result.State);
        Equal(ReadinessState.Checking, states[^2]);
        Equal(item.Item2, states[^1]);
    }
    var localService = Service(new FakeCredentials(), new FakeProbe(), new FakeLocal(true));
    Equal(ReadinessState.Installed, (await localService.CheckAsync(Load().First(x => x.Deployment == ModelDeployment.Local))).State);
}

static async Task TestFailuresAsync()
{
    var credentials = new FakeCredentials(("OpenAIApiKey", "secret"));
    var slow = new FakeProbe { WaitForCancellation = true };
    var service = Service(credentials, slow, timeout: TimeSpan.FromMilliseconds(20));
    Equal(ReadinessState.Unreachable, (await service.CheckAsync(CloudRow())).State);
    var broken = new FakeProbe { Error = new HttpRequestException("host secret details") };
    var failed = await Service(credentials, broken).CheckAsync(CloudRow());
    Equal(ReadinessState.Unreachable, failed.State);
    Equal("Provider could not be reached.", failed.Detail);
}

static async Task TestRedactionAsync()
{
    const string secret = "top-secret-value";
    var probe = new FakeProbe { Response = new(ProviderHealthOutcome.Unauthorized, $"Rejected {secret}") };
    var result = await Service(new FakeCredentials(("OpenAIApiKey", secret)), probe).CheckAsync(CloudRow());
    True(result.Detail == "Rejected [redacted]" && !result.Detail.Contains(secret, StringComparison.Ordinal));
    probe.Response = new(ProviderHealthOutcome.Unreachable, new string('x', ProviderHealthResponse.MaximumDetailBytes + 1));
    result = await Service(new FakeCredentials(("OpenAIApiKey", secret)), probe).CheckAsync(CloudRow());
    Equal("Provider returned an oversized health response.", result.Detail);
    Equal("[redacted]", new ProviderCredential(secret).ToString());
}

static Task TestRequestSurfaceAsync()
{
    var names = typeof(ProviderHealthRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var forbidden in new[] { "Audio", "Transcript", "Text", "Prompt", "Vocabulary", "Credentials", "SystemInfo" })
        True(!names.Contains(forbidden));
    Equal(5, names.Count);
    return Task.CompletedTask;
}

static async Task TestCredentialScopeAsync()
{
    var credentials = new FakeCredentials(("OpenAIApiKey", "only-this-secret"), ("AnthropicApiKey", "never-read"));
    var probe = new FakeProbe();
    await Service(credentials, probe).CheckAsync(CloudRow());
    Equal(1, credentials.Requested.Count);
    Equal("OpenAIApiKey", credentials.Requested.Single());
    Equal("only-this-secret", probe.Requests.Single().Credential.Value);
}

static Task TestCredentialChangeAsync()
{
    var service = Service(new FakeCredentials(), new FakeProbe());
    string? changed = null;
    service.CredentialInvalidated += (_, account) => changed = account;
    service.NotifyCredentialChanged("GroqApiKey");
    Equal("GroqApiKey", changed);
    Throws<ArgumentException>(() => service.NotifyCredentialChanged(" "));
    return Task.CompletedTask;
}

static async Task TestMetadataProbeRequestsAsync()
{
    const string secret = "provider-secret";
    var expected = new Dictionary<string, (string Uri, string Header)>(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = ("https://api.openai.com/v1/models", "Authorization"),
        ["groq"] = ("https://api.groq.com/openai/v1/models", "Authorization"),
        ["grok"] = ("https://api.x.ai/v1/models", "Authorization"),
        ["mistral"] = ("https://api.mistral.ai/v1/models", "Authorization"),
        ["cerebras"] = ("https://api.cerebras.ai/v1/models", "Authorization"),
        ["anthropic"] = ("https://api.anthropic.com/v1/models?limit=1", "x-api-key"),
        ["gemini"] = ("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1", "x-goog-api-key"),
        ["deepgram"] = ("https://api.deepgram.com/v1/projects?limit=1", "Authorization"),
        ["elevenlabs"] = ("https://api.elevenlabs.io/v1/models", "xi-api-key"),
    };
    foreach (var (provider, requestExpectation) in expected)
    {
        var transport = new FakeMetadataTransport(_ => JsonResponse("{}"));
        var response = await new ProviderMetadataHealthProbe(transport).CheckAsync(
            new(provider, "ignored-model", ModelSurface.PostProcessing, new(secret),
                new Uri("https://attacker.example.test/inference")));
        Equal(ProviderHealthOutcome.Healthy, response.Outcome);
        var sent = transport.Requests.Single();
        Equal(HttpMethod.Get, sent.Method);
        Equal(requestExpectation.Uri, sent.RequestUri!.AbsoluteUri);
        True(sent.Content is null && !sent.RequestUri.AbsoluteUri.Contains(secret, StringComparison.Ordinal));
        True(sent.Headers.Contains(requestExpectation.Header));
        True(!sent.Headers.SelectMany(header => header.Value).Any(value =>
            value.Contains("ignored-model", StringComparison.Ordinal)
            || value.Contains("attacker", StringComparison.Ordinal)));
    }

    var unsupportedTransport = new FakeMetadataTransport(_ => throw new InvalidOperationException("must not send"));
    foreach (var provider in new[] { "hyperwhisper", "assemblyai", "soniox", "azure-mai", "google-chirp", "custom" })
    {
        var unsupported = await new ProviderMetadataHealthProbe(unsupportedTransport).CheckAsync(
            new(provider, "model", ModelSurface.BatchTranscription, new(secret)));
        Equal(ProviderHealthOutcome.Unsupported, unsupported.Outcome);
    }
    Equal(0, unsupportedTransport.Requests.Count);
}

static async Task TestMetadataProbeOutcomesAsync()
{
    foreach (var item in new[]
    {
        (HttpStatusCode.OK, "{}", ProviderHealthOutcome.Healthy),
        (HttpStatusCode.NoContent, "", ProviderHealthOutcome.Healthy),
        (HttpStatusCode.Unauthorized, "{}", ProviderHealthOutcome.Unauthorized),
        (HttpStatusCode.Forbidden, "{}", ProviderHealthOutcome.Unauthorized),
        ((HttpStatusCode)429, "{}", ProviderHealthOutcome.RateLimited),
        (HttpStatusCode.BadGateway, "{}", ProviderHealthOutcome.Unreachable),
        (HttpStatusCode.OK, "not-json", ProviderHealthOutcome.Malformed),
    })
    {
        var transport = new FakeMetadataTransport(_ => JsonResponse(item.Item2, item.Item1));
        var result = await new ProviderMetadataHealthProbe(transport).CheckAsync(
            new("openai", "model", ModelSurface.BatchTranscription, new("secret")));
        Equal(item.Item3, result.Outcome);
        True(result.Detail is null || !result.Detail.Contains("secret", StringComparison.Ordinal));
    }

    var oversized = new FakeMetadataTransport(_ => JsonResponse(new string('x', ProviderHealthResponse.MaximumDetailBytes + 1)));
    Equal(ProviderHealthOutcome.Healthy,
        (await new ProviderMetadataHealthProbe(oversized).CheckAsync(
            new("openai", "model", ModelSurface.BatchTranscription, new("secret")))).Outcome);
}

static async Task TestMetadataProbeCancellationAsync()
{
    var transport = new FakeMetadataTransport(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return JsonResponse("{}");
    });
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => new ProviderMetadataHealthProbe(transport).CheckAsync(
        new("openai", "model", ModelSurface.BatchTranscription, new("secret")), cancellation.Token).AsTask());
}

static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

static ModelCapability CloudRow() => Load().First(x => x.Key.StartsWith("cloud/stt/", StringComparison.Ordinal)
    && x.CredentialAccount == "OpenAIApiKey");

static ModelReadinessService Service(FakeCredentials credentials, FakeProbe probe,
    FakeLocal? local = null, TimeSpan? timeout = null) =>
    new(credentials, probe, local ?? new FakeLocal(false), timeout);

static FileStream OpenCatalog(string name) => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Catalogs", name));

static void True(bool condition, string? message = null)
{
    if (!condition) throw new InvalidOperationException(message ?? "Assertion failed.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeCredentials(params (string Account, string Value)[] values) : IProviderCredentialSource
{
    private readonly Dictionary<string, string> _values = values.ToDictionary(x => x.Account, x => x.Value, StringComparer.Ordinal);
    public List<string> Requested { get; } = [];
    public ValueTask<ProviderCredential?> GetCredentialAsync(string account, CancellationToken cancellationToken = default)
    {
        Requested.Add(account);
        return ValueTask.FromResult(_values.TryGetValue(account, out var value) ? new ProviderCredential(value) : null);
    }
}

sealed class FakeProbe : IProviderHealthProbe
{
    public ProviderHealthResponse Response { get; set; } = new(ProviderHealthOutcome.Healthy);
    public Exception? Error { get; set; }
    public bool WaitForCancellation { get; set; }
    public List<ProviderHealthRequest> Requests { get; } = [];
    public async ValueTask<ProviderHealthResponse> CheckAsync(ProviderHealthRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Error is not null) throw Error;
        if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return Response;
    }
}

sealed class FakeLocal(bool installed) : ILocalModelReadinessSource
{
    public ValueTask<bool> IsInstalledAsync(ModelCapability model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(installed);
}

sealed class FakeMetadataTransport : HttpMessageInvoker
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;
    public FakeMetadataTransport(Func<HttpRequestMessage, HttpResponseMessage> response)
        : this((request, _) => Task.FromResult(response(request))) { }
    public FakeMetadataTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : base(new RejectingHandler()) => _response = response;
    public List<HttpRequestMessage> Requests { get; } = [];
    public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _response(request, cancellationToken);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test invoker override was bypassed.");
    }
}
