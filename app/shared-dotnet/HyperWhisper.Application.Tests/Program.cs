using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.AudioNormalization;
using HyperWhisper.SpeechOutput;
using HyperWhisper.SharedCore;
using HyperWhisper.PortableApplication.ViewModels;
using HyperWhisper.CloudAccount;

var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Application.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var paths = new TestPaths(root);
    var database = new ApplicationDb(paths);
    await database.MigrateAsync();
    await RunTranscriptionWorkflowTestsAsync(root);

    var freshDatabase = new ApplicationDb(new TestPaths(Path.Combine(root, "fresh-defaults")));
    await freshDatabase.InitializeAsync();
    await freshDatabase.InitializeAsync();
    var freshModes = await new ModeRepository(freshDatabase).ListAsync();
    Assert(freshModes.Count == 6 && freshModes.Single(item => item.IsDefault).Id == PortableModeDefaults.HyperModeId,
        "fresh database initialization did not idempotently seed the six portable defaults");

    var existingDatabase = new ApplicationDb(new TestPaths(Path.Combine(root, "existing-defaults")));
    await existingDatabase.MigrateAsync();
    var existingMode = new Mode { Name = "Existing", IsDefault = true };
    await new ModeRepository(existingDatabase).UpsertAsync(existingMode);
    await existingDatabase.InitializeAsync();
    Assert((await new ModeRepository(existingDatabase).ListAsync()).Single().Id == existingMode.Id,
        "database initialization replaced or supplemented an existing mode library");

    await using (var context = database.CreateContext())
    {
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert(applied.Count() == 14, $"expected all 14 EF migrations, got {applied.Count()}");
        Assert(await context.Database.CanConnectAsync(), "SQLite database is not connectable");
    }

    var history = new HistoryRepository(database);
    var transcript = new Transcript
    {
        Text = "portable history",
        TranscribedText = "portable history",
        Status = TranscriptStatus.Completed,
        Duration = 1.25,
        Date = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc)
    };
    await history.AddAsync(transcript);
    Assert((await history.GetAsync(transcript.Id))?.Text == "portable history", "history create/read failed");
    transcript.Text = "updated history";
    Assert(await history.UpdateAsync(transcript), "history update failed");
    Assert((await history.GetAsync(transcript.Id))?.Text == "updated history", "history update did not persist");
    var previousDay = new Transcript
    {
        Text = "previous day",
        Status = TranscriptStatus.Completed,
        Date = new DateTime(2026, 8, 22, 23, 59, 59, DateTimeKind.Utc),
    };
    var nextDay = new Transcript
    {
        Text = "next day",
        Status = TranscriptStatus.Completed,
        Date = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc),
    };
    await history.AddAsync(previousDay);
    await history.AddAsync(nextDay);
    var dateMatches = await history.SearchAsync(
        null,
        new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));
    Assert(dateMatches.Count == 1 && dateMatches[0].Id == transcript.Id,
        "history date range did not use inclusive start and exclusive next-day boundaries");
    Assert(await history.DeleteAsync(previousDay.Id) && await history.DeleteAsync(nextDay.Id),
        "history date-filter fixtures were not cleaned up");

    var historyDetails = new HistoryViewModel(history)
    {
        Selected = new Transcript
        {
            Text = "final canonical text",
            TranscribedText = "raw provider text",
            PostProcessedText = "post-processed text",
            Status = TranscriptStatus.Completed,
        },
    };
    Assert(historyDetails.DetailText == "post-processed text"
        && historyDetails.DetailLabel == "Post-processed transcript"
        && historyDetails.HasRawTranscript,
        "history detail did not prefer the explicit final post-processed value");
    historyDetails.ShowRawTranscript = true;
    Assert(historyDetails.DetailText == "raw provider text"
        && historyDetails.DetailLabel == "Raw transcription",
        "history raw transcript toggle did not use TranscribedText");
    historyDetails.Selected = new Transcript
    {
        Text = "legacy final fallback",
        TranscribedText = " ",
        Status = TranscriptStatus.Completed,
    };
    Assert(!historyDetails.ShowRawTranscript && !historyDetails.HasRawTranscript
        && historyDetails.DetailText == "legacy final fallback"
        && historyDetails.DetailLabel == "Final transcript",
        "history detail did not safely fall back when raw/post-processed text was unavailable");
    historyDetails.StartDate = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
    historyDetails.EndDate = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    await historyDetails.SearchAsync();
    Assert(historyDetails.Status.ErrorCode == "history.date_range_invalid",
        "history accepted an inverted date range");

    var vocabulary = new VocabularyRepository(database);
    var vocabularyItem = new VocabularyItem { Word = "HyperWhisper", Replacement = "HyperWhisper", SortOrder = 2 };
    await vocabulary.AddAsync(vocabularyItem);
    Assert((await vocabulary.ListAsync()).Single().Id == vocabularyItem.Id, "vocabulary CRUD failed");

    var modes = new ModeRepository(database);
    var mode = new Mode { Name = "Portable", SortOrder = 3, IsDefault = true };
    await modes.UpsertAsync(mode);
    Assert((await modes.ListAsync()).Single().Id == mode.Id, "mode create failed");
    mode.Name = "Portable Updated";
    await modes.UpsertAsync(mode);
    Assert((await modes.ListAsync()).Single().Name == "Portable Updated", "mode update failed");

    var files = new MemoryPrivateFileService();
    var settings = new PortableSettingsService(files, Path.Combine(root, "settings.json"));
    settings.Set("language", "en");
    settings.Set("enabled", true);
    settings.Set("localWhisperBackend", "vulkan");
    settings.Set("allowLocalWhisperCpuFallback", false);
    settings.Set("toggleShortcutModifiers", "Control, Alt");
    settings.Set("toggleShortcutKey", "F9");
    settings.Set("pushToTalkMode", "Modifier");
    settings.Set("pushToTalkModifier", "LeftAlt");
    settings.Set("pushToTalkShortcutModifiers", "None");
    settings.Set("pushToTalkShortcutKey", "");
    settings.Set("pushToTalkDoublePressLock", true);
    settings.Set("autoIncreaseMicVolume", true);
    settings.Set("keepMicrophoneWarm", true);
    settings.Set("audioEnvironmentPolicy", "duck");
    settings.Set("textOutput.pasteResultText", false);
    settings.Set("textOutput.removeFillerWords", true);
    settings.Set("textOutput.autocapitalizeInsert", false);
    settings.Set("textOutput.restoreClipboardAfterPaste", false);
    settings.Set("textOutput.clipboardRestoreDelaySeconds", 2.5d);
    Assert(settings.Save().IsSuccess, "settings save failed");
    var reloadedSettings = new PortableSettingsService(files, Path.Combine(root, "settings.json"));
    Assert(reloadedSettings.Load().IsSuccess, "settings load failed");
    Assert(reloadedSettings.Get<string>("language") == "en", "settings value did not round-trip");
    var outputSettings = new SettingsViewModel(reloadedSettings);
    outputSettings.Load();
    Assert(!outputSettings.PasteResultText && outputSettings.RemoveFillerWords
        && !outputSettings.AutocapitalizeInsert && !outputSettings.RestoreClipboardAfterPaste
        && outputSettings.ClipboardRestoreDelaySeconds == 2.5d,
        "text-output settings did not load from their canonical shared keys");
    outputSettings.PasteResultText = true;
    outputSettings.RemoveFillerWords = false;
    outputSettings.AutocapitalizeInsert = true;
    outputSettings.RestoreClipboardAfterPaste = true;
    outputSettings.ClipboardRestoreDelaySeconds = 4.5d;
    outputSettings.Save();
    Assert(reloadedSettings.Get("textOutput.pasteResultText", false)
        && !reloadedSettings.Get("textOutput.removeFillerWords", true)
        && reloadedSettings.Get("textOutput.autocapitalizeInsert", false)
        && reloadedSettings.Get("textOutput.restoreClipboardAfterPaste", false)
        && reloadedSettings.Get("textOutput.clipboardRestoreDelaySeconds", 0d) == 4.5d,
        "text-output settings did not save to their canonical shared keys");

    var backupService = new ApplicationBackupService(database, reloadedSettings);
    var exported = await backupService.ExportAsync();
    Assert(exported.Contains("Portable Updated", StringComparison.Ordinal), "backup omitted modes");
    Assert(exported.Contains("HyperWhisper", StringComparison.Ordinal), "backup omitted vocabulary");
    var exportedSettings = JsonNode.Parse(exported)!["settings"]!["textOutput"]!;
    Assert(exportedSettings["pasteResultText"]!.GetValue<bool>()
        && !exportedSettings["removeFillerWords"]!.GetValue<bool>()
        && exportedSettings["autocapitalizeInsert"]!.GetValue<bool>()
        && exportedSettings["restoreClipboardAfterPaste"]!.GetValue<bool>()
        && exportedSettings["clipboardRestoreDelaySeconds"]!.GetValue<double>() == 4.5d,
        "universal backup omitted canonical text-output settings");
    Assert(!exported.Contains("apiKeys", StringComparison.Ordinal), "backup exported an API-key container without explicit opt-in");
    var nonIntegerVersion = JsonNode.Parse(exported)!.AsObject();
    nonIntegerVersion["schemaVersion"] = "2";
    Assert((await backupService.ImportAsync(nonIntegerVersion.ToJsonString())).Error?.Code == "backup.unsupported_version",
        "non-integer schemaVersion escaped structured validation");
    var extendedBackup = JsonNode.Parse(exported)!.AsObject();
    var extensionSlices = extendedBackup["platformExtensions"]!.AsObject();
    extensionSlices["windows"] = new JsonObject { ["futureWindows"] = 17 };
    extensionSlices["macos"] = new JsonObject { ["futureMac"] = "keep" };
    extensionSlices["linux"]!["futureLinux"] = true;
    exported = extendedBackup.ToJsonString();

    Assert(await history.DeleteAsync(transcript.Id), "history delete failed");
    Assert(await vocabulary.DeleteAsync(vocabularyItem.Id), "vocabulary delete failed");
    Assert(await modes.DeleteAsync(mode.Id), "mode delete failed");
    reloadedSettings.Set("language", "fr");
    files.FailWrites = true;
    Assert((await backupService.ImportAsync(exported)).IsFailure, "backup import ignored a settings write failure");
    Assert(await history.GetAsync(transcript.Id) is null, "failed backup import committed history");
    Assert((await vocabulary.ListAsync()).Count == 0, "failed backup import committed vocabulary");
    Assert((await modes.ListAsync()).Count == 0, "failed backup import committed modes");
    Assert(reloadedSettings.Get<string>("language") == "fr", "failed backup import changed in-memory settings");
    files.FailWrites = false;
    Assert((await backupService.ImportAsync(exported)).IsSuccess, "backup import failed");
    Assert(await history.GetAsync(transcript.Id) is null, "universal-v2 backup unexpectedly invented transcript history");
    Assert((await vocabulary.ListAsync()).Single().Word == "HyperWhisper", "backup did not restore vocabulary");
    Assert((await modes.ListAsync()).Single().Name == "Portable Updated", "backup did not restore modes");
    Assert(reloadedSettings.Get<string>("language") == "en", "backup did not restore settings");
    Assert(reloadedSettings.Get<string>("toggleShortcutKey") == "F9"
        && reloadedSettings.Get<string>("localWhisperBackend") == "vulkan"
        && !reloadedSettings.Get<bool>("allowLocalWhisperCpuFallback")
        && reloadedSettings.Get<string>("pushToTalkMode") == "Modifier"
        && reloadedSettings.Get<bool>("pushToTalkDoublePressLock")
        && reloadedSettings.Get<bool>("autoIncreaseMicVolume")
        && reloadedSettings.Get<bool>("keepMicrophoneWarm")
        && reloadedSettings.Get<string>("audioEnvironmentPolicy") == "duck"
        && reloadedSettings.Get<bool>("textOutput.pasteResultText")
        && !reloadedSettings.Get<bool>("textOutput.removeFillerWords")
        && reloadedSettings.Get<bool>("textOutput.autocapitalizeInsert")
        && reloadedSettings.Get<bool>("textOutput.restoreClipboardAfterPaste")
        && reloadedSettings.Get<double>("textOutput.clipboardRestoreDelaySeconds") == 4.5d,
        "backup did not restore Linux interaction and audio settings");
    var reexported = JsonNode.Parse(await backupService.ExportAsync())!.AsObject()["platformExtensions"]!.AsObject();
    Assert(reexported["windows"]?["futureWindows"]?.GetValue<int>() == 17
        && reexported["macos"]?["futureMac"]?.GetValue<string>() == "keep"
        && reexported["linux"]?["futureLinux"]?.GetValue<bool>() == true,
        "backup did not preserve unknown Linux and foreign platform extensions");
    var malformedSettingsBackup = JsonNode.Parse(await backupService.ExportAsync())!.AsObject();
    malformedSettingsBackup["platformExtensions"]!["linux"]!["settings"]!["language"] = 42;
    var modesBeforeMalformedImport = (await modes.ListAsync()).Select(item => (item.Id, item.Name)).ToArray();
    var malformedResult = await backupService.ImportAsync(malformedSettingsBackup.ToJsonString());
    Assert(malformedResult.IsFailure && malformedResult.Error?.Code == "backup.invalid_settings",
        "malformed Linux setting did not produce a structured failure");
    Assert(reloadedSettings.Get<string>("language") == "en"
        && modesBeforeMalformedImport.SequenceEqual((await modes.ListAsync()).Select(item => (item.Id, item.Name))),
        "malformed Linux setting partially changed settings or database records");

    var externalAudio = Path.Combine(Path.GetDirectoryName(root)!, $"external-{Guid.NewGuid():N}.wav");
    await File.WriteAllBytesAsync(externalAudio, [7, 8, 9]);
    var externalTranscript = new Transcript { Text = "external", Status = TranscriptStatus.Completed, AudioFilePath = externalAudio };
    await history.AddAsync(externalTranscript);
    var safeDeletion = await new HistoryRepository(database, paths).DeleteAsync(externalTranscript.Id, deleteAudio: true);
    Assert(safeDeletion.TranscriptDeleted && !safeDeletion.AudioDeleted && File.Exists(externalAudio),
        "history deletion erased caller-owned external audio");
    File.Delete(externalAudio);

    var sourceAudio = Path.Combine(root, "source.mp3");
    await File.WriteAllBytesAsync(sourceAudio, [7, 8, 9]);
    var fakeNormalizer = new FakeAudioNormalizer();
    var importedAudio = await new DurableAudioImportService(
        new AcceptPrivateFileService(), paths, normalizer: fakeNormalizer).ImportAsync(sourceAudio);
    Assert(importedAudio.IsSuccess && Path.GetExtension(importedAudio.Value!) == ".wav" && fakeNormalizer.Calls == 1,
        "durable audio importer did not delegate to canonical audio normalization");
    var externalNormalized = Path.Combine(Path.GetDirectoryName(root)!, $"external-normalized-{Guid.NewGuid():N}.wav");
    await File.WriteAllBytesAsync(externalNormalized, new byte[48]);
    var escapedImport = await new DurableAudioImportService(
        new AcceptPrivateFileService(), paths, normalizer: new EscapingAudioNormalizer(externalNormalized)).ImportAsync(sourceAudio);
    Assert(escapedImport.Error?.Code == "audio_import.invalid_output_path" && File.Exists(externalNormalized),
        "durable audio importer accepted or deleted a normalizer path outside app-owned storage");
    File.Delete(externalNormalized);

    using (var shell = new HyperWhisper.PortableApplication.ViewModels.ApplicationShellViewModel(
        database, reloadedSettings, localWhisperRuntimeStatus: "Local Whisper (Vulkan; no CPU fallback)"))
    {
        await shell.InitializeAsync();
        Assert(!shell.Status.HasError, $"shell initialization failed: {shell.Status.ErrorCode}");
        foreach (var page in new[] { "home", "history", "vocabulary", "modes", "settings", "backup" })
        {
            shell.Navigate(page);
            Assert(shell.CurrentPage != null && shell.PageTitle.Length > 0, $"navigation failed for {page}");
        }

        await shell.History.DeleteAsync(null);
        Assert(shell.History.Status.ErrorCode == "history.no_selection", "history did not expose a structured selection failure");
        shell.Vocabulary.Word = " ";
        await shell.Vocabulary.AddAsync();
        Assert(shell.Vocabulary.Status.ErrorCode == "vocabulary.word_required", "vocabulary accepted an empty term");
        shell.Modes.Selected = null;
        shell.Modes.Name = "";
        await shell.Modes.SaveAsync();
        Assert(shell.Modes.Status.ErrorCode == "modes.name_required", "modes accepted an empty name");
        shell.Modes.Selected = shell.Modes.Items.First();
        shell.Modes.LocalPostProcessingEnabled = true;
        shell.Modes.LocalPostProcessingModel = "../escape.gguf";
        await shell.Modes.SaveAsync();
        Assert(shell.Modes.Status.ErrorCode == "modes.local_llm_model_required",
            "modes accepted a local LLM path outside the models directory");
        shell.Modes.LocalPostProcessingModel = "local-test.gguf";
        shell.Modes.UserSystemPrompt = "Keep the dictated wording.";
        await shell.Modes.SaveAsync();
        var localMode = (await new ModeRepository(database).ListAsync()).Single();
        Assert(localMode.PostProcessingMode == 2
            && localMode.PostProcessingProvider == "local_llm"
            && localMode.LocalPostProcessingModel == "local-test.gguf"
            && localMode.UserSystemPrompt == "Keep the dictated wording.",
            "mode editor did not persist credential-free local LLM configuration");
        shell.Modes.Selected = null;
        shell.Modes.Name = "Cloud medical";
        shell.Modes.ProviderType = "cloud";
        shell.Modes.CloudProvider = "hyperwhisper";
        shell.Modes.CloudAccuracyTier = "azureMaiTranscribe";
        shell.Modes.CloudDomain = "medical";
        shell.Modes.TranscriptionModel = "mai-1.5";
        shell.Modes.GeminiPrompt = "Verbatim medical transcription";
        shell.Modes.CustomVocabulary = "HyperWhisper, Ray, ray";
        shell.Modes.EnableScreenOcr = true;
        await shell.Modes.SaveAsync();
        var cloudMode = (await new ModeRepository(database).ListAsync()).Single(item => item.Name == "Cloud medical");
        Assert(cloudMode.CloudProvider == "hyperwhisper" && cloudMode.CloudAccuracyTier == "azureMaiTranscribe"
            && cloudMode.CloudTranscriptionDomain == "medical" && cloudMode.CloudTranscriptionModel == "mai-1.5"
            && cloudMode.CustomVocabulary?.Count == 2 && cloudMode.EnableScreenOCR,
            "new cloud mode did not persist routing and context fields");
        cloudMode.ModelType = "linux-model-type";
        cloudMode.IsSystemProvided = true;
        cloudMode.CreatedDate = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        cloudMode.ModifiedDate = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc);
        await new ModeRepository(database).UpsertAsync(cloudMode);
        var linuxModeBackup = JsonNode.Parse(await backupService.ExportAsync())!.AsObject();
        var exportedCloudMode = linuxModeBackup["modes"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["name"]!.GetValue<string>() == "Cloud medical");
        var linuxModeExtension = exportedCloudMode["platformExtensions"]!["linux"]!.AsObject();
        Assert(linuxModeExtension["modelType"]!.GetValue<string>() == "linux-model-type"
            && linuxModeExtension["enableScreenOCR"]!.GetValue<bool>()
            && linuxModeExtension["customVocabulary"]!.AsArray().Count == 2
            && linuxModeExtension["isSystemProvided"]!.GetValue<bool>()
            && linuxModeExtension["createdDate"]!.GetValue<string>().StartsWith("2025-02-03T04:05:06", StringComparison.Ordinal)
            && linuxModeExtension["modifiedDate"]!.GetValue<string>().StartsWith("2026-07-08T09:10:11", StringComparison.Ordinal),
            "Linux mode extension omitted Linux-owned fields");
        cloudMode.ModelType = "mutated"; cloudMode.EnableScreenOCR = false; cloudMode.CustomVocabulary = null;
        cloudMode.IsSystemProvided = false; cloudMode.CreatedDate = DateTime.UtcNow; cloudMode.ModifiedDate = DateTime.UtcNow;
        await new ModeRepository(database).UpsertAsync(cloudMode);
        Assert((await backupService.ImportAsync(linuxModeBackup.ToJsonString())).IsSuccess,
            "Linux mode extension round-trip import failed");
        var restoredLinuxMode = (await new ModeRepository(database).ListAsync()).Single(item => item.Name == "Cloud medical");
        Assert(restoredLinuxMode.ModelType == "linux-model-type" && restoredLinuxMode.EnableScreenOCR
            && restoredLinuxMode.CustomVocabulary?.SequenceEqual(["HyperWhisper", "Ray"]) == true
            && restoredLinuxMode.IsSystemProvided
            && restoredLinuxMode.CreatedDate == new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc)
            && restoredLinuxMode.ModifiedDate == new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc),
            "Linux mode extension fields did not import exactly");
        await shell.Modes.RefreshAsync();
        shell.Modes.Selected = shell.Modes.Items.Single(item => item.Name == "Cloud medical");
        Assert(shell.Modes.CloudProvider == "hyperwhisper" && shell.Modes.CloudDomain == "medical"
            && shell.Modes.CustomVocabulary.Contains("HyperWhisper", StringComparison.Ordinal),
            "cloud mode editor did not reload persisted provider fields");
        var outputRequest = shell.CreateTranscriptionRequest(
            shell.Modes.Selected, cursorContext: PortableCursorContext.MidSentence);
        Assert(outputRequest.VocabularyReplacements?.Count == 1
            && outputRequest.VocabularyReplacements[0].Word == "HyperWhisper"
            && outputRequest.ModeVocabularyReplacements?.Count == 0
            && outputRequest.SelectedMode?.CustomVocabulary?.Contains("Ray") == true
            && outputRequest.CursorContext == PortableCursorContext.MidSentence,
            "request composition confused prompt vocabulary with explicit replacement pairs or lost caret context");
        shell.Modes.ProviderType = "local";
        shell.Modes.LocalEngine = "whisper";
        shell.Modes.TranscriptionModel = "not-in-catalog";
        await shell.Modes.SaveAsync();
        Assert(shell.Modes.Status.ErrorCode == "modes.local_model_invalid", "mode editor accepted an unknown local model ID");
        shell.Settings.LocalLlmBackend = "vulkan";
        shell.Settings.AllowLocalLlmCpuFallback = false;
        Assert(shell.Settings.LocalWhisperBackend == "vulkan"
            && !shell.Settings.AllowLocalWhisperCpuFallback
            && !shell.Settings.WhisperRestartRequired
            && shell.Settings.LocalWhisperRuntimeStatus.Contains("Local Whisper (Vulkan; no CPU fallback)", StringComparison.Ordinal),
            "settings did not load the process-launch Whisper backend baseline");
        shell.Settings.LocalWhisperBackend = "cuda";
        shell.Settings.AllowLocalWhisperCpuFallback = true;
        Assert(shell.Settings.LocalWhisperBackend == "cuda12"
            && shell.Settings.WhisperRestartRequired
            && shell.Settings.LocalWhisperRuntimeStatus.Contains("Restart HyperWhisper", StringComparison.Ordinal),
            "settings did not normalize or report a restart-required Whisper backend change");
        shell.Settings.Save();
        Assert(!shell.Settings.Status.HasError,
            "settings did not persist local inference backend configuration");
        Assert(reloadedSettings.Get<string>("localWhisperBackend") == "cuda12"
            && reloadedSettings.Get<bool>("allowLocalWhisperCpuFallback"),
            "settings did not persist local Whisper backend configuration");
    }

    var credentialStore = new MemoryCredentialStore();
    var credentialViewModel = new HyperWhisper.PortableApplication.ViewModels.CredentialManagementViewModel(credentialStore);
    credentialViewModel.Selected = credentialViewModel.Items.Single(item => item.Account == "OpenAIApiKey");
    credentialViewModel.Secret = "test-secret-never-export";
    await credentialViewModel.SaveAsync();
    Assert(credentialViewModel.Items.Single(item => item.Account == "OpenAIApiKey").IsPresent
        && credentialViewModel.Secret.Length == 0, "credential UI did not save securely or clear its input");
    await credentialViewModel.DeleteAsync();
    Assert(!credentialViewModel.Items.Single(item => item.Account == "OpenAIApiKey").IsPresent,
        "credential UI did not delete the stored credential");
    Assert(credentialViewModel.Items.Any(item => item.Account == "AnthropicApiKey")
        && credentialViewModel.Items.Any(item => item.Account == "CerebrasApiKey")
        && credentialViewModel.Items.All(item => item.Account != "LicenseKey"),
        "credential UI omitted cloud post-processing providers");

    const string accountKey = "account-key-must-never-remain-in-ui";
    var accountStore = new MemoryCredentialStore();
    var accountHttp = new CloudAccountHttpHandler();
    using (var accountService = new PortableCloudAccountService(
        accountStore,
        new HttpClient(accountHttp) { Timeout = Timeout.InfiniteTimeSpan }))
    {
        var opened = new List<Uri>();
        var account = new CloudAccountViewModel(
            accountService,
            new StaticDeviceIdentity(),
            new string('R', 200) + "\nsecret host tail",
            uri => { opened.Add(uri); return PlatformResult.Success(); })
        {
            AccountKey = accountKey,
        };
        await account.ActivateAsync();
        Assert(account.HasAccount && account.AccountState == "Active"
            && account.CustomerEmail == "ray@example.test"
            && account.AccountKey.Length == 0
            && !account.Status.Message.Contains(accountKey, StringComparison.Ordinal),
            "account activation did not populate safe state or clear the submitted key");
        Assert(accountHttp.ValidateDeviceName is { Length: 128 }
            && !accountHttp.ValidateDeviceName.Any(char.IsControl),
            "account activation did not bound and sanitize the device name");
        await account.RefreshCreditsAsync();
        Assert(account.Credits == "42.5" && account.MinutesRemaining == "7",
            "account credit refresh did not update display state");
        await account.OpenPurchaseAsync();
        await account.OpenManageAsync();
        Assert(opened.SequenceEqual(new[] { CloudAccountLinks.Purchase, CloudAccountLinks.ManageAccount })
            && opened.All(uri => string.IsNullOrEmpty(uri.Query)),
            "account view exposed an identifier-bearing or unexpected external URL");
        await account.DeactivateAsync();
        Assert(!account.HasAccount && !accountStore.HasAccount("LicenseKey")
            && account.Status.Message.Contains("does not support remote key revocation", StringComparison.Ordinal),
            "account deactivation did not disclose local-only semantics or remove the local key");
    }

    var blockingAccountHttp = new BlockingCloudAccountHttpHandler();
    using (var blockingAccountService = new PortableCloudAccountService(
        new MemoryCredentialStore(),
        new HttpClient(blockingAccountHttp) { Timeout = Timeout.InfiniteTimeSpan }))
    {
        var gatedAccount = new CloudAccountViewModel(
            blockingAccountService,
            new StaticDeviceIdentity(),
            "Ray Linux",
            _ => PlatformResult.Success()) { AccountKey = "one-at-a-time" };
        var activation = gatedAccount.ActivateAsync();
        await blockingAccountHttp.Started.Task;
        Assert(!gatedAccount.ActivateCommand.CanExecute(null)
            && !gatedAccount.DeactivateCommand.CanExecute(null)
            && !gatedAccount.RefreshCreditsCommand.CanExecute(null),
            "account operation gate allowed overlapping activation, removal, or refresh");
        blockingAccountHttp.Release.TrySetResult();
        await activation;
        Assert(gatedAccount.ActivateCommand.CanExecute(null)
            && gatedAccount.DeactivateCommand.CanExecute(null),
            "account operation gate did not re-enable commands after completion");
    }

    var customModes = new HyperWhisper.PortableApplication.ViewModels.ModesViewModel(
        new ModeRepository(database), reloadedSettings, credentialStore)
    {
        Name = "Custom endpoint",
        ProviderType = "local",
        LocalEngine = "whisper",
        TranscriptionModel = "base",
        PostProcessingMode = "cloud",
        PostProcessingProvider = "custom",
        CustomEndpointName = "Local compatible endpoint",
        CustomEndpointUrl = "http://127.0.0.1:11434/v1/chat/completions",
        CustomEndpointModel = "llama3.2",
        CustomEndpointApiKey = "custom-secret-never-export",
    };
    await customModes.SaveAsync();
    Assert(!customModes.Status.HasError, "custom post-processing endpoint was not saved");
    var customMode = (await new ModeRepository(database).ListAsync())
        .Single(item => item.Name == "Custom endpoint");
    Assert(customMode.PostProcessingMode == 1
        && customMode.PostProcessingProvider?.StartsWith("custom:", StringComparison.Ordinal) == true,
        "mode editor did not persist cloud/custom routing");
    var customEndpoint = reloadedSettings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", [])!.Single();
    Assert(customEndpoint.EndpointUrl == "http://127.0.0.1:11434/v1/chat/completions"
        && credentialStore.HasAccount($"CustomEndpoint_{customEndpoint.Id:D}"),
        "custom endpoint configuration or secure credential was not persisted");
    var endpointCountBeforeFailure = reloadedSettings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", [])!.Length;
    var failingCustomModes = new HyperWhisper.PortableApplication.ViewModels.ModesViewModel(
        new ModeRepository(database), reloadedSettings, new FailWriteCredentialStore())
    {
        Name = "Rejected custom endpoint",
        ProviderType = "local",
        LocalEngine = "whisper",
        TranscriptionModel = "base",
        PostProcessingMode = "cloud",
        PostProcessingProvider = "custom",
        CustomEndpointName = "Rejected endpoint",
        CustomEndpointUrl = "http://127.0.0.1:11435/v1/chat/completions",
        CustomEndpointModel = "test-model",
        CustomEndpointApiKey = "must-not-partially-persist",
    };
    await failingCustomModes.SaveAsync();
    Assert(failingCustomModes.Status.ErrorCode == "modes.save_failed"
        && failingCustomModes.CustomEndpointApiKey == "must-not-partially-persist",
        "secure custom endpoint failure escaped or cleared the retryable UI value");
    Assert(reloadedSettings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", [])!.Length == endpointCountBeforeFailure
        && !(await new ModeRepository(database).ListAsync()).Any(item => item.Name == "Rejected custom endpoint"),
        "failed custom endpoint save left partial settings or mode persistence");

    var delayedModels = new DelayedHttpHandler();
    using (var modelHttp = new HttpClient(delayedModels))
    {
        var modelViewModel = new HyperWhisper.PortableApplication.ViewModels.ModelLibraryViewModel(
            new HyperWhisper.ModelManagement.PortableModelManager(paths, modelHttp));
        var downloadTarget = modelViewModel.Selected!;
        var downloadTask = modelViewModel.DownloadAsync();
        await delayedModels.Started.Task;
        var otherModel = modelViewModel.Items.First(item => item.Id != downloadTarget.Id);
        modelViewModel.Selected = otherModel;
        modelViewModel.Dispose();
        await downloadTask;
        Assert(downloadTarget.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            && otherModel.Status == "Not installed" && !otherModel.Installed,
            "changing model selection redirected an in-flight download result to the wrong model");
    }

    var requestTranscriber = new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("vocabulary routed", "test")));
    var requestAudio = Path.Combine(root, "request-vocabulary.wav");
    await File.WriteAllBytesAsync(requestAudio, [1]);
    using (var requestDevices = new FakeDevices())
    using (var requestRecorder = new FakeRecorder(requestAudio))
    using (var requestWorkflow = new TranscriptionWorkflow(requestRecorder, requestDevices, requestTranscriber, new HistoryRepository(database, paths)))
    using (var requestShell = new HyperWhisper.PortableApplication.ViewModels.ApplicationShellViewModel(database, reloadedSettings, requestWorkflow))
    {
        await requestShell.InitializeAsync();
        var retryTargetMode = requestShell.Modes.Items.Single(item => item.Name == "Cloud medical");
        requestShell.Modes.Selected = requestShell.Modes.Items.First(item => item.Id != retryTargetMode.Id);
        var retryTranscript = new Transcript
        {
            Text = "retry target", Status = TranscriptStatus.Completed, AudioFilePath = requestAudio,
            ModeId = retryTargetMode.Id, Mode = retryTargetMode.Name
        };
        await new HistoryRepository(database, paths).AddAsync(retryTranscript);
        await requestShell.History.RefreshAsync();
        requestShell.History.Selected = requestShell.History.Items.Single(item => item.Id == retryTranscript.Id);
        await requestShell.History.RetryAsync();
        Assert(requestTranscriber.LastRequest?.SelectedMode?.Id == retryTargetMode.Id,
            "history retry used the currently selected mode instead of the transcript's persisted mode");
        requestShell.Recording!.FilePath = requestAudio;
        await requestShell.Recording.TranscribeFileAsync();
        Assert(requestTranscriber.LastRequest?.Vocabulary?.Contains("HyperWhisper") == true,
            "transcription routing request omitted persisted global vocabulary");
    }

    var cancellationSource = Path.Combine(root, "import-cancellation.mp3");
    await File.WriteAllBytesAsync(cancellationSource, [1, 2, 3]);
    var blockingNormalizer = new BlockingAudioNormalizer();
    using (var cancellationDevices = new FakeDevices())
    using (var cancellationRecorder = new FakeRecorder(requestAudio))
    using (var cancellationWorkflow = new TranscriptionWorkflow(
        cancellationRecorder, cancellationDevices, requestTranscriber, new HistoryRepository(database, paths)))
    using (var cancellationViewModel = new HyperWhisper.PortableApplication.ViewModels.TranscriptionWorkflowViewModel(
        cancellationWorkflow,
        () => new TranscriptionWorkflowRequest("en", "Import cancellation"),
        new DurableAudioImportService(new AcceptPrivateFileService(), paths, normalizer: blockingNormalizer)))
    {
        cancellationViewModel.FilePath = cancellationSource;
        var importing = cancellationViewModel.TranscribeFileAsync();
        await blockingNormalizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(cancellationViewModel.IsImporting && cancellationViewModel.CanCancel && !cancellationViewModel.CanTranscribeFile,
            "file normalization did not expose a cancellable busy UI state");
        await cancellationViewModel.CancelAsync();
        await importing;
        Assert(!cancellationViewModel.IsImporting && cancellationViewModel.ErrorCode == "audio_import.cancelled",
            "file normalization cancellation did not restore a terminal UI state");
    }

    var failingHome = new HyperWhisper.PortableApplication.ViewModels.HomeViewModel(
        new HistoryRepository(new ApplicationDb(() => throw new IOException("expected test failure"))),
        new VocabularyRepository(database),
        new ModeRepository(database));
    await failingHome.RefreshAsync();
    Assert(failingHome.Status.ErrorCode == "home.refresh_failed", "repository failure was reported as success");

    Console.WriteLine("HyperWhisper.Application persistence tests passed.");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task RunTranscriptionWorkflowTestsAsync(string root)
{
    Assert(!typeof(TranscriptionWorkflow).GetMethods()
        .Where(method => method.Name == nameof(TranscriptionWorkflow.TranscribeFileAsync))
        .SelectMany(method => method.GetParameters())
        .Any(parameter => parameter.ParameterType == typeof(bool)),
        "public file-transcription API exposed spoofable audio ownership");

    static async Task<(ApplicationDb Database, HistoryRepository History)> CreateStoreAsync(string parent, string name)
    {
        var storeRoot = Path.Combine(parent, name);
        Directory.CreateDirectory(storeRoot);
        var database = new ApplicationDb(new TestPaths(storeRoot));
        await database.MigrateAsync();
        return (database, new HistoryRepository(database));
    }

    var successStore = await CreateStoreAsync(root, "workflow-success");
    var successAudio = Path.Combine(root, "success.wav");
    await File.WriteAllBytesAsync(successAudio, [1, 2, 3]);
    using (var recorder = new FakeRecorder(successAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("portable words", "Test Whisper"))),
        successStore.History))
    {
        var observedStates = new List<TranscriptionWorkflowState>();
        workflow.Changed += (_, _) => throw new InvalidOperationException("simulated subscriber failure");
        workflow.Changed += (_, _) => observedStates.Add(workflow.Snapshot.State);
        workflow.RefreshDevices();
        var started = await workflow.StartRecordingAsync();
        Assert(started.IsSuccess, "recording start did not report unambiguous success");
        Assert(workflow.Snapshot.State == TranscriptionWorkflowState.Recording, "recording did not enter Recording state");
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest("en", "Test", Guid.NewGuid()));
        Assert(result.IsSuccess, "successful recording transcription failed");
        var saved = await successStore.History.ListAsync();
        Assert(saved.Count == 1 && saved[0].Text == "portable words" && saved[0].Status == TranscriptStatus.Completed,
            "successful transcription was not persisted exactly once");
        Assert(observedStates.Contains(TranscriptionWorkflowState.Completed), "completion transition was not notified");
        var fileResult = await workflow.TranscribeFileAsync(successAudio, new TranscriptionWorkflowRequest("en", "File test"));
        Assert(fileResult.IsSuccess && (await successStore.History.ListAsync()).Count == 2,
            "successful file transcription was not persisted");
    }

    var outputStore = await CreateStoreAsync(root, "workflow-speech-output");
    var outputAudio = Path.Combine(root, "speech-output.wav");
    await File.WriteAllBytesAsync(outputAudio, [1]);
    var outputMode = new Mode
    {
        Name = "Output parity",
        PostProcessingMode = 0,
        RemoveTrailingPeriod = true,
        Punctuation = true,
        Capitalization = true,
    };
    using (var recorder = new FakeRecorder(outputAudio))
    using (var devices = new FakeDevices())
    using (var injection = new FakeTextInjection())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success(
            "Um, I think API uses new line hyper whisper.", "Test Whisper"))),
        outputStore.History,
        textInjection: injection))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest(
            Language: "en",
            SelectedMode: outputMode,
            VocabularyReplacements: [new("hyper whisper", "HyperWhisper")],
            ModeVocabularyReplacements: [new("uses", "USES")],
            OutputOptions: new SpeechOutputProcessingOptions(
                RemoveFillerWords: true,
                RemoveTrailingPeriod: true,
                AutocapitalizeInsert: true),
            CursorContext: PortableCursorContext.MidSentence));
        const string transcriptText = "I think API USES \n\n HyperWhisper.";
        Assert(result.IsSuccess && result.RawText == "Um, I think API uses new line hyper whisper."
            && result.Text == transcriptText,
            "batch workflow did not preserve raw text or apply ordered filler/voice/global/mode output processing");
        Assert(injection.LastText == "I think API USES \n\n HyperWhisper ",
            "batch injection did not use injection-only period/spacing/autocapitalization output");
        var saved = (await outputStore.History.ListAsync()).Single();
        Assert(saved.Text == transcriptText && saved.TranscribedText == result.RawText,
            "history did not preserve transcript output separately from raw provider text");
    }

    var copyStore = await CreateStoreAsync(root, "workflow-copy-only");
    using (var recorder = new FakeRecorder(outputAudio))
    using (var devices = new FakeDevices())
    using (var injection = new FakeTextInjection())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(
            PortableTranscriptionResult.Success("I think API works.", "Test Whisper"))),
        copyStore.History,
        textInjection: injection))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest(
            Language: "en",
            SelectedMode: outputMode,
            OutputOptions: new SpeechOutputProcessingOptions(
                RemoveFillerWords: false,
                RemoveTrailingPeriod: true,
                AutocapitalizeInsert: true),
            PasteResultText: false,
            CursorContext: PortableCursorContext.MidSentence));
        Assert(result.IsSuccess && result.InjectionOutcome == TextInjectionOutcome.CopiedToClipboard
            && injection.CallCount == 0 && injection.CopyCallCount == 1
            && injection.LastCopiedText == "I think API works ",
            "auto-paste off did not copy injection output without injecting");
        Assert((await copyStore.History.ListAsync()).Single().Text == "I think API works.",
            "auto-paste off did not save completed transcript history");
    }

    var postStore = await CreateStoreAsync(root, "workflow-postprocessing");
    var postAudio = Path.Combine(root, "post.wav");
    await File.WriteAllBytesAsync(postAudio, [1]);
    var localMode = new Mode
    {
        Name = "Local cleanup",
        PostProcessingMode = 2,
        PostProcessingProvider = "local_llm",
        LocalPostProcessingModel = "test.gguf",
    };
    var cloudMode = new Mode
    {
        Name = "Cloud cleanup",
        PostProcessingMode = 1,
        PostProcessingProvider = "anthropic",
        LanguageModel = "claude-haiku-4-5",
    };
    var appliedPostProcessor = new FakePostProcessor((text, _, _) => Task.FromResult(
        PortablePostProcessingResult.Applied("Cleaned words", "Local LLM · test.gguf · cpu")));
    var modeAwareTranscriber = new FakeTranscriber((_, _, _) => Task.FromResult(
        PortableTranscriptionResult.Success("raw words", "Test Whisper")));
    using var appliedInjection = new FakeTextInjection();
    using (var recorder = new FakeRecorder(postAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        modeAwareTranscriber,
        postStore.History,
        appliedPostProcessor,
        appliedInjection))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var result = await workflow.StopAndTranscribeAsync(
            new TranscriptionWorkflowRequest("en", localMode.Name, localMode.Id, localMode));
        Assert(result.IsSuccess && result.Text == "Cleaned words" && result.RawText == "raw words",
            "applied post-processing result did not preserve raw output");
        var saved = (await postStore.History.ListAsync()).Single();
        Assert(saved.Status == TranscriptStatus.Completed
            && saved.Text == "Cleaned words"
            && saved.TranscribedText == "raw words"
            && saved.PostProcessedText == "Cleaned words"
            && saved.PostProcessingProvider == "Local LLM · test.gguf · cpu",
            "post-processing fields were not persisted with Windows semantics");
        Assert(appliedPostProcessor.CallCount == 1, "enabled local post-processing was not invoked exactly once");
        Assert(modeAwareTranscriber.LastRequest?.SelectedMode == localMode,
            "selected mode was not passed safely to the transcription backend");
        Assert(appliedInjection.LastText == "Cleaned words ",
            "text injection ran before local post-processing or received raw text");
        Assert(result.InjectionOutcome == TextInjectionOutcome.Pasted
            && workflow.Snapshot.Message == "Transcription pasted and saved to history",
            "successful paste outcome was not surfaced honestly");

        var cloudResult = await workflow.TranscribeFileAsync(
            postAudio, new TranscriptionWorkflowRequest("en", cloudMode.Name, cloudMode.Id, cloudMode));
        Assert(cloudResult.IsSuccess && appliedPostProcessor.CallCount == 2,
            "enabled cloud post-processing was not routed through the shared processor");

        var injectionCalls = appliedInjection.CallCount;
        var fileResult = await workflow.TranscribeFileAsync(
            postAudio,
            new TranscriptionWorkflowRequest("en", localMode.Name, localMode.Id, localMode));
        Assert(fileResult.IsSuccess && fileResult.InjectionOutcome is null
            && appliedInjection.CallCount == injectionCalls,
            "file transcription injected into the active application");
    }

    foreach (var (outcome, expectedMessage) in new[]
    {
        (TextInjectionOutcome.Pasted, "Transcription pasted and saved to history"),
        (TextInjectionOutcome.CopiedToClipboard, "Transcription copied to clipboard and saved to history"),
        (TextInjectionOutcome.SecureFieldSkipped, "Transcription saved; secure field was not modified"),
        (TextInjectionOutcome.Failed, "Transcription saved, but text injection failed"),
    })
    {
        var outcomeStore = await CreateStoreAsync(root, $"workflow-injection-{outcome}");
        var outcomeAudio = Path.Combine(root, $"injection-{outcome}.wav");
        await File.WriteAllBytesAsync(outcomeAudio, [1]);
        using var recorder = new FakeRecorder(outcomeAudio);
        using var devices = new FakeDevices();
        using var injection = new FakeTextInjection(outcome);
        using var workflow = new TranscriptionWorkflow(
            recorder,
            devices,
            new FakeTranscriber((_, _, _) => Task.FromResult(
                PortableTranscriptionResult.Success("injected words", "Test Whisper"))),
            outcomeStore.History,
            textInjection: injection);
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest());
        Assert(result.IsSuccess && result.InjectionOutcome == outcome,
            $"{outcome} injection outcome was not returned");
        Assert(workflow.Snapshot.Message == expectedMessage,
            $"{outcome} injection status was not surfaced honestly");
        Assert((await outcomeStore.History.ListAsync()).Single().Status == TranscriptStatus.Completed,
            $"{outcome} injection outcome incorrectly invalidated transcription");
    }

    var fallbackStore = await CreateStoreAsync(root, "workflow-postprocessing-fallback");
    var fallbackAudio = Path.Combine(root, "post-fallback.wav");
    await File.WriteAllBytesAsync(fallbackAudio, [1]);
    foreach (var failure in new[]
    {
        PortablePostProcessingResult.Skipped("raw fallback", "postprocessing.failed", "expected failure"),
        PortablePostProcessingResult.Skipped("raw fallback", "postprocessing.cancelled", "expected cancellation"),
        new PortablePostProcessingResult("", true, "Local LLM"),
    })
    {
        using var recorder = new FakeRecorder(fallbackAudio);
        using var devices = new FakeDevices();
        using var workflow = new TranscriptionWorkflow(
            recorder,
            devices,
            new FakeTranscriber((_, _, _) => Task.FromResult(
                PortableTranscriptionResult.Success("raw fallback", "Test Whisper"))),
            fallbackStore.History,
            new FakePostProcessor((_, _, _) => Task.FromResult(failure)));
        workflow.RefreshDevices();
        var result = await workflow.TranscribeFileAsync(
            fallbackAudio,
            new TranscriptionWorkflowRequest("en", localMode.Name, localMode.Id, localMode));
        Assert(result.IsSuccess && result.Text == "raw fallback" && result.PostProcessedText is null,
            "post-processing failure/cancellation did not preserve raw transcription");
    }
    Assert((await fallbackStore.History.ListAsync()).All(item =>
        item.Status == TranscriptStatus.Completed
        && item.Text == "raw fallback"
        && item.TranscribedText == "raw fallback"
        && item.PostProcessedText is null
        && item.PostProcessingProvider is null),
        "fallback history claimed a fake post-processing completion");

    var persistenceStore = await CreateStoreAsync(root, "workflow-persistence-failure");
    await using (var context = persistenceStore.Database.CreateContext())
    {
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_transcript BEFORE INSERT ON Transcripts BEGIN SELECT RAISE(ABORT, 'simulated persistence failure'); END;");
    }
    var persistenceAudio = Path.Combine(root, "persistence.wav");
    await File.WriteAllBytesAsync(persistenceAudio, [1]);
    using (var recorder = new FakeRecorder(persistenceAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("valid backend text", "Test Whisper"))),
        persistenceStore.History))
    {
        workflow.RefreshDevices();
        var result = await workflow.TranscribeFileAsync(persistenceAudio, new TranscriptionWorkflowRequest());
        Assert(!result.IsSuccess && workflow.Snapshot.State == TranscriptionWorkflowState.Failed
            && workflow.Snapshot.ErrorCode == "workflow.persistence_failed",
            "persistence failure after backend success was not classified or exposed correctly");
        Assert((await persistenceStore.History.ListAsync()).Count == 0, "persistence failure created fake completed history");
    }

    var failureStore = await CreateStoreAsync(root, "workflow-failure");
    var failureAudio = Path.Combine(root, "failure.wav");
    await File.WriteAllBytesAsync(failureAudio, [1]);
    using (var recorder = new FakeRecorder(failureAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Failed(
            PortableTranscriptionErrorCode.TranscriptionFailed,
            "expected backend rejection"))),
        failureStore.History))
    {
        var failedWasNotified = false;
        workflow.Changed += (_, _) => failedWasNotified |= workflow.Snapshot.State == TranscriptionWorkflowState.Failed;
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest());
        Assert(!result.IsSuccess && failedWasNotified, "backend result failure transition was not visible");
        var failed = (await failureStore.History.ListAsync()).Single();
        Assert(failed.Status == TranscriptStatus.Failed
            && failed.FailedReason == "expected backend rejection"
            && failed.AudioFilePath == failureAudio,
            "backend failure did not persist a terminal retryable history row");
        Assert(File.Exists(failureAudio), "owned audio was deleted after a non-cancel failure");
    }

    var exceptionStore = await CreateStoreAsync(root, "workflow-exception");
    var exceptionAudio = Path.Combine(root, "exception.wav");
    await File.WriteAllBytesAsync(exceptionAudio, [1]);
    using (var recorder = new FakeRecorder(exceptionAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => throw new InvalidOperationException("simulated backend exception")),
        exceptionStore.History))
    {
        workflow.RefreshDevices();
        var result = await workflow.TranscribeFileAsync(exceptionAudio, new TranscriptionWorkflowRequest());
        Assert(!result.IsSuccess && workflow.Snapshot.ErrorCode == "workflow.backend_failed",
            "backend exception was misclassified as a persistence failure");
        var failed = (await exceptionStore.History.ListAsync()).Single();
        Assert(failed.Status == TranscriptStatus.Failed && failed.FailedReason?.Length > 0,
            "backend exception did not terminate its processing history row");
        Assert(File.Exists(exceptionAudio), "external source was deleted after backend failure");
    }

    var cancellationStore = await CreateStoreAsync(root, "workflow-cancel");
    var cancellationAudio = Path.Combine(root, "cancel.wav");
    await File.WriteAllBytesAsync(cancellationAudio, [1]);
    var transcriptionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var recorder = new FakeRecorder(cancellationAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber(async (_, _, cancellationToken) =>
        {
            transcriptionEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PortableTranscriptionResult.Success("unreachable", "Test Whisper");
        }),
        cancellationStore.History))
    {
        workflow.RefreshDevices();
        var task = workflow.TranscribeFileAsync(cancellationAudio, new TranscriptionWorkflowRequest());
        await transcriptionEntered.Task;
        await workflow.CancelAsync();
        var result = await task;
        Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled, "file transcription cancellation was not structured");
        Assert((await cancellationStore.History.ListAsync()).Count == 0, "cancelled transcription created fake history");
    }

    var ownedSuccessStore = await CreateStoreAsync(root, "workflow-owned-import-success");
    var ownedSuccessAudio = Path.Combine(root, "owned-success.wav");
    await File.WriteAllBytesAsync(ownedSuccessAudio, [1]);
    using (var recorder = new FakeRecorder(ownedSuccessAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(
            PortableTranscriptionResult.Success("owned import", "Test Whisper"))),
        ownedSuccessStore.History))
    {
        workflow.RefreshDevices();
        var result = await workflow.TranscribeOwnedFileAsync(
            ownedSuccessAudio, new TranscriptionWorkflowRequest());
        Assert(result.IsSuccess && File.Exists(ownedSuccessAudio)
            && (await ownedSuccessStore.History.ListAsync()).Single().AudioFilePath == ownedSuccessAudio,
            "completed app-owned import was not retained for history");
    }

    var ownedFailureStore = await CreateStoreAsync(root, "workflow-owned-import-failure");
    var ownedFailureAudio = Path.Combine(root, "owned-failure.wav");
    await File.WriteAllBytesAsync(ownedFailureAudio, [1]);
    using (var recorder = new FakeRecorder(ownedFailureAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Failed(
            PortableTranscriptionErrorCode.TranscriptionFailed, "expected owned failure"))),
        ownedFailureStore.History))
    {
        workflow.RefreshDevices();
        var result = await workflow.TranscribeOwnedFileAsync(
            ownedFailureAudio, new TranscriptionWorkflowRequest());
        var failed = (await ownedFailureStore.History.ListAsync()).Single();
        Assert(!result.IsSuccess && !File.Exists(ownedFailureAudio) && failed.AudioFilePath is null,
            "terminal app-owned import failure orphaned audio or left a dangling history path");
    }

    var ownedCancellationStore = await CreateStoreAsync(root, "workflow-owned-import-cancel");
    var ownedCancellationAudio = Path.Combine(root, "owned-cancel.wav");
    await File.WriteAllBytesAsync(ownedCancellationAudio, [1]);
    var ownedTranscriptionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var recorder = new FakeRecorder(ownedCancellationAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber(async (_, _, cancellationToken) =>
        {
            ownedTranscriptionEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PortableTranscriptionResult.Success("unreachable", "Test Whisper");
        }),
        ownedCancellationStore.History))
    {
        workflow.RefreshDevices();
        var task = workflow.TranscribeOwnedFileAsync(
            ownedCancellationAudio, new TranscriptionWorkflowRequest());
        await ownedTranscriptionEntered.Task;
        await workflow.CancelAsync();
        var result = await task;
        Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled
            && !File.Exists(ownedCancellationAudio)
            && (await ownedCancellationStore.History.ListAsync()).Count == 0,
            "cancelled app-owned import orphaned normalized audio or history");
    }

    var cleanupStore = await CreateStoreAsync(root, "workflow-cancel-cleanup-failure");
    await using (var context = cleanupStore.Database.CreateContext())
    {
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_cancel_delete BEFORE DELETE ON Transcripts BEGIN SELECT RAISE(ABORT, 'simulated delete failure'); END;");
    }
    var cleanupAudio = Path.Combine(root, "cancel-cleanup.wav");
    await File.WriteAllBytesAsync(cleanupAudio, [1]);
    var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var recorder = new FakeRecorder(cleanupAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber(async (_, _, cancellationToken) =>
        {
            cleanupEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PortableTranscriptionResult.Success("unreachable", "Test Whisper");
        }),
        cleanupStore.History))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var task = workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest());
        await cleanupEntered.Task;
        await workflow.CancelAsync();
        var result = await task;
        var retained = (await cleanupStore.History.ListAsync()).Single();
        Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled,
            "cleanup failure changed structured cancellation result");
        Assert(retained.Status == TranscriptStatus.Failed
            && retained.FailedReason == "Cancellation cleanup did not finish",
            "failed cancellation deletion left a Processing row");
        Assert(File.Exists(cleanupAudio),
            "failed cancellation deletion created a dangling history audio path");
    }

    var falseDeleteStore = await CreateStoreAsync(root, "workflow-cancel-delete-false");
    var falseDeleteAudio = Path.Combine(root, "cancel-delete-false.wav");
    await File.WriteAllBytesAsync(falseDeleteAudio, [1]);
    var falseDeleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var recorder = new FakeRecorder(falseDeleteAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber(async (_, _, cancellationToken) =>
        {
            falseDeleteEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PortableTranscriptionResult.Success("unreachable", "Test Whisper");
        }),
        new FalseDeleteHistoryStore(falseDeleteStore.History)))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var task = workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest());
        await falseDeleteEntered.Task;
        await workflow.CancelAsync();
        await task;
        var retained = (await falseDeleteStore.History.ListAsync()).Single();
        Assert(retained.Status == TranscriptStatus.Failed && File.Exists(falseDeleteAudio),
            "false cancellation deletion left a dangling or Processing history row");
    }

    var raceStore = await CreateStoreAsync(root, "workflow-stop-race");
    var raceAudio = Path.Combine(root, "race.wav");
    await File.WriteAllBytesAsync(raceAudio, [1]);
    using (var recorder = new FakeRecorder(raceAudio) { BlockStop = true })
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("should cancel", "Test Whisper"))),
        raceStore.History))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var stopping = Task.Run(() => workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest()));
        await recorder.StopEntered.Task;
        await workflow.CancelAsync();
        Assert(recorder.StopCount == 1, "cancel raced with stop and called recorder.Stop twice");
        recorder.ReleaseStop.Set();
        var result = await stopping;
        Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled, "stop/cancel race did not cancel transcription");
        Assert(recorder.StopCount == 1, "stop/cancel race called recorder.Stop twice after completion");
        Assert((await raceStore.History.ListAsync()).Count == 0, "stop/cancel race created fake history");
    }

    var orphanStore = await CreateStoreAsync(root, "workflow-orphan");
    var orphan = new Transcript { Status = TranscriptStatus.Processing, AudioFilePath = "/tmp/orphan.wav" };
    await orphanStore.History.AddAsync(orphan);
    Assert(await orphanStore.History.FailOrphanedProcessingAsync() == 1,
        "orphan processing safety net did not find the row");
    var repaired = await orphanStore.History.GetAsync(orphan.Id);
    Assert(repaired?.Status == TranscriptStatus.Failed && repaired.FailedReason?.Length > 0,
        "orphan processing row remained non-terminal");
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
        if (FailWrites)
            return PlatformResult.Failure("test.write_failed", "The simulated private write failed.");
        _files[path] = contents.ToArray();
        return PlatformResult.Success();
    }

    public PlatformResult WriteAllTextAtomically(string path, string contents)
        => WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));

    public PlatformResult<byte[]?> ReadAllBytes(string path)
        => PlatformResult<byte[]?>.Success(_files.TryGetValue(path, out var value) ? value.ToArray() : null);

    public PlatformResult<string?> ReadAllText(string path)
        => PlatformResult<string?>.Success(
            _files.TryGetValue(path, out var value) ? Encoding.UTF8.GetString(value) : null);

    public PlatformResult Delete(string path)
    {
        _files.Remove(path);
        return PlatformResult.Success();
    }

    public PlatformResult<bool> IsRestrictedToCurrentUser(string path)
        => PlatformResult<bool>.Success(_files.ContainsKey(path));
}

