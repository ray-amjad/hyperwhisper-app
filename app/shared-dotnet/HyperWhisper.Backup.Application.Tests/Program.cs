using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperWhisper.Data.Entities;
using Microsoft.Data.Sqlite;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;
using uniffi.hyperwhisper_core;

var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Backup.Application.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var paths = new TestPaths(root);
    var files = new MemoryPrivateFileService();
    var settings = new PortableSettingsService(files, paths);
    Assert(settings.Load().IsSuccess, "settings did not initialize");
    settings.Set("language", "en");
    Assert(settings.Save().IsSuccess, "settings seed did not save");
    var database = new ApplicationDb(paths);
    await database.MigrateAsync();
    var modes = new ModeRepository(database);
    var first = new Mode { Id = Guid.NewGuid(), Name = "First", IsDefault = true, SortOrder = 0 };
    var second = new Mode { Id = Guid.NewGuid(), Name = "Second", SortOrder = 1 };
    await modes.UpsertAsync(first);
    await modes.UpsertAsync(second);
    var vocabulary = new VocabularyRepository(database);
    await vocabulary.AddAsync(new VocabularyItem { Id = Guid.NewGuid(), Word = "Alpha", Replacement = "old", SortOrder = 0 });
    var credentialStore = new MemoryCredentialStore();
    credentialStore.Seed("OpenAIApiKey", "openai-export-secret");
    credentialStore.Seed("AnthropicApiKey", "anthropic-export-secret");
    credentialStore.Seed("LicenseKey", "account-key-must-never-export");
    var service = new ApplicationBackupService(database, settings, credentialStore);

    var vocabularyOnly = await service.ExportAsync(new BackupExportSelection(
        IncludeSettings: false, IncludeModes: false, IncludeVocabulary: true));
    var vocabularyOnlyRoot = JsonNode.Parse(vocabularyOnly)!.AsObject();
    Assert(!vocabularyOnlyRoot.ContainsKey("settings") && !vocabularyOnlyRoot.ContainsKey("platformExtensions")
        && !vocabularyOnlyRoot.ContainsKey("modes")
        && vocabularyOnlyRoot.ContainsKey("vocabulary"), "selective export did not preserve section presence");
    Assert(!vocabularyOnlyRoot.ContainsKey("apiKeys") && !vocabularyOnlyRoot.ContainsKey("licenseKey"),
        "export leaked a credential or account/license field");
    var inspectedVocabulary = service.Inspect(vocabularyOnly);
    Assert(inspectedVocabulary.IsSuccess && inspectedVocabulary.Value is
        { HasSettings: false, HasModes: false, HasVocabulary: true, VocabularyCount: 1 },
        "inspection did not distinguish absent sections");
    var credentialExport = JsonNode.Parse(await service.ExportAsync(new BackupExportSelection(
        IncludeSettings: false, IncludeModes: false, IncludeVocabulary: false, IncludeCredentials: true)))!.AsObject();
    Assert(credentialExport["apiKeys"] is JsonObject exportedKeys
        && exportedKeys.Count == 2
        && exportedKeys["openai"]!.GetValue<string>() == "openai-export-secret"
        && exportedKeys["anthropic"]!.GetValue<string>() == "anthropic-export-secret"
        && !credentialExport.ContainsKey("licenseKey")
        && !credentialExport.ToJsonString().Contains("account-key-must-never-export", StringComparison.Ordinal),
        "explicit API-key export was incomplete or leaked an account/license key");
    var selectedExport = JsonNode.Parse(await service.ExportAsync(BackupExportSelection.SelectedModes([second.Id])))!.AsObject();
    Assert(selectedExport["modes"]!.AsArray() is [{ } selectedMode]
        && selectedMode["id"]!.GetValue<string>() == second.Id.ToString("D")
        && selectedMode["isDefault"]!.GetValue<bool>(),
        "selected-mode export did not explicitly produce a self-contained default mode");

    var full = JsonNode.Parse(await service.ExportAsync())!.AsObject();
    full["apiKeys"] = new JsonObject
    {
        ["openai"] = "openai-import-secret",
        ["anthropic"] = "anthropic-import-secret",
        ["groq"] = "groq-import-secret",
        ["unknown-future-provider"] = "must-be-ignored",
    };
    full["licenseKey"] = "must-not-import";
    full["platformExtensions"]!["windows"] = new JsonObject { ["futureWindows"] = 17 };
    full["platformExtensions"]!["linux"]!["settings"]!["language"] = "fr";
    full["platformExtensions"]!["linux"]!["settings"]!["themeMode"] = "dark";
    full["platformExtensions"]!["linux"]!["settings"]!["minimizeToTray"] = false;
    full["settings"]!["general"]!["launchMinimized"] = true;
    full["settings"]!["general"]!["showRecordingWindow"] = false;
    full["settings"]!["advanced"]!["typingSpeedWPM"] = 80;
    var fullModes = full["modes"]!.AsArray();
    fullModes[0]!["name"] = "Imported First";
    fullModes[0]!["platformExtensions"]!["macos"] = new JsonObject { ["futureMac"] = "keep" };
    var fullVocabulary = full["vocabulary"]!.AsArray();
    fullVocabulary[0]!["replacement"] = "new";
    fullVocabulary.Add(new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString("D"), ["word"] = "Beta", ["replacement"] = "second",
        ["sortOrder"] = 1, ["source"] = "manual",
    });
    var importJson = full.ToJsonString();
    var inspection = service.Inspect(importJson);
    Assert(inspection.IsSuccess && inspection.Value is { ContainsCredentials: true, ContainsLicenseKey: true, HasModes: true },
        "inspection did not report sensitive input fields without importing them");

    var skipSelection = new BackupImportSelection(
        ImportSettings: false, ImportModes: false, ImportVocabulary: true,
        VocabularyConflictPolicy: VocabularyConflictPolicy.Skip);
    var skipPreview = await service.PreviewImportAsync(importJson, skipSelection);
    Assert(skipPreview.IsSuccess && skipPreview.Value is
        { WillImportSettings: false, VocabularyAdded: 1, VocabularySkipped: 1, VocabularyReplaced: 0 },
        "skip-conflict preview counts were wrong");
    var skipped = await service.ImportAsync(importJson, skipSelection);
    Assert(skipped.IsSuccess && skipped.Value is { VocabularyAdded: 1, VocabularySkipped: 1 },
        "skip-conflict summary counts were wrong");
    var afterSkip = await vocabulary.ListAsync();
    Assert(afterSkip.Single(item => item.Word == "Alpha").Replacement == "old" && afterSkip.Any(item => item.Word == "Beta"),
        "skip-conflict import changed the conflicting value or omitted the new value");
    Assert((await modes.ListAsync()).Any(item => item.Name == "First") && settings.Get<string>("language") == "en",
        "unselected sections were imported");
    Assert(credentialStore.Text("OpenAIApiKey") == "openai-export-secret"
        && credentialStore.Text("AnthropicApiKey") == "anthropic-export-secret"
        && !credentialStore.Contains("unknown-future-provider"),
        "credentials were imported without explicit selection");

    var credentialSelection = new BackupImportSelection(
        ImportSettings: false, ImportModes: false, ImportVocabulary: false, ImportCredentials: true);
    var credentialPreview = await service.PreviewImportAsync(importJson, credentialSelection);
    Assert(credentialPreview.IsSuccess && credentialPreview.Value!.CredentialsToImport == 3,
        "credential preview did not count only recognized non-empty API keys");
    var credentialImport = await service.ImportAsync(importJson, credentialSelection);
    Assert(credentialImport.IsSuccess && credentialImport.Value!.CredentialsImported == 3
        && credentialStore.Text("OpenAIApiKey") == "openai-import-secret"
        && credentialStore.Text("AnthropicApiKey") == "anthropic-import-secret"
        && credentialStore.Text("GroqApiKey") == "groq-import-secret"
        && credentialStore.Text("LicenseKey") == "account-key-must-never-export",
        "selected API keys were not imported directly into secure storage or an account key changed");

    credentialStore.Seed("AnthropicApiKey", "anthropic-before-failure");
    credentialStore.Seed("GroqApiKey", "groq-before-failure");
    credentialStore.FailWriteAccount = "GroqApiKey";
    var failedCredentialImport = await service.ImportAsync(importJson, credentialSelection);
    credentialStore.FailWriteAccount = null;
    Assert(failedCredentialImport.Error?.Code == "backup.credential_store_failed"
        && credentialStore.Text("AnthropicApiKey") == "anthropic-before-failure"
        && credentialStore.Text("GroqApiKey") == "groq-before-failure",
        "failed secure-store import did not roll back previously written API keys");

    // The Gemini 3.5 Transcribe key is a SEPARATE credential from the legacy
    // "gemini" post-processing key: same "AIza" shape, different API. Before
    // this was wired up, a user who held only the Transcribe key exported a
    // backup that restored a machine with the provider silently unconfigured —
    // and the legacy key restoring fine is exactly what masked it. The provider
    // id is squashed lowercase, matching the Windows [JsonPropertyName] and the
    // macOS member, so one file round-trips across all three platforms.
    var geminiStore = new MemoryCredentialStore();
    geminiStore.Seed("GeminiTranscribeApiKey", "aiza-transcribe-only-secret");
    var geminiService = new ApplicationBackupService(database, settings, geminiStore);
    var geminiExport = JsonNode.Parse(await geminiService.ExportAsync(new BackupExportSelection(
        IncludeSettings: false, IncludeModes: false, IncludeVocabulary: false, IncludeCredentials: true)))!.AsObject();
    Assert(geminiExport["apiKeys"] is JsonObject geminiKeys
        && geminiKeys.Count == 1
        && geminiKeys["geminitranscribe"]!.GetValue<string>() == "aiza-transcribe-only-secret"
        && !geminiKeys.ContainsKey("gemini"),
        "the Gemini 3.5 Transcribe key was not exported under its own provider id");
    var geminiRestore = new MemoryCredentialStore();
    var geminiImport = await new ApplicationBackupService(database, settings, geminiRestore)
        .ImportAsync(geminiExport.ToJsonString(), credentialSelection);
    Assert(geminiImport.IsSuccess && geminiImport.Value!.CredentialsImported == 1
        && geminiRestore.Text("GeminiTranscribeApiKey") == "aiza-transcribe-only-secret"
        && !geminiRestore.Contains("GeminiApiKey"),
        "the Gemini 3.5 Transcribe key did not survive a backup round trip");

    // A Windows or macOS backup spells this id in camelCase. Before the id
    // charset accepted capitals, one such member failed the WHOLE restore —
    // modes, vocabulary and settings with it, not just the keys — while the
    // sibling platforms read the same file fine.
    var camelCaseExport = geminiExport.DeepClone().AsObject();
    camelCaseExport["apiKeys"] = new JsonObject { ["geminiTranscribe"] = "aiza-camel-case-secret" };
    Assert(camelCaseExport.ToJsonString().Contains("geminiTranscribe", StringComparison.Ordinal),
        "the camelCase fixture lost its capital letter before the assertion ran");
    Assert(new ApplicationBackupService(database, settings, new MemoryCredentialStore())
        .Inspect(camelCaseExport.ToJsonString()).Error is null,
        "a camelCase API-key provider id failed the whole restore");
    var camelCaseRestore = new MemoryCredentialStore();
    var camelCaseImport = await new ApplicationBackupService(database, settings, camelCaseRestore)
        .ImportAsync(camelCaseExport.ToJsonString(), credentialSelection);
    Assert(camelCaseImport.IsSuccess && camelCaseImport.Value!.CredentialsImported == 1
        && camelCaseRestore.Text("GeminiTranscribeApiKey") == "aiza-camel-case-secret",
        "a camelCase provider id did not fold onto its canonical account");

    var invalidProvider = full.DeepClone().AsObject();
    invalidProvider["apiKeys"] = new JsonObject { [new string('p', 65)] = "value" };
    Assert(service.Inspect(invalidProvider.ToJsonString()).Error?.Code == "backup.invalid_credentials",
        "an unbounded API-key provider identifier was accepted");
    invalidProvider["apiKeys"] = new JsonObject { ["Invalid Provider"] = "value" };
    Assert(service.Inspect(invalidProvider.ToJsonString()).Error?.Code == "backup.invalid_credentials",
        "an invalid API-key provider identifier was accepted");
    var oversizedCredential = full.DeepClone().AsObject();
    oversizedCredential["apiKeys"] = new JsonObject { ["openai"] = new string('s', 16 * 1024 + 1) };
    Assert(service.Inspect(oversizedCredential.ToJsonString()).Error?.Code == "backup.invalid_credentials",
        "an oversized API-key value was accepted");

    var replaceSelection = skipSelection with { VocabularyConflictPolicy = VocabularyConflictPolicy.Replace };
    var replaced = await service.ImportAsync(importJson, replaceSelection);
    Assert(replaced.IsSuccess && replaced.Value is { VocabularyAdded: 0, VocabularyReplaced: 2 },
        "replace-conflict summary counts were wrong");
    Assert((await vocabulary.ListAsync()).Single(item => item.Word == "Alpha").Replacement == "new",
        "replace-conflict policy did not deterministically replace the existing entry");

    var selectedModes = BackupImportSelection.SelectedModes([first.Id]);
    var modePreview = await service.PreviewImportAsync(importJson, selectedModes);
    Assert(modePreview.IsSuccess && modePreview.Value is { ModesAdded: 0, ModesReplaced: 1, ModesRemoved: 0 },
        "selected-mode preview counts were wrong");
    var selectedResult = await service.ImportAsync(importJson, selectedModes);
    Assert(selectedResult.IsSuccess, "selected-mode import failed");
    var afterSelected = await modes.ListAsync();
    Assert(afterSelected.Single(item => item.Id == first.Id).Name == "Imported First"
        && afterSelected.Single(item => item.Id == second.Id).Name == "Second",
        "selected-mode merge changed an unselected mode");

    var emptySelection = selectedModes with { SelectedModeIds = ImmutableHashSet<Guid>.Empty };
    Assert((await service.ImportAsync(importJson, emptySelection)).IsSuccess,
        "an explicit empty selected-mode set was not treated as a no-op");
    var missingSelection = selectedModes with { SelectedModeIds = ImmutableHashSet.Create(Guid.NewGuid()) };
    var missingPreview = await service.PreviewImportAsync(importJson, missingSelection);
    Assert(missingPreview.IsSuccess && missingPreview.Value!.MissingSelectedModeIds.Length == 1,
        "preview did not report a missing selected mode");
    Assert((await service.ImportAsync(importJson, missingSelection)).Error?.Code == "backup.selected_modes_missing",
        "a missing selected mode was silently ignored");

    var noModes = full.DeepClone().AsObject();
    noModes.Remove("modes");
    var beforeAbsentModes = (await modes.ListAsync()).Select(item => (item.Id, item.Name)).ToArray();
    Assert((await service.ImportAsync(noModes.ToJsonString(), BackupImportSelection.All)).IsSuccess,
        "backup with an absent modes section failed");
    Assert(settings.Get<string>("themeMode") == "dark" && !settings.Get("minimizeToTray", true)
        && settings.Get("general.launchMinimized", false) && !settings.Get("general.showRecordingWindow", true)
        && settings.Get("advanced.typingSpeedWPM", 0) == 80,
        "shell, overlay, tray, or typing-speed settings did not import through their canonical mappings");
    Assert(beforeAbsentModes.SequenceEqual((await modes.ListAsync()).Select(item => (item.Id, item.Name))),
        "an absent modes section modified the current mode library");

    var emptyModes = full.DeepClone().AsObject();
    emptyModes["modes"] = new JsonArray();
    Assert((await service.ImportAsync(emptyModes.ToJsonString(), BackupImportSelection.All)).Error?.Code == "backup.no_modes",
        "an explicitly empty replace-all modes section wiped or bypassed the mode invariant");

    var rollback = full.DeepClone().AsObject();
    rollback["modes"]![0]!["name"] = "Must Roll Back";
    files.FailWrites = true;
    var failed = await service.ImportAsync(rollback.ToJsonString(), BackupImportSelection.All);
    files.FailWrites = false;
    Assert(failed.IsFailure && (await modes.ListAsync()).All(item => item.Name != "Must Roll Back")
        && settings.Get<string>("language") == "fr", "settings failure did not roll back DB and in-memory settings");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(
        () => service.ImportAsync(importJson, BackupImportSelection.All, cancelled.Token),
        "cancellation was swallowed");
    Assert((await modes.ListAsync()).All(item => item.Name != "Must Roll Back"), "cancellation mutated the database");

    var viewModelImportPath = Path.Combine(root, "view-model-import.json");
    await File.WriteAllTextAsync(viewModelImportPath, importJson);
    var backupViewModel = new BackupViewModel(service) { Path = viewModelImportPath };
    await backupViewModel.InspectAsync();
    Assert(backupViewModel.HasInspectedBackup && backupViewModel.Contents is
        { ContainsCredentials: true, ContainsLicenseKey: true }
        && backupViewModel.ContainsImportableCredentials && backupViewModel.ContainsUnsupportedSensitiveData
        && backupViewModel.SensitiveDataNotice.Contains("explicitly selected", StringComparison.Ordinal)
        && backupViewModel.SensitiveDataNotice.Contains("never imported", StringComparison.Ordinal),
        "view model did not distinguish importable API keys from unsupported account/license fields");
    Assert(backupViewModel.ImportModeSelections.Count == 2 && !backupViewModel.CanConfirmImport,
        "view model did not expose inspected modes or required a preview");
    backupViewModel.ImportSettings = false;
    backupViewModel.ImportModes = false;
    backupViewModel.SkipVocabularyConflicts = true;
    await backupViewModel.PreviewAsync();
    Assert(!backupViewModel.CanConfirmImport && backupViewModel.Preview is
        { WillImportSettings: false, ModesAdded: 0, ModesReplaced: 0, VocabularySkipped: 2 }
        && backupViewModel.PreviewSummary.Contains("skip 2", StringComparison.Ordinal),
        "view model preview did not reflect selected sections, conflict policy, or require confirmation");
    backupViewModel.ImportConfirmed = true;
    Assert(backupViewModel.CanConfirmImport, "explicit preview confirmation did not enable import");
    backupViewModel.ReplaceVocabularyConflicts = true;
    Assert(!backupViewModel.CanConfirmImport, "changing a selection did not invalidate stale confirmation");
    await backupViewModel.PreviewAsync();
    backupViewModel.ImportConfirmed = true;
    await File.WriteAllTextAsync(viewModelImportPath, "not the inspected backup");
    var importedEventRaised = false;
    backupViewModel.Imported += (_, _) => importedEventRaised = true;
    await backupViewModel.ImportAsync();
    Assert(importedEventRaised && !backupViewModel.CanConfirmImport
        && backupViewModel.OperationSummary.Contains("Imported settings: False", StringComparison.Ordinal),
        "confirmed import did not use the inspected/previewed snapshot or expose its summary");

    var viewModelExportPath = Path.Combine(root, "view-model-export.json");
    backupViewModel.Path = viewModelExportPath;
    backupViewModel.ExportSettings = false;
    backupViewModel.ExportModes = false;
    backupViewModel.ExportVocabulary = true;
    await backupViewModel.ExportAsync();
    var viewModelExport = JsonNode.Parse(await File.ReadAllTextAsync(viewModelExportPath))!.AsObject();
    Assert(!viewModelExport.ContainsKey("settings") && !viewModelExport.ContainsKey("modes")
        && viewModelExport.ContainsKey("vocabulary") && !viewModelExport.ContainsKey("apiKeys")
        && backupViewModel.OperationSummary.Contains("license", StringComparison.Ordinal),
        "view model export did not honor section selection or disclose sensitive-field exclusion");
    backupViewModel.ExportCredentials = true;
    await backupViewModel.ExportAsync();
    Assert(backupViewModel.Status.ErrorCode == "backup.plaintext_acknowledgement_required"
        && backupViewModel.PlaintextCredentialWarning.Contains("plaintext", StringComparison.Ordinal)
        && backupViewModel.PlaintextCredentialWarning.Contains("Anyone with the file", StringComparison.Ordinal),
        "view model exported API keys without a strong explicit plaintext warning acknowledgement");
    backupViewModel.PlaintextCredentialExportAcknowledged = true;
    await backupViewModel.ExportAsync();
    Assert(JsonNode.Parse(await File.ReadAllTextAsync(viewModelExportPath))!["apiKeys"] is JsonObject,
        "view model did not export explicitly acknowledged API keys");
    backupViewModel.Path = "relative.json";
    await backupViewModel.InspectAsync();
    Assert(backupViewModel.Status.ErrorCode == "backup.path_required", "view model path validation was not stable");
    backupViewModel.Path = viewModelImportPath;
    using var viewModelCancellation = new CancellationTokenSource();
    viewModelCancellation.Cancel();
    await backupViewModel.InspectAsync(viewModelCancellation.Token);
    Assert(backupViewModel.Status.ErrorCode == "backup.cancelled", "view model cancellation was not reported distinctly");

    var roundTrip = JsonNode.Parse(await service.ExportAsync())!.AsObject();
    Assert(roundTrip["platformExtensions"]!["windows"]!["futureWindows"]!.GetValue<int>() == 17
        && roundTrip["modes"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == first.Id.ToString("D"))!
            ["platformExtensions"]!["macos"]!["futureMac"]!.GetValue<string>() == "keep",
        "foreign platform extensions were not preserved across import/export");

    // --- Catalog v8: a backup written by a pre-v8 client -------------------
    //
    // A backup file is the one input that is arbitrarily old, and it bypasses the
    // one-shot EF migration entirely: that migration is already recorded as
    // applied by the time a restore runs, so whatever the file says is what lands
    // in the database. Every retired Chirp 3 spelling has to be canonicalised on
    // the way in — and the failure is silent, because an unmigrated id resolves
    // to Deepgram at read time with no error.
    //
    // Since #288 the canonicalisation happens inside the shared core, on the
    // NormalizeCloudRouting hop every imported mode already takes — not in a
    // private helper on this path. These assertions are what pins that the port
    // kept the behaviour, so they are written against the observable restore.
    foreach (var legacyTier in new[]
    {
        "googleChirp3", "googlechirp3", "GOOGLECHIRP3",
        "googlechirp", "google-chirp", "chirp", "chirp_3", "googlespeech",
    })
    {
        var legacyBackup = JsonNode.Parse(await service.ExportAsync(
            BackupExportSelection.SelectedModes([second.Id])))!.AsObject();
        legacyBackup["modes"]!.AsArray()[0]!["cloudAccuracyTier"] = legacyTier;
        Assert((await service.ImportAsync(legacyBackup.ToJsonString())).IsSuccess,
            $"a backup carrying the retired tier '{legacyTier}' failed to import");

        var restored = (await modes.ListAsync()).Single(item => item.Id == second.Id);
        Assert(restored.CloudAccuracyTier != "deepgramNova3",
            $"a backup carrying '{legacyTier}' restored onto Deepgram — the documented silent "
                + "failure: the user changes vendor, credits and X-STT-Provider with no error.");
        Assert(restored.CloudAccuracyTier == "geminiTranscribe",
            $"a backup carrying '{legacyTier}' restored as '{restored.CloudAccuracyTier}'; the "
                + "portable import path must canonicalise through the shared core.");
    }

    // An absent tier keeps this path's own documented default rather than the
    // core's empty-input answer (deepgramNova3).
    var noTierBackup = JsonNode.Parse(await service.ExportAsync(
        BackupExportSelection.SelectedModes([second.Id])))!.AsObject();
    noTierBackup["modes"]!.AsArray()[0]!.AsObject().Remove("cloudAccuracyTier");
    Assert((await service.ImportAsync(noTierBackup.ToJsonString())).IsSuccess, "tier-less backup failed to import");
    Assert((await modes.ListAsync()).Single(item => item.Id == second.Id).CloudAccuracyTier == "elevenLabsScribeV2",
        "a backup with no cloudAccuracyTier no longer falls back to elevenLabsScribeV2");

    // NATIVE CAPTURE (issue #277, phase 1a). Drives every
    // shared-conformance/backup-vectors.json modeNormalization row through the SHIPPING
    // Linux mode-import path (ApplicationBackupService.ImportAsync -> ParseMode) and pins
    // the answer this build produces. It changes no behavior; it records it, so the Rust
    // port can be diffed on the same inputs before the native copies go.
    //
    // A row carries "expected" when Windows and Linux already agree, and
    // "expectedWindows"/"expectedLinux" when they do not — Linux runs no cloud-routing
    // migration, no catalog provider normalization, no model-alias resolution and no
    // cloudTranscriptionDomain gate, so most rows differ today. That recorded
    // disagreement IS the drift #277 asked to be documented; 1b collapses it.
    // The same file is read by app/windows/HyperWhisper.SmokeTests.
    var vectorsPath = Path.Combine(AppContext.BaseDirectory, "backup-vectors.json");
    Assert(File.Exists(vectorsPath), $"backup-vectors.json not found at {vectorsPath}");
    var vectorRows = JsonNode.Parse(await File.ReadAllTextAsync(vectorsPath))!
        .AsObject()["modeNormalization"]!.AsArray();
    Assert(vectorRows.Count > 0, "backup-vectors.json has no modeNormalization rows");

    // A dedicated database so the vector modes cannot perturb the assertions above
    // (BackupImportSelection.All replaces every existing mode).
    var vectorRoot = Path.Combine(root, "mode-normalization-vectors");
    Directory.CreateDirectory(vectorRoot);
    var vectorPaths = new TestPaths(vectorRoot);
    var vectorSettings = new PortableSettingsService(new MemoryPrivateFileService(), vectorPaths);
    Assert(vectorSettings.Load().IsSuccess, "vector settings did not initialize");
    var vectorDatabase = new ApplicationDb(vectorPaths);
    await vectorDatabase.MigrateAsync();
    var vectorService = new ApplicationBackupService(vectorDatabase, vectorSettings);

    var vectorBackup = new JsonObject
    {
        ["schemaVersion"] = 2,
        ["exportDate"] = DateTimeOffset.UtcNow.ToString("O"),
        ["appVersion"] = "1.0.0",
        ["platform"] = "linux",
        ["modes"] = new JsonArray(vectorRows.Select(item => item!["mode"]!.DeepClone()).ToArray()),
    };
    var vectorImport = await vectorService.ImportAsync(vectorBackup.ToJsonString());
    Assert(vectorImport.IsSuccess, $"vector import failed: {vectorImport.Error?.Message}");

    var importedModes = (await new ModeRepository(vectorDatabase).ListAsync())
        .ToDictionary(item => item.Id);
    foreach (var vectorRow in vectorRows)
    {
        var vector = vectorRow!.AsObject();
        var label = vector["name"]!.GetValue<string>();
        var expected = (vector["expected"] ?? vector["expectedLinux"]) as JsonObject
            ?? throw new InvalidOperationException(
                $"vector '{label}' declares neither 'expected' nor 'expectedLinux'");
        var id = Guid.Parse(vector["mode"]!["id"]!.GetValue<string>());
        Assert(importedModes.TryGetValue(id, out var imported), $"vector '{label}' was not imported");

        AssertModeVectorField(label, "cloudProvider", expected, imported!.CloudProvider);
        AssertModeVectorField(label, "cloudTranscriptionModel", expected, imported.CloudTranscriptionModel);
        AssertModeVectorField(label, "cloudTranscriptionDomain", expected, imported.CloudTranscriptionDomain);
        AssertModeVectorField(label, "cloudAccuracyTier", expected, imported.CloudAccuracyTier);
        AssertModeVectorField(label, "cloudPostProcessingModel", expected, imported.CloudPostProcessingModel);
    }

    Console.WriteLine($"Backup mode-normalization vectors: {vectorRows.Count}/{vectorRows.Count} rows matched the Linux import.");

    // NATIVE CAPTURE (issue #277, phase 2a) — the SETTINGS halves.
    //
    // linuxSettings rows run through the SHIPPING Linux settings adapters:
    // ApplicationBackupService.ExportAsync -> BuildSharedSettings for the export
    // direction, and ImportAsync -> ApplySharedSettings/CopyCategory for the
    // import direction. Both are PRIVATE, so the public service is the only way
    // in — which is also the honest capture, since it is what a user's backup
    // actually traverses. Native keys in the vectors are the dotted
    // PortableSettingsService storage keys, which on Linux ARE the universal
    // keys: this half of the map is near-identity and renames nothing.
    //
    // macosSettings rows run through the core adapter the macOS app already
    // uses (hw-backup mapping.rs macos_settings_to_universal /
    // universal_to_macos_settings, reached over the same UniFFI binding macOS
    // uses). Capturing it here means a 2b regression in the shared mapping is
    // visible on linux-ci, and phase 4's vectors already cover all three heads.
    //
    // Nothing in this block changes behavior; it records it.
    var settingsDocument = JsonNode.Parse(await File.ReadAllTextAsync(vectorsPath))!.AsObject();

    var linuxRows = settingsDocument["linuxSettings"]!.AsArray();
    Assert(linuxRows.Count > 0, "backup-vectors.json has no linuxSettings rows");
    var linuxCase = 0;
    foreach (var linuxRowNode in linuxRows)
    {
        var row = linuxRowNode!.AsObject();
        var label = row["name"]!.GetValue<string>();
        var direction = row["direction"]!.GetValue<string>();
        linuxCase++;

        // One profile per row so a row can never observe the previous row's state.
        var caseRoot = Path.Combine(root, $"linux-settings-{linuxCase:D2}");
        Directory.CreateDirectory(caseRoot);
        var casePaths = new TestPaths(caseRoot);
        var caseSettings = new PortableSettingsService(new MemoryPrivateFileService(), casePaths);
        Assert(caseSettings.Load().IsSuccess, $"linuxSettings '{label}': settings did not initialize");
        var caseDatabase = new ApplicationDb(casePaths);
        await caseDatabase.MigrateAsync();
        var caseService = new ApplicationBackupService(caseDatabase, caseSettings);

        var seed = row[direction == "export" ? "native" : "baselineNative"]!.AsObject();
        foreach (var entry in seed)
            caseSettings.Set(entry.Key, entry.Value!.DeepClone());
        Assert(caseSettings.Save().IsSuccess, $"linuxSettings '{label}': seed did not save");

        if (direction == "export")
        {
            var exported = JsonNode.Parse(await caseService.ExportAsync(new BackupExportSelection(
                IncludeSettings: true, IncludeModes: false, IncludeVocabulary: false)))!.AsObject();
            AssertVectorJson(label, "settings", row["expectedUniversal"], exported["settings"]);
            continue;
        }

        Assert(direction == "import", $"linuxSettings '{label}': unknown direction '{direction}'");
        var importJsonForRow = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["exportDate"] = DateTimeOffset.UtcNow.ToString("O"),
            ["appVersion"] = "1.0.0",
            ["platform"] = "linux",
            ["settings"] = row["universal"]!.DeepClone(),
        }.ToJsonString();
        var imported = await caseService.ImportAsync(importJsonForRow, new BackupImportSelection(
            ImportSettings: true, ImportModes: false, ImportVocabulary: false));
        Assert(imported.IsSuccess, $"linuxSettings '{label}': import failed: {imported.Error?.Message}");

        foreach (var expected in row["expectedNative"]!.AsObject())
        {
            var stored = caseSettings.Get<JsonElement?>(expected.Key);
            Assert(stored.HasValue, $"linuxSettings '{label}': storage key '{expected.Key}' is missing");
            AssertVectorJson(label, expected.Key, expected.Value,
                JsonNode.Parse(stored!.Value.GetRawText()));
        }
        foreach (var ignored in row["ignoredUniversalKeys"]?.AsArray() ?? [])
            Assert(caseSettings.Get<JsonElement?>(ignored!.GetValue<string>()) is null,
                $"linuxSettings '{label}': '{ignored}' reached storage — CopyCategory is supposed to drop it");
    }
    Console.WriteLine($"Backup linux-settings vectors: {linuxRows.Count}/{linuxRows.Count} rows matched the Linux adapters.");

    // PHASE 2b — the deep-merge on the Linux import half, asserted to be a NO-OP.
    //
    // ApplySharedSettings now converts in the shared core and then deep-merges the
    // core's answer over a baseline snapshot before _settings.Replace(). Two things
    // have to hold, and neither is covered by the vector rows above:
    //
    //   1. Replace() must not WIPE the store. The old code Set() one key at a time,
    //      so Linux-only keys, backup.platformExtensions and anything else outside
    //      the 23 shared keys were never at risk. They are now only safe because the
    //      baseline is the merge's floor.
    //   2. A universal block that carries ONE key must change exactly that key. The
    //      core is present-only today, so the merge is inert — but the day it returns
    //      a COMPLETE native blob, this is the assertion that stops an absent backup
    //      key arriving as a core default and clobbering a live setting.
    {
        var mergeRoot = Path.Combine(root, "linux-settings-deep-merge");
        Directory.CreateDirectory(mergeRoot);
        var mergePaths = new TestPaths(mergeRoot);
        var mergeSettings = new PortableSettingsService(new MemoryPrivateFileService(), mergePaths);
        Assert(mergeSettings.Load().IsSuccess, "deep-merge: settings did not initialize");

        // A shared key the import will touch, a shared key it will not, and three
        // keys with no pairs row at all — one of them device-local.
        mergeSettings.Set("general.launchMinimized", false);
        mergeSettings.Set("advanced.typingSpeedWPM", 95);
        mergeSettings.Set("localWhisperBackend", "cuda12");
        mergeSettings.Set("selectedModeId", "device-local-value");
        mergeSettings.Set("backup.platformExtensions",
            JsonSerializer.SerializeToElement(new JsonObject { ["macos"] = new JsonObject() }));
        Assert(mergeSettings.Save().IsSuccess, "deep-merge: seed did not save");

        var mergeDatabase = new ApplicationDb(mergePaths);
        await mergeDatabase.MigrateAsync();
        var mergeService = new ApplicationBackupService(mergeDatabase, mergeSettings);

        var oneKey = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["exportDate"] = DateTimeOffset.UtcNow.ToString("O"),
            ["appVersion"] = "1.0.0",
            ["platform"] = "linux",
            ["settings"] = new JsonObject { ["general"] = new JsonObject { ["launchMinimized"] = true } },
        }.ToJsonString();
        var mergeImport = await mergeService.ImportAsync(oneKey, new BackupImportSelection(
            ImportSettings: true, ImportModes: false, ImportVocabulary: false));
        Assert(mergeImport.IsSuccess, $"deep-merge: import failed: {mergeImport.Error?.Message}");

        Assert(mergeSettings.Get<bool>("general.launchMinimized"),
            "deep-merge: the one key the backup carried should have been applied");
        Assert(mergeSettings.Get<int>("advanced.typingSpeedWPM") == 95,
            "deep-merge: a shared key the backup did NOT carry was clobbered — the "
            + "baseline is supposed to be the merge's floor, not a core default");
        Assert(mergeSettings.Get<string>("localWhisperBackend") == "cuda12",
            "deep-merge: Replace() wiped a Linux-only key that has no pairs row");
        Assert(mergeSettings.Get<string>("selectedModeId") == "device-local-value",
            "deep-merge: Replace() wiped a device-local key");
        Assert(mergeSettings.Get<JsonElement?>("backup.platformExtensions") is not null,
            "deep-merge: Replace() wiped the preserved foreign platformExtensions");

        // And the export half must never promote a key without a pairs row.
        var mergeExport = JsonNode.Parse(await mergeService.ExportAsync(new BackupExportSelection(
            IncludeSettings: true, IncludeModes: false, IncludeVocabulary: false)))!.AsObject();
        var sharedBlock = mergeExport["settings"]!.ToJsonString();
        foreach (var leaked in new[] { "localWhisperBackend", "cuda12", "selectedModeId", "device-local-value" })
            Assert(!sharedBlock.Contains(leaked, StringComparison.Ordinal),
                $"deep-merge: '{leaked}' reached the shared settings block; the whole store "
                + "is handed to the core, so only keys with a pairs row may come back out");
    }
    Console.WriteLine("Backup linux-settings deep-merge: baseline preserved, no key leaked.");

    // PHASE 3c — the Linux half of the unknownKeyRoundTrip vectors.
    //
    // Top-level foreign-slice retention already worked here before #288; Windows and
    // macOS were the broken heads. This block exists so the SAME rows are asserted on
    // all three, and so a future Linux refactor cannot quietly drop what Windows and
    // macOS were just fixed to keep.
    //
    // Linux's STORAGE STRATEGY differs on purpose and the vector records it: Linux
    // persists the WHOLE imported map in the backup.platformExtensions setting and
    // OVERWRITES its own "linux" slice at export time (ApplicationBackupExport), where
    // Windows and macOS store only the foreign slices and add their own on the way
    // out. The observable contract is identical either way, and that is what the
    // expectedReExportedKeysByHead / own-slice assertions check.
    {
        var extensionRows = settingsDocument["unknownKeyRoundTrip"]!.AsArray()
            .Select(node => node!.AsObject())
            .Where(row => row["kind"]!.GetValue<string>() == "topLevelPlatformExtensions"
                && row["heads"]!.AsArray().Any(head => head!.GetValue<string>() == "linux"))
            .ToList();
        Assert(extensionRows.Count > 0,
            "backup-vectors.json has no topLevelPlatformExtensions row naming the linux head");

        var caseIndex = 0;
        foreach (var row in extensionRows)
        {
            var label = row["name"]!.GetValue<string>();
            var caseRoot = Path.Combine(root, $"linux-foreign-extensions-{caseIndex++}");
            Directory.CreateDirectory(caseRoot);
            var casePaths = new TestPaths(caseRoot);
            var caseSettings = new PortableSettingsService(new MemoryPrivateFileService(), casePaths);
            Assert(caseSettings.Load().IsSuccess, $"'{label}': settings did not initialize");
            // A live Linux-only value the export must rebuild its own slice from, so a
            // stale preserved "linux" slice cannot win.
            caseSettings.Set("autostartEnabled", false);
            Assert(caseSettings.Save().IsSuccess, $"'{label}': seed did not save");

            var caseDatabase = new ApplicationDb(casePaths);
            await caseDatabase.MigrateAsync();
            var caseService = new ApplicationBackupService(caseDatabase, caseSettings);

            var file = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["exportDate"] = DateTimeOffset.UtcNow.ToString("O"),
                ["appVersion"] = "1.0.0",
                ["platform"] = "linux",
                ["settings"] = new JsonObject(),
                ["platformExtensions"] = row["imported"]!.DeepClone(),
            }.ToJsonString();

            var import = await caseService.ImportAsync(file, new BackupImportSelection(
                ImportSettings: true, ImportModes: false, ImportVocabulary: false));
            Assert(import.IsSuccess, $"'{label}': import failed: {import.Error?.Message}");

            var stored = caseSettings.Get<JsonElement?>("backup.platformExtensions");
            Assert(stored is not null, $"'{label}': Linux did not persist the imported map at all");
            AssertVectorJson(label, "backup.platformExtensions",
                row["expectedStoredByHead"]!["linux"],
                JsonNode.Parse(stored!.Value.GetRawText()));

            // Flip a live Linux-only value AFTER the import (the import may legitimately
            // have applied the file's own "linux" slice). If the export still shows the
            // imported value, the own slice lost to a stale preserved copy.
            var flipped = !caseSettings.Get<bool>("autostartEnabled");
            caseSettings.Set("autostartEnabled", flipped);
            Assert(caseSettings.Save().IsSuccess, $"'{label}': flip did not save");

            var exported = JsonNode.Parse(await caseService.ExportAsync(new BackupExportSelection(
                IncludeSettings: true, IncludeModes: false, IncludeVocabulary: false)))!.AsObject();
            var reExported = exported["platformExtensions"]!.AsObject();

            var actualKeys = reExported.Select(entry => entry.Key)
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var wantKeys = row["expectedReExportedKeysByHead"]!["linux"]!.AsArray()
                .Select(key => key!.GetValue<string>())
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert(actualKeys.SequenceEqual(wantKeys, StringComparer.Ordinal),
                $"'{label}': re-exported top-level platformExtensions keys were "
                + $"[{string.Join(", ", actualKeys)}], expected [{string.Join(", ", wantKeys)}]");

            Assert(reExported["linux"]!["settings"]!["autostartEnabled"]!.GetValue<bool>() == flipped,
                $"'{label}': the \"linux\" slice must be rebuilt from live settings and "
                + "overwrite any preserved copy of itself");

            // Every foreign slice comes back verbatim.
            foreach (var expected in row["expectedStoredByHead"]!["linux"]!.AsObject())
            {
                if (expected.Key == "linux") continue;
                AssertVectorJson(label, $"re-emitted '{expected.Key}' slice",
                    expected.Value, reExported[expected.Key]);
            }
        }
        Console.WriteLine(
            $"Backup linux top-level platformExtensions: {extensionRows.Count} vector rows preserved.");
    }

    var macosRows = settingsDocument["macosSettings"]!.AsArray();
    Assert(macosRows.Count > 0, "backup-vectors.json has no macosSettings rows");
    foreach (var macosRowNode in macosRows)
    {
        var row = macosRowNode!.AsObject();
        var label = row["name"]!.GetValue<string>();
        var direction = row["direction"]!.GetValue<string>();
        if (direction == "toUniversal")
        {
            AssertVectorJson(label, "universal settings record", row["expectedUniversal"],
                JsonNode.Parse(HyperwhisperCoreMethods.MacosSettingsToUniversalSettingsJson(
                    row["macos"]!.ToJsonString(),
                    row["existingMacosExtension"]?.ToJsonString())));
            continue;
        }
        Assert(direction == "toMacos", $"macosSettings '{label}': unknown direction '{direction}'");
        AssertVectorJson(label, "macOS 7-category settings", row["expectedMacos"],
            JsonNode.Parse(HyperwhisperCoreMethods.UniversalSettingsToMacosSettingsJson(
                row["universal"]!.ToJsonString())));
    }
    Console.WriteLine($"Backup macos-settings vectors: {macosRows.Count}/{macosRows.Count} rows matched the shared core.");

    Console.WriteLine("Backup application tests passed (42/42).");
}
finally
{
    // Every assertion has already run by here, so this is cleanup and not a
    // test. It still has to work on BOTH hosts: this harness runs on Windows CI
    // as well as Linux CI since the backup vectors were wired into all three
    // stacks.
    //
    // Windows holds each SQLite file open in Microsoft.Data.Sqlite's CONNECTION
    // POOL after the last DbContext is disposed, so a recursive delete of the
    // temp root fails with "the process cannot access the file
    // 'hyperwhisper.db'". Linux lets the unlink through regardless. Draining the
    // pool releases the handles; the retry covers a handle the OS has not
    // finished closing yet, which is timing and not a leak.
    SqliteConnection.ClearAllPools();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            Directory.Delete(root, recursive: true);
            break;
        }
        catch (IOException) when (attempt < 5)
        {
            Thread.Sleep(200);
        }
        catch (IOException error)
        {
            // A temp directory left on a CI runner is noise; the exit code of
            // this harness means "the vectors matched", so do not overwrite a
            // pass with a cleanup failure. Say what was left behind instead.
            Console.Error.WriteLine($"warning: could not remove {root}: {error.Message}");
            break;
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// Compares one field of a backup-vectors.json expectation against the value the native
// importer produced. JSON null means the field must come out null — the
// absent-stays-absent rule the vectors exist to pin.
static void AssertModeVectorField(string label, string field, JsonObject expected, string? actual)
{
    Assert(expected.ContainsKey(field), $"vector '{label}' is missing the expected field '{field}'");
    var want = expected[field]?.GetValue<string>();
    Assert(want == actual,
        $"vector '{label}': {field} expected {Quote(want)}, got {Quote(actual)}");

    static string Quote(string? value) => value is null ? "null" : $"'{value}'";
}

// Compares a backup-vectors.json settings expectation against what the native
// adapter produced. JsonNode.DeepEquals is order-insensitive over object keys and
// number-representation-insensitive, so a row pins VALUES, never formatting.
static void AssertVectorJson(string label, string what, JsonNode? expected, JsonNode? actual)
{
    Assert(JsonNode.DeepEquals(expected, actual),
        $"vector '{label}': {what} mismatch{Environment.NewLine}  expected {Render(expected)}{Environment.NewLine}  actual   {Render(actual)}");

    static string Render(JsonNode? node) => node?.ToJsonString() ?? "null";
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}

file sealed class TestPaths(string root) : IAppPaths
{
    public string DataDirectory => root;
    public string ConfigDirectory => root;
    public string CacheDirectory => root;
    public string StateDirectory => root;
    public string LogsDirectory => root;
    public string ModelsDirectory => root;
    public string RecordingsDirectory => root;
    public string RuntimeDirectory => root;
    public string TemporaryDirectory => root;
}

file sealed class MemoryPrivateFileService : IPrivateFileService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    public bool FailWrites { get; set; }
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        if (FailWrites) return PlatformResult.Failure("test.write_failed", "Simulated write failure.");
        _files[path] = contents.ToArray();
        return PlatformResult.Success();
    }
    public PlatformResult WriteAllTextAtomically(string path, string contents)
        => WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));
    public PlatformResult<byte[]?> ReadAllBytes(string path)
        => PlatformResult<byte[]?>.Success(_files.TryGetValue(path, out var bytes) ? bytes.ToArray() : null);
    public PlatformResult<string?> ReadAllText(string path)
        => PlatformResult<string?>.Success(_files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null);
    public PlatformResult Delete(string path) { _files.Remove(path); return PlatformResult.Success(); }
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(true);
}

file sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    public string? FailWriteAccount { get; set; }

    public void Seed(string account, string value) => _values[account] = Encoding.UTF8.GetBytes(value);
    public bool Contains(string account) => _values.ContainsKey(account);
    public string? Text(string account) => _values.TryGetValue(account, out var value) ? Encoding.UTF8.GetString(value) : null;

    public PlatformResult<byte[]?> Read(string resource, string account)
        => PlatformResult<byte[]?>.Success(_values.TryGetValue(account, out var value) ? value.ToArray() : null);

    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value)
    {
        if (string.Equals(account, FailWriteAccount, StringComparison.Ordinal))
            return PlatformResult.Failure("test.write_failed", "Simulated secure-store failure.");
        _values[account] = value.ToArray();
        return PlatformResult.Success();
    }

    public PlatformResult Delete(string resource, string account)
    {
        _values.Remove(account);
        return PlatformResult.Success();
    }
}