file sealed class AcceptPrivateFileService : IPrivateFileService
{
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents) => PlatformResult.Success();
    public PlatformResult WriteAllTextAtomically(string path, string contents) => PlatformResult.Success();
    public PlatformResult<byte[]?> ReadAllBytes(string path) => PlatformResult<byte[]?>.Success(null);
    public PlatformResult<string?> ReadAllText(string path) => PlatformResult<string?>.Success(null);
    public PlatformResult Delete(string path) { if (File.Exists(path)) File.Delete(path); return PlatformResult.Success(); }
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(File.Exists(path));
}

file sealed class FakeAudioNormalizer : IAudioNormalizationService
{
    public int Calls { get; private set; }

    public async Task<PlatformResult<string>> NormalizeAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, $"import-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(destination, new byte[48], cancellationToken);
        progress?.Report(new AudioNormalizationProgress("complete", 3, 3, 1));
        return PlatformResult<string>.Success(destination);
    }
}

file sealed class BlockingAudioNormalizer : IAudioNormalizationService
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<PlatformResult<string>> NormalizeAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new AudioNormalizationProgress("staging", 1, 3, 0.03));
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

file sealed class EscapingAudioNormalizer(string path) : IAudioNormalizationService
{
    public Task<PlatformResult<string>> NormalizeAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<string>.Success(path));
}

file sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<(string Resource, string Account), byte[]> _values = [];
    public PlatformResult<byte[]?> Read(string resource, string account) => PlatformResult<byte[]?>.Success(
        _values.TryGetValue((resource, account), out var value) ? value.ToArray() : null);
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value)
    { _values[(resource, account)] = value.ToArray(); return PlatformResult.Success(); }
    public PlatformResult Delete(string resource, string account)
    { _values.Remove((resource, account)); return PlatformResult.Success(); }
    public bool HasAccount(string account) => _values.ContainsKey(("HyperWhisper", account));
}

file sealed class FailWriteCredentialStore : ICredentialStore
{
    public PlatformResult<byte[]?> Read(string resource, string account) => PlatformResult<byte[]?>.Success(null);
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value) =>
        PlatformResult.Failure("credentials.write_failed", "simulated secure store failure");
    public PlatformResult Delete(string resource, string account) => PlatformResult.Success();
}

file sealed class StaticDeviceIdentity : IDeviceIdentityProvider
{
    public PlatformResult<DeviceIdentity> GetDeviceIdentity() => PlatformResult<DeviceIdentity>.Success(
        new DeviceIdentity("privacy-preserving-device-id", DeviceIdentitySource.StoredFallback));
}

file sealed class CloudAccountHttpHandler : HttpMessageHandler
{
    public string? ValidateDeviceName { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/api/license/validate")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ValidateDeviceName = body.RootElement.GetProperty("device_name").GetString();
            return Json("{\"valid\":true,\"customer_id\":\"customer-1\",\"customer_email\":\"ray@example.test\",\"expires_at\":\"2030-01-02T03:04:05Z\"}");
        }
        if (path == "/usage")
            return Json("{\"credits_remaining\":42.5,\"minutes_remaining\":7,\"credits_per_minute\":5.5,\"is_licensed\":true,\"is_anonymous\":false}");
        if (path == "/api/license/deactivate")
            return Json("{\"success\":true}");
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

file sealed class BlockingCloudAccountHttpHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"valid\":true}", Encoding.UTF8, "application/json"),
        };
    }
}

file sealed class DelayedHttpHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

file sealed class FakeDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([new AudioInputDevice("test", "Test microphone", true)]);
    public void Dispose() { }
}

file sealed class FakeRecorder(string outputPath) : IAudioRecorder
{
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(1.5);
    public bool BlockStop { get; init; }
    public int StopCount { get; private set; }
    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim ReleaseStop { get; } = new(initialState: false);
    public PlatformResult Start(AudioRecordingOptions options)
    {
        IsRecording = true;
        return PlatformResult.Success();
    }
    public PlatformResult<string> Stop()
    {
        StopCount++;
        StopEntered.TrySetResult();
        if (BlockStop) ReleaseStop.Wait(TimeSpan.FromSeconds(5));
        IsRecording = false;
        return PlatformResult<string>.Success(outputPath);
    }
    public void Dispose()
    {
        ReleaseStop.Set();
        ReleaseStop.Dispose();
    }
}

file sealed class FakeTranscriber(
    Func<string, string?, CancellationToken, Task<PortableTranscriptionResult>> transcribe) : IRecordedAudioTranscriber
{
    public TranscriptionWorkflowRequest? LastRequest { get; private set; }
    public TranscriptionBackendCapability Capability { get; } = new(true, "Test Whisper");
    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return transcribe(audioPath, request.Language, cancellationToken);
    }
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken = default) =>
        transcribe(audioPath, language, cancellationToken);
}

file sealed class FakePostProcessor(
    Func<string, Mode, CancellationToken, Task<PortablePostProcessingResult>> process) : ITranscriptionPostProcessor
{
    public int CallCount { get; private set; }
    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return process(transcript, mode, cancellationToken);
    }
}

file sealed class FalseDeleteHistoryStore(HistoryRepository inner) : ITranscriptionHistoryStore
{
    public Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.GetAsync(id, cancellationToken);
    public Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default) =>
        inner.AddAsync(transcript, cancellationToken);
    public Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default) =>
        inner.UpdateAsync(transcript, cancellationToken);
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

file sealed class FakeTextInjection(TextInjectionOutcome outcome = TextInjectionOutcome.Pasted) : ITextInjectionService
{
    public string? LastText { get; private set; }
    public string? LastCopiedText { get; private set; }
    public int CallCount { get; private set; }
    public int CopyCallCount { get; private set; }
    public bool IsCapturedTargetAvailable => true;
    public void CaptureTarget() { }
    public void StartSession() { }
    public void EndSession() { }
    public void CancelPendingClipboardRestore() { }
    public void ScheduleClipboardRestore(TimeSpan delay) { }
    public ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlatformResult.Success());
    public ValueTask<PlatformResult> CopyToClipboardAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        LastCopiedText = text;
        CopyCallCount++;
        return ValueTask.FromResult(PlatformResult.Success());
    }
    public ValueTask<TextInjectionOutcome> InjectTranscriptAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        LastText = text;
        CallCount++;
        return ValueTask.FromResult(outcome);
    }
    public void Dispose() { }
}
