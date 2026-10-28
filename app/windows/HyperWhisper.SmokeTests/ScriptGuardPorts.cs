// SCRIPT GUARD PORTS (issue #830)
//
// app/windows/HyperWhisper/scripts/ held 22 verify_*.ps1/.py guard scripts that
// no workflow ran. The behavioural rules among them are ported here so they run
// in windows-ci with the rest of the smoke suite. Each check calls the real
// product method; none reads source text. Program.Main calls RunAll once, inside
// its scratch profile (AppPaths override), so every file made here sits in a
// temp folder and is removed again.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using HyperWhisper.AppClassification;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.LocalApi;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Services.Transcription;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels;
using HyperWhisper.Views.Windows;
using Microsoft.EntityFrameworkCore;
using AppContextInfo = HyperWhisper.Services.ApplicationContext;

namespace HyperWhisper.SmokeTests;

internal static class ScriptGuardPorts
{
    private const BindingFlags InstanceField = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll(Action<string, Action> run)
    {
        run("history: audio deletion stays inside the trusted recording roots (#830)", () =>
        {
            // A transcript row carries a path. Deleting the row must never delete a
            // file the app does not own (a restored backup or a hand-edited DB can
            // point anywhere), but must still clean up the app's own recordings.
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();
            var history = HistoryService.Instance;
            var id = Guid.NewGuid().ToString("N");
            var recordings = StorageService.Instance.GetRecordingsFolder();
            // The sibling and ".." fixtures sit next to the recordings folder, so it
            // must be the scratch profile's own.
            Assert(Path.GetFullPath(recordings).StartsWith(Path.GetFullPath(AppPaths.AppDataRoot), StringComparison.OrdinalIgnoreCase),
                $"the recordings folder {recordings} is outside the scratch profile");
            var outsideDir = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"outside-{id}");
            var siblingDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordings)) + "-sibling-" + id;
            var trusted = new[]
            {
                Path.Combine(recordings, $"g830-{id}.wav"),
                Path.Combine(SettingsService.GetLegacyAudioFolder(), $"g830-{id}.wav"),
                Path.Combine(AppPaths.ProfileTempRecordingsDirectory, $"g830-{id}.wav"),
                Path.Combine(Path.GetTempPath(), $"hyperwhisper_{id}.wav"),
            };
            var untrusted = new[]
            {
                Path.Combine(outsideDir, "outside.wav"),
                Path.Combine(siblingDir, "sibling-prefix.wav"),
                Path.Combine(outsideDir, $"hyperwhisper_{id}.wav"),
                Path.Combine(Path.GetTempPath(), $"hyperwhisper_{id}.txt"),
            };
            var traversal = Path.Combine(recordings, "..", $"traversal-{id}.wav");
            var trimmed = Path.Combine(recordings, $"g830-{id}.trimmed.wav");
            Transcript? row = null;
            try
            {
                foreach (var path in trusted.Concat(untrusted).Append(traversal).Append(trimmed))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                }

                foreach (var path in trusted)
                    Assert(HistoryService.IsDeletableAudioPath(path), $"a recording the app owns is not deletable: {path}");
                foreach (var path in untrusted.Append(traversal))
                    Assert(!HistoryService.IsDeletableAudioPath(path), $"a file outside the recording roots is deletable: {path}");

                foreach (var path in untrusted.Append(traversal))
                    history.DeleteAudioFile(path);
                foreach (var path in untrusted.Append(traversal))
                    Assert(File.Exists(path), $"DeleteAudioFile removed a file outside the recording roots: {path}");
                foreach (var path in trusted)
                    history.DeleteAudioFile(path);
                foreach (var path in trusted)
                    Assert(!File.Exists(path), $"DeleteAudioFile left a trusted recording behind: {path}");

                // Deleting a row runs both its paths through the guard: the trimmed
                // copy (trusted) goes, the original (outside) stays.
                row = history.CreateProcessingTranscript(1.0, "g830-delete-guard", untrusted[0]);
                row.TrimmedAudioFilePath = trimmed;
                row.Status = TranscriptStatus.Completed;
                row.Text = "g830";
                history.UpdateTranscript(row);
                var deleted = history.DeleteTranscript(row.Id);
                // The Error is the localized toast copy; the Exception is the cause (#2403).
                Assert(deleted.IsSuccess && deleted.Value,
                    $"the row was not deleted: {deleted.Error} (cause: {DescribeCause(deleted.Exception)})");
                row = null;
                Assert(!File.Exists(trimmed), "deleting the row left its trusted trimmed audio behind");
                Assert(File.Exists(untrusted[0]), "deleting the row removed audio outside the recording roots");
            }
            finally
            {
                if (row != null) history.DeleteTranscript(row.Id);
                foreach (var path in trusted.Concat(untrusted).Append(traversal).Append(trimmed))
                    TryDeleteFile(path);
                TryDeleteDirectory(outsideDir);
                TryDeleteDirectory(siblingDir);
            }
        });

        run("local api: only a socket bind failure on a remembered port retries on an ephemeral port (#830)", () =>
        {
            // A remembered port another app took would otherwise stop the Local API
            // for good; any other startup failure must still surface as fatal.
            const int remembered = 48123;
            var accessDenied = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied);
            var inUse = new IOException("Failed to bind",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse));

            Assert(LocalApiBindFallback.ShouldRetryWithEphemeral(accessDenied, remembered),
                "a bare WSAEACCES on the remembered port did not retry");
            Assert(LocalApiBindFallback.ShouldRetryWithEphemeral(inUse, remembered),
                "an IOException wrapping WSAEADDRINUSE on the remembered port did not retry");
            Assert(LocalApiBindFallback.ShouldRetryWithEphemeral(
                    new InvalidOperationException("An attempt was made to access a socket in a way forbidden by its access permissions"), remembered),
                "Kestrel's access-permissions message did not retry");
            Assert(LocalApiBindFallback.ShouldRetryWithEphemeral(new InvalidOperationException("address already in use"), remembered),
                "Kestrel's address-in-use message did not retry");
            Assert(!LocalApiBindFallback.ShouldRetryWithEphemeral(accessDenied, 0),
                "a failure on an ephemeral port retried again (it would loop)");
            Assert(!LocalApiBindFallback.ShouldRetryWithEphemeral(new InvalidOperationException("route registration failed"), remembered),
                "a non-bind startup failure was retried instead of failing");
        });

        run("transcription: a repeated-phrase loop collapses to one copy, ordinary text is untouched (#830)", () =>
        {
            // Whisper can loop on long audio; the user would paste the same phrase
            // dozens of times. Emphasis ("yes yes yes") and short text must survive.
            Assert(TranscriptionService.CollapseRepetitionLoops("alpha beta gamma alpha beta gamma alpha beta gamma tail") == "alpha beta gamma tail",
                "three repeats of a three-word phrase did not collapse");
            var mixedCase = TranscriptionService.CollapseRepetitionLoops("Alpha beta gamma. alpha beta gamma alpha beta gamma");
            Assert(mixedCase == "Alpha beta gamma.",
                $"repeats differing only in case and edge punctuation did not collapse to the first copy: '{mixedCase}'");
            const string emphasis = "yes yes yes yes yes yes yes yes yes";
            Assert(TranscriptionService.CollapseRepetitionLoops(emphasis) == emphasis, "single-word emphasis was collapsed");
            const string shortText = "one two three one two";
            Assert(TranscriptionService.CollapseRepetitionLoops(shortText) == shortText, "text too short to hold a loop changed");
            // Long enough to reach the loop scan (9 tokens), but the phrase repeats only twice.
            const string twoRepeats = "alpha beta gamma alpha beta gamma delta epsilon zeta";
            Assert(TranscriptionService.CollapseRepetitionLoops(twoRepeats) == twoRepeats, "a phrase said only twice was collapsed");
        });

        run("streaming: a multi-channel mic downmixes at 1/sqrt(n) and clamps (#830)", () =>
        {
            // A mic array with one live channel: a plain average (1/n) makes the
            // voice too quiet to transcribe; no clamp would wrap loud audio.
            var mix = typeof(StreamingAudioCapture).GetMethod("MixToMono", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(StreamingAudioCapture), "MixToMono");
            short[] Mix(int channels, params short[] samples)
            {
                var buffer = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);
                var output = (byte[])mix.Invoke(null, new object[] { buffer, buffer.Length, channels })!;
                var result = new short[output.Length / sizeof(short)];
                Buffer.BlockCopy(output, 0, result, 0, output.Length);
                return result;
            }
            void Near(short actual, double expected, string label) =>
                Assert(Math.Abs(actual - expected) <= 1.0, $"{label}: expected ~{expected:F1}, got {actual}");

            var four = Mix(4, 16000, 0, 0, 0);
            Assert(four.Length == 1, $"one 4-channel frame gave {four.Length} samples");
            Near(four[0], 16000 / Math.Sqrt(4), "4 channels, one live");
            var eight = Mix(8, 16000, 0, 0, 0, 0, 0, 0, 0);
            Near(eight[0], 16000 / Math.Sqrt(8), "8 channels, one live");
            var two = Mix(4, 12000, 0, 0, 0, 0, -12000, 0, 0);
            Assert(two.Length == 2, $"two 4-channel frames gave {two.Length} samples");
            Near(two[0], 6000, "frame 1");
            Near(two[1], -6000, "frame 2");
            Assert(Mix(4, short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue)[0] == short.MaxValue,
                "loud correlated channels did not clamp to Int16 max");
            Assert(Mix(4, short.MinValue, short.MinValue, short.MinValue, short.MinValue)[0] == short.MinValue,
                "loud correlated channels did not clamp to Int16 min");
        });

        run("orchestrator: whitespace from the engine is NoSpeechDetected and names the engine (#830)", () =>
        {
            // Silence must end as the no-speech failure (its own toast and History
            // reason), not paste blanks or a generic error with no provider.
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();
            var mode = new Mode { Name = "g830 no speech", ProviderType = "local", Language = "auto", PostProcessingMode = 0 };
            using var orchestrator = new TranscriptionOrchestrator();
            TranscriptionException? caught = null;
            try
            {
                orchestrator.TranscribeAsync(
                    audioPath: "C:\\hyperwhisper-smoketest\\silent.wav",
                    mode: mode,
                    vocabulary: null,
                    localTranscriptionProvider: new WhitespaceProvider(),
                    applicationContext: null,
                    cancellationToken: CancellationToken.None,
                    callSite: TranscriptionCallSite.Gui).GetAwaiter().GetResult();
            }
            catch (TranscriptionException ex)
            {
                caught = ex;
            }

            Assert(caught != null, "a whitespace-only transcript did not fail");
            Assert(caught!.Code == TranscriptionErrorCode.NoSpeechDetected, $"expected NoSpeechDetected, got {caught.Code}");
            Assert(caught.ProviderName == WhitespaceProvider.ProviderName, $"the provider name was lost: '{caught.ProviderName}'");
            Assert(caught.Message.Contains("No speech", StringComparison.OrdinalIgnoreCase), $"message '{caught.Message}'");
        });

        run("history: startup recovery turns a dead session's Processing row into a retryable Failed row (#830)", () =>
        {
            // A crash mid-transcription leaves a row spinning forever unless startup
            // recovery marks it Failed. A finished row must not be touched.
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();
            var history = HistoryService.Instance;
            var orphan = history.CreateProcessingTranscript(1.0, "g830-orphan", audioFilePath: null);
            var done = history.CreateProcessingTranscript(1.0, "g830-done", audioFilePath: null);
            try
            {
                done.Status = TranscriptStatus.Completed;
                done.Text = "g830 finished";
                history.UpdateTranscript(done);

                var recovered = history.RecoverOrphanedProcessingTranscripts();
                Assert(recovered >= 1, $"recovery reported {recovered} rows; {RecoveryZeroCause(history, orphan.Id)}");
                using var check = new HyperWhisperDbContext();
                var savedOrphan = check.Transcripts.AsNoTracking().FirstOrDefault(t => t.Id == orphan.Id);
                Assert(savedOrphan?.Status == TranscriptStatus.Failed && savedOrphan.FailedReason == HistoryService.InterruptedReason,
                    $"the orphan is {savedOrphan?.Status.ToString() ?? "missing"} ({savedOrphan?.FailedReason ?? "no reason"})");
                var savedDone = check.Transcripts.AsNoTracking().FirstOrDefault(t => t.Id == done.Id);
                Assert(savedDone?.Status == TranscriptStatus.Completed && savedDone.Text == "g830 finished",
                    $"recovery touched a Completed row: {savedDone?.Status.ToString() ?? "missing"}");
            }
            finally
            {
                history.DeleteTranscript(orphan.Id);
                history.DeleteTranscript(done.Id);
            }
        });

        run("history: a one-row update and startup recovery wait for a reader on another connection, not fail at once (#2430)", () =>
        {
            // The database is in rollback-journal mode, and EF ran a one-row SaveChanges
            // in autocommit, committing at the statement reset where the provider's busy
            // retry does not run. A reader mid-read on another connection (the Home stats
            // bar after every History event) then failed the write with "database is
            // locked" in a few milliseconds. The write must wait for the reader instead.
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();
            var history = HistoryService.Instance;
            var row = history.CreateProcessingTranscript(1.0, "g2430-update", audioFilePath: null);
            var orphan = history.CreateProcessingTranscript(1.0, "g2430-orphan", audioFilePath: null);
            try
            {
                var pending = history.GetPendingTranscriptsSnapshot();
                Assert(!pending.Any(t => t.Id == row.Id || t.Id == orphan.Id),
                    "a fixture row went to the pending store, so the database write is never tested");

                row.Status = TranscriptStatus.Completed;
                row.Text = "g2430 waited";
                var update = WriteWhileAReaderHolds(() => history.UpdateTranscript(row));
                Assert(update.Failure == null, $"the update failed while a reader held the database: {DescribeCause(update.Failure)}");
                Assert(update.WaitedForReader, "the update finished while the reader still held the database");

                // The recovery check above left nothing else Processing, and the update
                // finished this check's own row, so recovery is a one-row UPDATE too.
                var recovered = 0;
                var recovery = WriteWhileAReaderHolds(() => recovered = history.RecoverOrphanedProcessingTranscripts());
                Assert(recovery.Failure == null, $"recovery threw: {DescribeCause(recovery.Failure)}");
                Assert(recovered >= 1, $"recovery under a reader reported {recovered} rows; {RecoveryZeroCause(history, orphan.Id)}");
                Assert(recovery.WaitedForReader, "recovery finished while the reader still held the database");

                using var check = new HyperWhisperDbContext();
                var savedRow = check.Transcripts.AsNoTracking().FirstOrDefault(t => t.Id == row.Id);
                Assert(savedRow?.Status == TranscriptStatus.Completed && savedRow.Text == "g2430 waited",
                    $"the update did not persist: {savedRow?.Status.ToString() ?? "missing"}");
                var savedOrphan = check.Transcripts.AsNoTracking().FirstOrDefault(t => t.Id == orphan.Id);
                Assert(savedOrphan?.Status == TranscriptStatus.Failed && savedOrphan.FailedReason == HistoryService.InterruptedReason,
                    $"recovery did not persist: the orphan is {savedOrphan?.Status.ToString() ?? "missing"}");
            }
            finally
            {
                history.DeleteTranscript(row.Id);
                history.DeleteTranscript(orphan.Id);
            }
        });

        run("models: deleting a Whisper or Parakeet model whose file is in use fails as a Result and keeps it (#830)", () =>
        {
            // A model file held open (a running transcription, antivirus) made
            // Delete throw into the Settings page. It must come back as a failure
            // the page shows, with the model still installed.
            var whisper = new WhisperModelService();
            var whisperModel = new WhisperModelInfo("g830-locked", "G830 locked", "4 KB", false, 4096, 0);
            var whisperPath = whisper.GetModelPath(whisperModel);
            var parakeet = new ParakeetModelService();
            var parakeetModel = new ParakeetModelInfo(
                "g830-locked-parakeet", "G830", "1 KB", 10, true, ["en"], ["tokens.txt"], "smoke/none",
                fileSizes: new Dictionary<string, long> { ["tokens.txt"] = 10 });
            var parakeetDir = parakeet.GetModelDirectory(parakeetModel);
            var tokens = Path.Combine(parakeetDir, "tokens.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(whisperPath)!);
                File.WriteAllBytes(whisperPath, new byte[4096]);
                Directory.CreateDirectory(parakeetDir);
                File.WriteAllBytes(tokens, new byte[10]);

                using (new FileStream(whisperPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var result = whisper.DeleteModel(whisperModel);
                    Assert(result.IsFailure && !string.IsNullOrWhiteSpace(result.Error),
                        "deleting a locked Whisper model did not return a failure with a message");
                }
                Assert(File.Exists(whisperPath), "the locked Whisper model file is gone");

                using (new FileStream(tokens, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var result = parakeet.DeleteModel(parakeetModel);
                    Assert(result.IsFailure && !string.IsNullOrWhiteSpace(result.Error),
                        "deleting a locked Parakeet model did not return a failure with a message");
                }
                Assert(File.Exists(tokens), "the locked Parakeet model file is gone");

                // Unlocked, the same calls succeed and remove the files.
                Assert(whisper.DeleteModel(whisperModel).IsSuccess && !File.Exists(whisperPath), "an unlocked Whisper delete failed");
                Assert(parakeet.DeleteModel(parakeetModel).IsSuccess && !Directory.Exists(parakeetDir), "an unlocked Parakeet delete failed");
            }
            finally
            {
                TryDeleteFile(whisperPath);
                TryDeleteDirectory(parakeetDir);
            }
        });

        run("models: the Model Library refuses to delete a local model a mode still uses (#2245)", () =>
        {
            // A deleted model a mode still points at breaks that mode: its local
            // transcription, or its AI rewrite, fails on the next recording.
            var llmId = LocalLlmModelInfo.AllModels[0].Id;
            var otherLlmId = LocalLlmModelInfo.AllModels.First(m => m.Id != llmId).Id;
            var localLlm = PostProcessingProvider.LocalLlm.ToStringValue();
            // PostProcessingMode 2: local AI rewrite on. A mode with it off (0) runs no local LLM.
            Mode M(string name, Action<Mode> set) { var m = new Mode { Name = name, PostProcessingMode = 2 }; set(m); return m; }

            Assert(ModelInUse.FindModeUsingLocalLlm([M("Rewrite", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = llmId; })], llmId)?.Name == "Rewrite",
                "a mode whose LocalPostProcessingModel is the model did not protect it");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Legacy", m => { m.PostProcessingProvider = localLlm; m.LanguageModel = llmId; })], llmId)?.Name == "Legacy",
                "a mode that keeps the model in the legacy LanguageModel field did not protect it");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Upper", m => { m.PostProcessingProvider = "LOCAL_LLM"; m.LocalPostProcessingModel = llmId; })], llmId)?.Name == "Upper",
                "the provider match was case-sensitive");

            // #2536: a mode runs on the default local model whenever its stored id
            // does not resolve (PostProcessingService.ResolveModel, then
            // LocalLlmModelInfo.GetByIdOrDefault), so it protects that default.
            var defaultLlmId = LocalLlmModelInfo.GetDefault().Id;
            var nonDefaultLlmId = LocalLlmModelInfo.AllModels.First(m => m.Id != defaultLlmId).Id;
            Assert(ModelInUse.FindModeUsingLocalLlm([M("NoId", m => { m.PostProcessingProvider = localLlm; })], defaultLlmId)?.Name == "NoId",
                "a local-LLM mode with no model id did not protect the default it runs on");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Empty", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = ""; m.LanguageModel = nonDefaultLlmId; })], defaultLlmId)?.Name == "Empty",
                "a local-LLM mode with an empty LocalPostProcessingModel did not protect the default it runs on");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Empty", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = ""; m.LanguageModel = nonDefaultLlmId; })], nonDefaultLlmId) == null,
                "an empty LocalPostProcessingModel fell back to LanguageModel, which the runtime does not");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Case", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = nonDefaultLlmId.ToUpperInvariant(); })], defaultLlmId)?.Name == "Case",
                "a mode on an id in different case did not protect the default it runs on");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Case", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = nonDefaultLlmId.ToUpperInvariant(); })], nonDefaultLlmId) == null,
                "a mode on an id in different case blocked a model the runtime never loads for it");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Unknown", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = "retired-model.gguf"; })], defaultLlmId)?.Name == "Unknown",
                "a mode on an unknown id did not protect the default it runs on");
            // A cloud id (even a retired one that migrates) is another provider's,
            // so a local-LLM mode holding one runs on the default local model.
            const string retiredId = "llama-3.1-8b";
            Assert(LanguageModelInfo.MigrateModelId(retiredId) != retiredId, $"test premise: {retiredId} no longer migrates");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Old", m => { m.PostProcessingProvider = localLlm; m.LanguageModel = retiredId; })], defaultLlmId)?.Name == "Old",
                "a local-LLM mode on a retired cloud id did not protect the default it runs on");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Other", m => { m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = otherLlmId; })], llmId) == null,
                "a mode on a different local model blocked the delete");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Cloud", m => { m.PostProcessingProvider = "openai"; m.LanguageModel = llmId; })], llmId) == null,
                "a mode whose provider is not the local LLM blocked the delete");

            Assert(ModelInUse.FindModeUsingWhisperModel([M("W", m => { m.ProviderType = "local"; m.LocalEngine = "whisper"; m.ModelType = "base"; })], "base")?.Name == "W",
                "a local Whisper mode did not protect its model");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("WLegacy", m => { m.ProviderType = "local"; m.LocalEngine = "whisper"; m.Model = "base"; })], "base")?.Name == "WLegacy",
                "a local Whisper mode that keeps its model in the legacy Model field did not protect it");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("W", m => { m.ProviderType = "cloud"; m.LocalEngine = "whisper"; m.ModelType = "base"; })], "base") == null,
                "a cloud mode's leftover Whisper model blocked the delete");
            Assert(ModelInUse.FindModeUsingParakeetModel([M("P", m => { m.ProviderType = "local"; m.LocalEngine = "parakeet"; m.LocalParakeetModel = "pk"; })], "pk")?.Name == "P",
                "a local Parakeet mode did not protect its model");
            Assert(ModelInUse.FindModeUsingParakeetModel([M("P", m => { m.ProviderType = "local"; m.LocalEngine = "whisper"; m.LocalParakeetModel = "pk"; })], "pk") == null,
                "a Whisper-engine mode's leftover Parakeet model blocked the delete");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("WNoEngine", m => { m.ProviderType = "local"; m.LocalEngine = ""; m.ModelType = "base"; })], "base")?.Name == "WNoEngine",
                "a local mode with no engine set (it runs Whisper) did not protect its model");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("WNull", m => { m.ProviderType = null; m.LocalEngine = "whisper"; m.ModelType = "base"; })], "base")?.Name == "WNull",
                "a mode with no ProviderType (it runs locally) did not protect its model");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("WNew", m => { m.ProviderType = "local"; m.LocalEngine = "whisper"; m.ModelType = "small"; m.Model = "base"; })], "base") == null,
                "a stale legacy Model blocked a delete the mode's ModelType does not use");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("WCase", m => { m.ProviderType = "local"; m.LocalEngine = "whisper"; m.ModelType = "BASE"; })], "base") == null,
                "an id in different case blocked a delete; the runtime loads Whisper types by exact match");
            // The Local API reads a mode a little differently from the recording
            // path (any case of "parakeet", a blank id falls back to Model); a mode
            // protects what either path would load.
            Assert(ModelInUse.FindModeUsingParakeetModel([M("PCase", m => { m.ProviderType = "local"; m.LocalEngine = "Parakeet"; m.LocalParakeetModel = "pk"; })], "pk")?.Name == "PCase",
                "a mode the Local API runs on Parakeet (engine \"Parakeet\") did not protect its model");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("PCase", m => { m.ProviderType = "local"; m.LocalEngine = "Parakeet"; m.ModelType = "base"; })], "base")?.Name == "PCase",
                "a mode the recording path runs on Whisper (engine \"Parakeet\") did not protect its Whisper model");
            Assert(ModelInUse.FindModeUsingParakeetModel([M("PBlank", m => { m.ProviderType = "local"; m.LocalEngine = "parakeet"; m.LocalParakeetModel = " "; m.Model = "pk"; })], "pk")?.Name == "PBlank",
                "a Parakeet mode with a blank id did not protect the Model the Local API falls back to");
            Assert(ModelInUse.FindModeUsingWhisperModel([M("WBlank", m => { m.ProviderType = "local"; m.LocalEngine = "whisper"; m.ModelType = ""; m.Model = "base"; })], "base")?.Name == "WBlank",
                "a Whisper mode with a blank ModelType did not protect the Model the Local API falls back to");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("Off", m => { m.PostProcessingMode = 0; m.PostProcessingProvider = localLlm; })], defaultLlmId) == null,
                "a mode with AI rewrite off blocked deleting the default local model");
            Assert(ModelInUse.FindModeUsingLocalLlm([M("OffNamed", m => { m.PostProcessingMode = 0; m.PostProcessingProvider = localLlm; m.LocalPostProcessingModel = nonDefaultLlmId; })], nonDefaultLlmId)?.Name == "OffNamed",
                "a mode with AI rewrite off stopped protecting the local model it names");
        });

        run("prompt: each detected app type puts its own context block in the AI prompt (#830)", () =>
        {
            // The AppType -> core HwAppType switch is C#. A wrong arm formats an
            // email like a chat message, or code like prose, with no error.
            AppContextInfo Context(AppType type, string process, string format) => new()
            {
                ProcessName = process,
                WindowTitle = $"{process} test window",
                Category = type.ToCategory(),
                TextFormat = format,
                AppType = type,
                AppTypeConfidence = "strong",
                AppTypeSource = "verifier",
            };
            var cases = new (string Preset, AppContextInfo Context, string Marker, string Wire)[]
            {
                ("hyper", Context(AppType.Email, "OUTLOOK", "email"), "<EMAIL_CONTEXT_DETECTED>", "email"),
                ("hyper", Context(AppType.Document, "WINWORD", "markdown"), "<DOCUMENT_CONTEXT_DETECTED>", "document"),
                ("hyper", Context(AppType.Code, "Code", "code"), "<CODE_CONTEXT_DETECTED>", "code"),
                ("hyper", Context(AppType.Terminal, "WindowsTerminal", "command"), "<TERMINAL_CONTEXT_DETECTED>", "terminal"),
                ("message", Context(AppType.WorkMessaging, "slack", "text"), "<WORK_MESSAGE_CONTEXT_DETECTED>", "work_messaging"),
                ("message", Context(AppType.PersonalMessaging, "Discord", "text"), "<PERSONAL_MESSAGE_CONTEXT_DETECTED>", "personal_messaging"),
            };
            foreach (var c in cases)
            {
                var mode = new Mode { Preset = c.Preset };
                var prompt = PromptBuilder.SystemPrompt(mode, c.Context);
                var info = PromptBuilder.SystemInfo(mode, applicationContext: c.Context);
                Assert(prompt.Contains(c.Marker, StringComparison.Ordinal), $"{c.Wire}: the prompt has no {c.Marker}");
                Assert(info.Contains($"<APP_TYPE>{c.Wire}</APP_TYPE>", StringComparison.Ordinal), $"{c.Wire}: system info has the wrong APP_TYPE");
                Assert(info.Contains("<APP_TYPE_CONFIDENCE>strong</APP_TYPE_CONFIDENCE>", StringComparison.Ordinal)
                    && info.Contains("<APP_TYPE_SOURCE>verifier</APP_TYPE_SOURCE>", StringComparison.Ordinal),
                    $"{c.Wire}: system info lost the confidence or source");
                Assert(!prompt.Contains("{{", StringComparison.Ordinal), $"{c.Wire}: the prompt kept an unfilled template slot");
            }
        });

        run("streaming: only HyperWhisper Cloud streams without the user's own API key (#830)", () =>
        {
            // A direct provider marked keyless would start a session with no key and
            // fail on the wire; Cloud marked keyed would ask a subscriber for a key.
            foreach (var provider in Enum.GetValues<StreamingTranscriptionProvider>())
            {
                Assert(provider.RequiresApiKey() == (provider != StreamingTranscriptionProvider.HyperWhisperCloud),
                    $"{provider}: RequiresApiKey() is {provider.RequiresApiKey()}");
            }
        });

        run("local api: a /transcribe file reached through a junction under a trusted root is refused (#830, #740)", () =>
        {
            // A token holder could plant a junction in the recordings folder and have
            // the app read (and upload) any file it points at. The opened handle's
            // real path decides, not the string the caller sent.
            var id = Guid.NewGuid().ToString("N");
            var root = AppPaths.ProfileTempRecordingsDirectory;
            var outsideDir = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"junction-target-{id}");
            var junction = Path.Combine(root, $"g830-junction-{id}");
            var realFile = Path.Combine(root, $"g830-real-{id}.wav");
            var viaJunction = Path.Combine(junction, "secret.wav");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(outsideDir);
                File.WriteAllBytes(Path.Combine(outsideDir, "secret.wav"), new byte[] { 1, 2, 3, 4 });
                File.WriteAllBytes(realFile, new byte[] { 1, 2, 3, 4 });
                var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outsideDir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                mklink.WaitForExit(10_000);
                Assert(mklink.ExitCode == 0 && File.Exists(viaJunction),
                    $"could not create the junction fixture (exit {mklink.ExitCode}): {mklink.StandardError.ReadToEnd()}");
                Assert(HistoryService.IsTrustedAudioPath(viaJunction),
                    "precondition: the junction path should look trusted by its string alone");

                using (var real = new FileStream(realFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert(LocalApiFilePolicy.VerifyOpenedFile(real), "a real file in the recordings folder was refused");
                using (var planted = new FileStream(viaJunction, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert(!LocalApiFilePolicy.VerifyOpenedFile(planted), "a file reached through a junction to outside the roots was accepted");
            }
            finally
            {
                // A junction is removed as a link; its target folder stays until deleted below.
                try { if (Directory.Exists(junction)) Directory.Delete(junction); } catch { }
                TryDeleteFile(realFile);
                TryDeleteDirectory(outsideDir);
            }
        });

        run("file transcription: a cancel before or during conversion throws OperationCanceledException and leaves no temp WAV (#830)", () =>
        {
            // Escape during a file transcription must end silently as a cancel. A
            // cancel reported as a Failure shows an error toast, and a half-written
            // converted WAV left behind fills %TEMP%.
            var source = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"g830-cancel-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            HashSet<string> Converted() => Directory.EnumerateFiles(Path.GetTempPath(), "hyperwhisper_converted_*.wav").ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Five minutes of 44.1 kHz stereo: the call must convert (not pass
                // it through), and the conversion runs long enough to cancel inside.
                const int bytesPerSecond = 44100 * 4;
                using (var writer = new NAudio.Wave.WaveFileWriter(source, new NAudio.Wave.WaveFormat(44100, 16, 2)))
                {
                    var second = new byte[bytesPerSecond];
                    for (var i = 0; i < second.Length; i += 2) second[i] = (byte)(i * 7);
                    for (var s = 0; s < 300; s++) writer.Write(second, 0, second.Length);
                }

                Exception? Convert(CancellationToken token)
                {
                    try
                    {
                        // On the pool: an earlier case leaves a WPF SynchronizationContext on this
                        // thread, and the method's await would wait for a Dispatcher loop that never runs.
                        var result = Task.Run(() => FileTranscriptionService.ConvertToWhisperFormatAsync(source, token)).GetAwaiter().GetResult();
                        if (result.IsSuccess) TryDeleteFile(result.Value!);
                        Assert(false, $"a cancelled conversion returned a result (success {result.IsSuccess}, error '{result.Error}')");
                        return null;
                    }
                    catch (OperationCanceledException ex)
                    {
                        return ex;
                    }
                }

                var before = Converted();
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    Assert(Convert(cancelled.Token) != null, "a conversion cancelled before it started did not throw OperationCanceledException");
                }

                // Cancel the moment the converted file appears, i.e. mid-write.
                using var midway = new CancellationTokenSource();
                var sawTempFile = false;
                var watcher = Task.Run(() =>
                {
                    var clock = Stopwatch.StartNew();
                    while (!midway.IsCancellationRequested && clock.Elapsed < TimeSpan.FromSeconds(60))
                    {
                        if (Converted().Except(before).Any())
                        {
                            sawTempFile = true;
                            midway.Cancel();
                            return;
                        }
                        Thread.Sleep(1);
                    }
                });
                var thrown = Convert(midway.Token);
                watcher.Wait(TimeSpan.FromSeconds(70));
                Assert(sawTempFile, "the conversion never wrote a temp file, so the mid-conversion cancel was not exercised");
                Assert(thrown != null, "a conversion cancelled mid-write did not throw OperationCanceledException");
                var leaked = Converted().Except(before).ToList();
                foreach (var path in leaked) TryDeleteFile(path);
                Assert(leaked.Count == 0, $"a cancelled conversion left {string.Join(", ", leaked)}");
            }
            finally
            {
                TryDeleteFile(source);
            }
        });

        run("tray: Start/Stop item text and enabled state, and the empty mode submenus (#830)", () =>
        {
            // Stop must stay reachable from the tray while recording; Start must be
            // off while a transcription or model load is running or nothing to
            // record with is chosen; an empty mode list shows a disabled placeholder.
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            var recordingMenu = new ToolStripMenuItem();
            var modeMenu = new ToolStripMenuItem();
            var fileMenu = new ToolStripMenuItem();
            SetField(window, "_viewModel", vm);
            SetField(window, "_recordingMenu", recordingMenu);
            SetField(window, "_modeMenu", modeMenu);
            SetField(window, "_fileTranscriptionMenu", fileMenu);
            var named = new Mode { Id = Guid.NewGuid(), Name = "Alpha", SortOrder = 1 };
            var unnamed = new Mode { Id = Guid.NewGuid(), Name = "   ", SortOrder = 2 };
            var device = new AudioDeviceService.AudioDevice(3, "G830 Microphone");

            void Set(bool recording, bool transcribing, bool loading, AudioDeviceService.AudioDevice? mic, Mode? selected, List<Mode> modes)
            {
                SetField(vm, "_isRecording", recording);
                SetField(vm, "_isTranscribing", transcribing);
                SetField(vm, "_isModelLoading", loading);
                SetField(vm, "_selectedAudioDevice", mic);
                SetField(vm, "_selectedMode", selected);
                SetField(vm, "_modes", modes);
            }
            void Refresh(string method) =>
                (typeof(MainWindow).GetMethod(method, InstanceField) ?? throw new MissingMethodException(nameof(MainWindow), method))
                    .Invoke(window, null);

            var start = Loc.S("menu.recording.toggle");
            var stop = Loc.S("menu.recording.stop");
            var modes = new List<Mode> { named, unnamed };
            (bool Recording, bool Transcribing, bool Loading, AudioDeviceService.AudioDevice? Mic, Mode? Selected, string Text, bool Enabled, string Label)[] states =
            {
                (false, false, false, null, named, start, false, "idle with no microphone"),
                (false, false, false, device, null, start, false, "idle with no mode"),
                (false, false, false, device, named, start, true, "idle and ready"),
                (false, true, false, device, named, start, false, "transcribing"),
                (false, false, true, device, named, start, false, "loading a model"),
                (true, false, true, null, null, stop, true, "recording (Stop must stay reachable)"),
            };
            foreach (var s in states)
            {
                Set(s.Recording, s.Transcribing, s.Loading, s.Mic, s.Selected, modes);
                Refresh("RefreshRecordingMenu");
                Assert(recordingMenu.Text == s.Text && recordingMenu.Enabled == s.Enabled,
                    $"{s.Label}: the item is '{recordingMenu.Text}' enabled={recordingMenu.Enabled}, expected '{s.Text}' enabled={s.Enabled}");
            }

            Set(false, false, false, device, unnamed, modes);
            Refresh("RefreshModeMenu");
            var items = modeMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            Assert(items.Count == 2 && items[0].Text == "Alpha" && !items[0].Checked, "the named mode item is wrong");
            Assert(items[1].Text == Loc.S("menu.mode.unnamed") && items[1].Checked && ReferenceEquals(items[1].Tag, unnamed),
                "a blank-named selected mode is not shown as the checked unnamed item");

            foreach (var (menu, method) in new[] { (modeMenu, "RefreshModeMenu"), (fileMenu, "RefreshFileTranscriptionMenu") })
            {
                Set(false, false, false, device, null, new List<Mode>());
                Refresh(method);
                Assert(menu.DropDownItems.Count == 1 && menu.DropDownItems[0].Text == Loc.S("menu.mode.none") && !menu.DropDownItems[0].Enabled,
                    $"{method}: no modes did not give one disabled '{Loc.S("menu.mode.none")}' item");
            }
        });

        RunKeepWarm(run);
        RunDictationModePinning(run);
        RunStreamingCancel(run);
    }

    /// <summary>
    /// The microphone keep-warm state machine (#2244), driven through its capture
    /// seam with a fake capture, so a check sees every open and close the service
    /// makes. It replaces the deleted verify_microphone_keep_warm_wiring.ps1, which
    /// matched source text and failed on moved code. The Linux head has the same
    /// rules (HyperWhisper.Linux.Platform.Tests, "microphone keep-warm ...").
    /// </summary>
    private static void RunKeepWarm(Action<string, Action> run)
    {
        run("keep-warm: re-selecting the warm device keeps the one capture (#2244)", () =>
        {
            var captures = new List<FakeCapture>();
            using var keepWarm = NewKeepWarm(captures);

            keepWarm.Configure(true, 2);
            Assert(captures.Count == 1 && captures[0].Device == 2 && captures[0].Running,
                "enabling keep-warm on device #2 did not open one running capture on #2");

            // Settings and device-list refreshes call Configure again with the same
            // device; a restart there re-pays the cold WASAPI activation.
            keepWarm.Configure(true, 2);
            keepWarm.Configure(true, 2);
            Assert(captures.Count == 1 && captures[0].Running && captures[0].Stops == 0,
                $"re-selecting the warm device restarted capture ({captures.Count} opened, {captures[0].Stops} stops)");

            keepWarm.Configure(true, 5);
            Assert(captures.Count == 2 && captures[0].Disposed && !captures[0].Running,
                "changing device left the old capture open");
            Assert(captures[1].Device == 5 && captures[1].Running,
                "changing device did not open a capture on the new device");

            // -1 is NAudio's system-default device: a device, not "no device".
            keepWarm.Configure(true, -1);
            Assert(captures.Count == 3 && captures[2].Device == -1 && captures[2].Running,
                "the system-default device (-1) did not open a capture");

            keepWarm.Configure(true, null);
            Assert(captures.TrueForAll(c => c.Disposed) && captures.Count == 3,
                "no selected device left a capture open, or opened one");

            keepWarm.Configure(true, 5);
            keepWarm.Configure(false, 5);
            Assert(captures.Count == 4 && captures[3].Disposed,
                "disabling keep-warm left the capture open");
            keepWarm.Configure(false, 5);
            Assert(captures.Count == 4, "keep-warm opened a capture while disabled");
        });

        run("keep-warm: a recording suspends it and stop/cancel resume it (#2244)", () =>
        {
            var captures = new List<FakeCapture>();
            using var keepWarm = NewKeepWarm(captures);
            keepWarm.Configure(true, 3);

            // Start: the real recorder opens the same endpoint next, so the idle
            // stream must be closed before it.
            keepWarm.SuspendForRecording();
            Assert(captures.Count == 1 && captures[0].Disposed && !captures[0].Running,
                "starting a recording left the keep-warm capture open");

            // A settings or device change mid-recording must not reopen it.
            keepWarm.Configure(true, 3);
            keepWarm.Configure(true, 4);
            Assert(captures.Count == 1, "keep-warm reopened the microphone during a recording");

            // Stop (or cancel, the same call): reopen on the device the app has now.
            keepWarm.ResumeAfterRecording(4);
            Assert(captures.Count == 2 && captures[1].Device == 4 && captures[1].Running,
                "stopping the recording did not reopen keep-warm on the selected device");

            // A cancel straight after a stop resumes twice: still one capture.
            keepWarm.ResumeAfterRecording(4);
            Assert(captures.Count == 2 && captures[1].Running,
                "resuming twice opened a second capture");

            // A recording while keep-warm is off resumes to off.
            keepWarm.Configure(false, 4);
            keepWarm.SuspendForRecording();
            keepWarm.ResumeAfterRecording(4);
            Assert(captures.Count == 2 && captures[1].Disposed,
                "a recording with keep-warm off opened the microphone when it ended");
        });

        run("keep-warm: a failed or dropped capture recovers only while it is wanted (#2244)", () =>
        {
            var captures = new List<FakeCapture>();
            var failStarts = 1;
            using var keepWarm = new MicrophoneKeepWarmService(device =>
            {
                var capture = new FakeCapture(device, failStart: failStarts-- > 0);
                captures.Add(capture);
                return capture;
            });

            keepWarm.Configure(true, 1);
            Assert(captures.Count == 1 && captures[0].Disposed && !captures[0].Running,
                "a capture that failed to start was not disposed");
            keepWarm.Configure(true, 1);
            Assert(captures.Count == 2 && captures[1].Running,
                "after a failed start, the next Configure did not try again");

            // The device vanishes: NAudio stops with an error, and keep-warm reopens.
            captures[1].Fail();
            Assert(captures[1].Disposed && captures.Count == 3 && captures[2].Device == 1 && captures[2].Running,
                "an unexpected capture stop did not reopen keep-warm");

            // NAudio raises RecordingStopped on its capture thread, so a stop can land
            // after keep-warm has unsubscribed and let that capture go. Such a late
            // error stop from a replaced capture changes nothing.
            captures[1].Fail(late: true);
            Assert(captures.Count == 3 && captures[2].Running && !captures[2].Disposed,
                "a replaced capture's late error stop tore down or replaced the live capture");

            // During a recording, a late error stop from the capture keep-warm just
            // closed must not reopen the microphone the recorder now holds.
            keepWarm.SuspendForRecording();
            captures[2].Fail(late: true);
            Assert(captures.Count == 3, "a late error stop reopened keep-warm during a recording");
            keepWarm.ResumeAfterRecording(1);
            Assert(captures.Count == 4 && captures[3].Running, "keep-warm did not resume after the recording");

            keepWarm.Dispose();
            Assert(captures[3].Disposed, "disposing keep-warm left the capture open");
            keepWarm.Configure(true, 1);
            keepWarm.ResumeAfterRecording(1);
            Assert(captures.Count == 4, "keep-warm opened a capture after it was disposed");
        });

        run("keep-warm: MainViewModel suspends it for recordings, resumes it after, and follows setting and device changes (#2244)", () =>
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo Vm(string name) => typeof(MainViewModel).GetMethod(name, all)
                ?? throw new MissingMethodException(nameof(MainViewModel), name);
            MethodInfo KeepWarm(string name) => typeof(MicrophoneKeepWarmService).GetMethod(name)
                ?? throw new MissingMethodException(nameof(MicrophoneKeepWarmService), name);

            Assert(Program.AsyncBodyCalls(Vm("SuspendMicrophoneKeepWarm"), KeepWarm(nameof(MicrophoneKeepWarmService.SuspendForRecording))),
                "SuspendMicrophoneKeepWarm no longer suspends the keep-warm service");
            Assert(Program.AsyncBodyCalls(Vm("ResumeMicrophoneKeepWarm"), KeepWarm(nameof(MicrophoneKeepWarmService.ResumeAfterRecording))),
                "ResumeMicrophoneKeepWarm no longer resumes the keep-warm service");
            Assert(Program.AsyncBodyCalls(Vm("UpdateMicrophoneKeepWarm"), KeepWarm(nameof(MicrophoneKeepWarmService.Configure))),
                "UpdateMicrophoneKeepWarm no longer configures the keep-warm service");

            // Presence only: the IL scan cannot see whether the suspend comes before
            // the recorder opens.
            foreach (var starter in new[] { "StartRecordingAsync", "StartStreamingRecordingAsync" })
            {
                Assert(Program.AsyncBodyCalls(Vm(starter), Vm("SuspendMicrophoneKeepWarm")),
                    $"{starter} no longer suspends keep-warm, so the idle stream holds the microphone during the recording");
            }
            foreach (var resumer in new[] { "StopRecordingAndTranscribeAsync", "StopStreamingRecordingAsync", "CancelRecordingAsync", "CancelStreamingRecordingAsync", "CleanupFailedRecordingStart" })
            {
                Assert(Program.AsyncBodyCalls(Vm(resumer), Vm("ResumeMicrophoneKeepWarm")),
                    $"{resumer} no longer resumes keep-warm, so the microphone stays cold after it");
            }
            foreach (var listener in new[] { "OnSelectedAudioDeviceChanged", "OnSettingsChanged" })
            {
                Assert(Program.AsyncBodyCalls(Vm(listener), Vm("UpdateMicrophoneKeepWarm")),
                    $"{listener} no longer reconfigures keep-warm, so a setting or device change waits for the next recording");
            }
            Assert(Program.AsyncBodyCalls(Vm("CleanupAsync"), KeepWarm(nameof(MicrophoneKeepWarmService.Dispose))),
                "CleanupAsync no longer disposes keep-warm, so the microphone stays open at exit");
        });
    }

    /// <summary>
    /// The mode a dictation runs on (#2241). A tray or hotkey mode switch while
    /// the user is still speaking must not move the recording already in progress
    /// to the new mode: its provider, language, trailing-period rule and History
    /// label stay the ones it started with. The check drives the real start, stop
    /// and cancel path through MainViewModel's dictation seams, with a fake
    /// microphone, transcriber and paste target. It replaces the deleted
    /// verify_ptt_tray_mode_pinning.ps1, which matched source text.
    /// </summary>
    private static void RunDictationModePinning(Action<string, Action> run)
    {
        run("dictation: a tray or hotkey mode switch mid-dictation leaves the recording on the mode it started with (#2241)", () =>
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();

            // Earlier cases can leave a WPF context on this thread, and the harness
            // runs no Dispatcher loop, so an awaited continuation would never run.
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            var settings = SettingsService.Instance;
            var saved = (settings.AutoPasteEnabled, settings.AutocapitalizeInsert, settings.AutoIncreaseMicVolume,
                settings.StoreAsM4A, settings.MediaControlMode, settings.EnableSoundEffects);
            var transcribed = new List<(string AudioPath, Mode Mode)>();
            var pasted = new List<string>();
            using var shortcuts = new KeyboardShortcutService();
            using var pushToTalk = new PushToTalkMonitor();
            MainViewModel? vm = null;
            try
            {
                // The paste seam sits on the auto-paste branch; the rest keep the
                // run off the machine (no mic boost, no media mute, no sounds, no focus read).
                settings.AutoPasteEnabled = true;
                settings.AutocapitalizeInsert = false;
                settings.AutoIncreaseMicVolume = false;
                settings.StoreAsM4A = false;
                settings.MediaControlMode = "off";
                settings.EnableSoundEffects = false;

                // Alpha and Beta differ in everything the stop path reads from the
                // mode: the name History shows, the language (Japanese gets no
                // trailing space) and the trailing-period rule.
                Mode CloudMode(string name, string language, bool removeTrailingPeriod)
                {
                    var mode = ModeDefaults.GetDefaultModes().Single();
                    mode.Id = Guid.NewGuid();
                    mode.Name = name;
                    mode.Language = language;
                    mode.RemoveTrailingPeriod = removeTrailingPeriod;
                    mode.PostProcessingMode = 0;
                    mode.EnableScreenOCR = false;
                    return mode;
                }
                var alpha = CloudMode("G2241 Alpha", "ja", removeTrailingPeriod: true);
                var beta = CloudMode("G2241 Beta", "en", removeTrailingPeriod: false);
                Assert(alpha.ProviderType == "cloud", "precondition: the stock mode is a cloud mode, so no local model has to load");
                const string spoken = "Hello world.";
                const string alphaText = "Hello world";
                const string betaText = "Hello world. ";

                var recorder = new FakeDictationRecorder();
                vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
                foreach (var (name, value) in new (string, object?)[]
                {
                    ("_settingsService", settings),
                    ("_modelService", new WhisperModelService()),
                    ("_transcriptionService", new TranscriptionService()),
                    ("_parakeetTranscriptionService", new ParakeetTranscriptionService()),
                    ("_localLlmModelService", new LocalLlmModelService()),
                    ("_vocabularyService", VocabularyService.Instance),
                    ("_storageService", StorageService.Instance),
                    ("_shortcutService", shortcuts),
                    ("_pushToTalkMonitor", pushToTalk),
                    ("_audioDevices", new List<AudioDeviceService.AudioDevice>()),
                    ("_selectedAudioDevice", new AudioDeviceService.AudioDevice(3, "G2241 Microphone")),
                    ("_modes", new List<Mode> { alpha, beta }),
                    ("_selectedMode", alpha),
                })
                {
                    SetField(vm, name, value);
                }
                // No BYOK key, and never the machine's Credential Manager.
                vm.ApiKeyServiceOverride = new ApiKeyService(new Program.InMemoryCredentialBackend());
                vm.CloudAccountKeyPresentOverride = () => false;
                vm.DictationRecorderOverride = recorder;
                vm.TranscribeRecordingOverride = (audioPath, mode) =>
                {
                    transcribed.Add((audioPath, mode));
                    return Task.FromResult(new TranscriptionResult(spoken, spoken, "G2241 Provider"));
                };
                vm.DeliverAutoPasteOverride = text =>
                {
                    pasted.Add(text);
                    return Task.FromResult((SmartPasteResult.Pasted, false));
                };
                var cycleMode = typeof(MainViewModel).GetMethod("CycleMode", all)
                    ?? throw new MissingMethodException(nameof(MainViewModel), "CycleMode");

                string? HistoryLabel(string audioPath)
                {
                    using var db = new HyperWhisperDbContext();
                    var row = db.Transcripts.AsNoTracking().FirstOrDefault(t => t.AudioFilePath == audioPath);
                    return row?.Status == TranscriptStatus.Completed ? row.Mode : $"<{row?.Status.ToString() ?? "no row"}>";
                }

                void Dictate(string label, Action switchMode, Mode expected, string expectedText)
                {
                    var before = transcribed.Count;
                    vm.StartRecordingAsync().GetAwaiter().GetResult();
                    Assert(vm.IsRecording && recorder.Recording, $"{label}: the dictation did not start");
                    switchMode();
                    Assert(!ReferenceEquals(vm.SelectedMode, expected), $"{label}: precondition: the switch moved the selection off {expected.Name}");
                    vm.StopRecordingAndTranscribeAsync().GetAwaiter().GetResult();
                    Assert(!vm.IsRecording && !vm.IsTranscribing, $"{label}: the dictation did not finish");
                    Assert(transcribed.Count == before + 1, $"{label}: {transcribed.Count - before} transcriptions ran, expected 1");
                    var (audioPath, mode) = transcribed[^1];
                    Assert(ReferenceEquals(mode, expected),
                        $"{label}: the recording was transcribed for '{mode.Name}', the mode selected mid-dictation, not '{expected.Name}' it started on");
                    Assert(pasted.Count == transcribed.Count && pasted[^1] == expectedText,
                        $"{label}: pasted '{pasted.LastOrDefault()}', expected '{expectedText}' ({expected.Name}'s language and trailing-period rule)");
                    var history = HistoryLabel(audioPath);
                    Assert(history == expected.Name, $"{label}: History labels the row '{history}', expected '{expected.Name}'");
                }

                // The changeMode hotkey goes through CycleMode, Alpha -> Beta.
                Dictate("hotkey switch", () => Assert((bool)cycleMode.Invoke(vm, null)!, "CycleMode refused the switch"),
                    alpha, alphaText);
                Assert(ReferenceEquals(vm.SelectedMode, beta), "the hotkey's switch did not stay selected after the dictation");

                // The tray's Mode submenu goes through TrySelectMode, Beta -> Alpha.
                Dictate("tray switch", () => Assert(vm.TrySelectMode(alpha), "TrySelectMode refused the switch"),
                    beta, betaText);

                // Cancel: nothing is transcribed or pasted, and the next dictation
                // runs on the mode selected when IT starts.
                vm.StartRecordingAsync().GetAwaiter().GetResult();
                Assert(vm.IsRecording && recorder.Recording, "cancel: the dictation did not start");
                Assert(vm.TrySelectMode(beta), "cancel: TrySelectMode refused the switch");
                var transcriptions = transcribed.Count;
                vm.HandleCancelRequest().GetAwaiter().GetResult();
                Assert(!vm.IsRecording && !recorder.Recording, "cancel: the recording is still running");
                Assert(transcribed.Count == transcriptions && pasted.Count == transcriptions,
                    "cancel: a cancelled dictation was transcribed or pasted");
                Dictate("after cancel", () => Assert(vm.TrySelectMode(alpha), "TrySelectMode refused the switch"),
                    beta, betaText);
            }
            finally
            {
                // A failed assertion can leave a dictation running: cancel it, so
                // keep-warm and the audio-environment claim are handed back.
                if (vm?.IsRecording == true)
                {
                    try { vm.HandleCancelRequest().GetAwaiter().GetResult(); }
                    catch (Exception) { /* best effort */ }
                }
                // Every row this check made, including one a failed stop left behind.
                using (var db = new HyperWhisperDbContext())
                {
                    var ids = db.Transcripts.AsNoTracking()
                        .Where(t => t.Mode != null && t.Mode.StartsWith("G2241 "))
                        .Select(t => t.Id)
                        .ToList();
                    foreach (var id in ids) HistoryService.Instance.DeleteTranscript(id);
                }
                foreach (var (audioPath, _) in transcribed) TryDeleteFile(audioPath);
                settings.AutoPasteEnabled = saved.AutoPasteEnabled;
                settings.AutocapitalizeInsert = saved.AutocapitalizeInsert;
                settings.AutoIncreaseMicVolume = saved.AutoIncreaseMicVolume;
                settings.StoreAsM4A = saved.StoreAsM4A;
                settings.MediaControlMode = saved.MediaControlMode;
                settings.EnableSoundEffects = saved.EnableSoundEffects;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    /// <summary>
    /// A microphone for the dictation seam (#2241): each stop hands back a short
    /// WAV of silence, so the stop path saves and transcribes a real file.
    /// </summary>
    private sealed class FakeDictationRecorder : IDictationRecorder
    {
        public bool Recording { get; private set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public long CapturedAudioBytes { get; private set; }

        public void StartRecording(int deviceNumber)
        {
            Recording = true;
            CapturedAudioBytes = 0;
        }

        public Result<string> StopRecording()
        {
            if (!Recording) return Result<string>.Failure("not recording");
            Recording = false;
            Directory.CreateDirectory(AppPaths.ProfileTempRecordingsDirectory);
            var path = Path.Combine(AppPaths.ProfileTempRecordingsDirectory, $"g2241-{Guid.NewGuid():N}.wav");
            using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            {
                var silence = new byte[3200];
                writer.Write(silence, 0, silence.Length);
                CapturedAudioBytes = silence.Length;
            }
            return Result<string>.Success(path);
        }

        public void BoostMicVolume(int deviceNumber) { }
        public void RestoreMicVolume() { }
    }

    private static MicrophoneKeepWarmService NewKeepWarm(List<FakeCapture> captures) =>
        new(device =>
        {
            var capture = new FakeCapture(device);
            captures.Add(capture);
            return capture;
        });

    /// <summary>
    /// A capture that records what keep-warm did to it, and can fail like a pulled
    /// device. Like WaveInEvent it raises RecordingStopped after StopRecording; and
    /// because NAudio raises it on its capture thread, a "late" raise reaches every
    /// handler ever attached, even one keep-warm has since removed.
    /// </summary>
    private sealed class FakeCapture(int device, bool failStart = false) : NAudio.Wave.IWaveIn
    {
        private readonly List<EventHandler<NAudio.Wave.StoppedEventArgs>> _everStopped = [];
        private EventHandler<NAudio.Wave.StoppedEventArgs>? _stopped;

        public int Device { get; } = device;
        public bool Running { get; private set; }
        public bool Disposed { get; private set; }
        public int Stops { get; private set; }
        public NAudio.Wave.WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);

        public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped
        {
            add { _stopped += value; if (value != null) _everStopped.Add(value); }
            remove => _stopped -= value;
        }

        public void StartRecording()
        {
            if (failStart)
                throw new InvalidOperationException("device busy");
            Running = true;
            DataAvailable?.Invoke(this, new NAudio.Wave.WaveInEventArgs(new byte[320], 320));
        }

        public void StopRecording()
        {
            Stops++;
            Running = false;
            RaiseStopped(null, late: true);
        }

        public void Fail(bool late = false)
        {
            Running = false;
            RaiseStopped(new InvalidOperationException("device removed"), late);
        }

        private void RaiseStopped(Exception? error, bool late)
        {
            var args = new NAudio.Wave.StoppedEventArgs(error);
            if (!late)
            {
                _stopped?.Invoke(this, args);
                return;
            }

            foreach (var handler in _everStopped.ToArray())
                handler(this, args);
        }

        public void Dispose()
        {
            Running = false;
            Disposed = true;
        }
    }

    /// <summary>
    /// Esc during a live streaming session (#2242). The real StartStreamingRecordingAsync
    /// opens the session through its factory seams with a fake client and capture, and the
    /// real HandleCancelRequest must discard it: the stop-and-save path never entered, no
    /// History row, the cancelled status. The session has no paste service, so a paste is
    /// not seen directly; the stop path pastes between its transcribing signal and its
    /// History write, and the check asserts neither happened. It replaces the deleted
    /// verify_streaming_cancel_wiring.ps1, which matched source text. The second check is
    /// the control: the same session stopped through StopStreamingRecordingAsync does
    /// save, so "no row" above is not vacuous.
    /// </summary>
    private static void RunStreamingCancel(Action<string, Action> run)
    {
        run("streaming: Esc during a live session discards the text instead of saving and pasting it (#2242)", () =>
        {
            using var session = StreamingSession.Start();
            var vm = session.Vm;
            Assert(vm.IsRecording && session.Capture.StartedOn == StreamingSession.DeviceNumber && session.Client.Starts == 1,
                "precondition: the fake session did not start (IsRecording, capture on the selected device, one client start)");

            vm.HandleCancelRequest().GetAwaiter().GetResult();

            Assert(session.Client.Stops == 1, $"the cancel stopped the streaming client {session.Client.Stops} times, not once");
            Assert(session.Capture.Stopped && session.Capture.Disposed, "the cancel left the microphone capture running");
            Assert(session.Client.Disposed && session.Client.Handlers == 0 && session.Capture.Handlers == 0,
                "the cancel did not tear the session down (client disposed, every handler removed)");
            // The stop-and-save path announces itself before its first await, and
            // pastes before it writes the streamed text to History; the cancel does neither.
            Assert(session.TranscribingShown == 0 && !session.SawTranscribing,
                "the cancel entered the stop-and-save path (ShowTranscribingRequested / IsTranscribing)");
            Assert(session.SavedRows() == 0, "the cancel saved the streamed text to History");
            Assert(vm.StatusText == MainViewModel.CancelledStatusText(cancelledFileTranscription: false),
                $"the status reads '{vm.StatusText}', not the cancelled text");
            Assert(!vm.IsRecording && !vm.IsTranscribing, "the view model still reports a recording or a transcription");
        });

        run("streaming: the same live session stopped normally does save its text (control for #2242)", () =>
        {
            using var session = StreamingSession.Start();
            var stop = typeof(MainViewModel).GetMethod("StopStreamingRecordingAsync", InstanceField)
                ?? throw new MissingMethodException(nameof(MainViewModel), "StopStreamingRecordingAsync");
            ((Task)stop.Invoke(session.Vm, null)!).GetAwaiter().GetResult();

            Assert(session.Client.Stops == 1, $"the stop called the streaming client's StopAsync {session.Client.Stops} times");
            Assert(session.TranscribingShown == 1, "the stop path did not show the transcribing state");
            Assert(session.SavedRows() == 1, "the stop path did not save the streamed text to History, so the cancel check proves nothing");
        });
    }

    /// <summary>
    /// A MainViewModel with a live streaming session started by the real
    /// StartStreamingRecordingAsync over the fakes below. Hermetic: no microphone, no
    /// network, no paste service (so nothing can type into the runner's desktop), History
    /// in the suite's scratch profile, and the settings that would touch the machine's
    /// audio (mic boost, sounds, media muting) off for the duration.
    /// </summary>
    private sealed class StreamingSession : IDisposable
    {
        public const int DeviceNumber = 7;
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly (bool Boost, bool Sounds, string Media) _previousSettings;
        private readonly List<IDisposable> _owned = [];
        // The streamed text goes through the shared core's finalizer, which may recase
        // or punctuate it, so the History lookup matches on the GUID in its middle.
        private readonly string _marker = Guid.NewGuid().ToString("N");

        public MainViewModel Vm { get; private set; } = null!;
        public FakeStreamingClient Client { get; }
        public FakeStreamingSource Capture { get; } = new();
        public int TranscribingShown { get; private set; }
        public bool SawTranscribing { get; private set; }

        private StreamingSession()
        {
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();
            // The suite's thread can hold a DispatcherSynchronizationContext no loop pumps;
            // the stop path's History insert resumes on it and would hang (see Program.cs).
            SynchronizationContext.SetSynchronizationContext(null);

            var settings = SettingsService.Instance;
            _previousSettings = (settings.AutoIncreaseMicVolume, settings.EnableSoundEffects, settings.MediaControlMode);
            settings.AutoIncreaseMicVolume = false;
            settings.EnableSoundEffects = false;
            settings.MediaControlMode = "off";
            Client = new FakeStreamingClient($"Streamed text for 2242, {_marker} end");
        }

        private void Wire()
        {
            var settings = SettingsService.Instance;
            Vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            SetField(Vm, "_settingsService", settings);
            SetField(Vm, "_vocabularyService", VocabularyService.Instance);
            SetField(Vm, "_recorderService", Own(new AudioRecorderService()));
            SetField(Vm, "_transcriptionService", Own(new TranscriptionService()));
            SetField(Vm, "_parakeetTranscriptionService", new ParakeetTranscriptionService());
            SetField(Vm, "_pushToTalkMonitor", Own(new PushToTalkMonitor()));
            SetField(Vm, "_shortcutService", Own(new KeyboardShortcutService()));
            SetField(Vm, "_selectedAudioDevice", new AudioDeviceService.AudioDevice(DeviceNumber, "G2242 Microphone"));
            Vm.StreamingClientFactoryOverride = _ => Result<IStreamingTranscriptionClient>.Success(Client);
            Vm.StreamingAudioSourceFactoryOverride = () => Capture;
            Vm.ShowTranscribingRequested += (_, _) => TranscribingShown++;
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsTranscribing) && Vm.IsTranscribing)
                    SawTranscribing = true;
            };
        }

        public static StreamingSession Start()
        {
            var session = new StreamingSession();
            try
            {
                session.Wire();
                var start = typeof(MainViewModel).GetMethod("StartStreamingRecordingAsync", InstanceField)
                    ?? throw new MissingMethodException(nameof(MainViewModel), "StartStreamingRecordingAsync");
                ((Task)start.Invoke(session.Vm, null)!).GetAwaiter().GetResult();
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>History rows holding this session's streamed text.</summary>
        public int SavedRows()
        {
            using var db = new HyperWhisperDbContext();
            return db.Transcripts.AsNoTracking().Count(t => t.Text != null && t.Text.Contains(_marker));
        }

        private T Own<T>(T disposable) where T : IDisposable
        {
            _owned.Add(disposable);
            return disposable;
        }

        public void Dispose()
        {
            try
            {
                // A session the check left running (it failed before its cancel/stop).
                if (Vm != null)
                    (typeof(MainViewModel).GetField("_durationTimer", InstanceField)?.GetValue(Vm) as IDisposable)?.Dispose();
                using (var db = new HyperWhisperDbContext())
                {
                    foreach (var id in db.Transcripts.AsNoTracking().Where(t => t.Text != null && t.Text.Contains(_marker)).Select(t => t.Id).ToList())
                        HistoryService.Instance.DeleteTranscript(id);
                }
                foreach (var owned in _owned)
                    owned.Dispose();
            }
            finally
            {
                var settings = SettingsService.Instance;
                settings.AutoIncreaseMicVolume = _previousSettings.Boost;
                settings.EnableSoundEffects = _previousSettings.Sounds;
                settings.MediaControlMode = _previousSettings.Media;
                SynchronizationContext.SetSynchronizationContext(_previousContext);
            }
        }
    }

    /// <summary>
    /// A streaming client that connects at once and returns <paramref name="finalText"/>
    /// from StopAsync, as a provider would after the user spoke. Handlers counts the
    /// view model's live subscriptions, so a check sees the session cleanup run.
    /// </summary>
    private sealed class FakeStreamingClient(string finalText) : IStreamingTranscriptionClient
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public int Handlers { get; private set; }
        public StreamingConnectionState State { get; private set; } = StreamingConnectionState.Idle;
        public int AudioSampleRate => 16000;

        public event Action<StreamingConnectionState>? StateChanged { add => Handlers++; remove => Handlers--; }
        public event Action<string>? FinalTranscriptSegmentReceived { add => Handlers++; remove => Handlers--; }
        public event Action<string>? WarningReceived { add => Handlers++; remove => Handlers--; }
        public event Action<string>? ErrorReceived { add => Handlers++; remove => Handlers--; }
        public event Action<double, double>? SessionCompleted { add => Handlers++; remove => Handlers--; }

        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            State = StreamingConnectionState.Streaming;
            return Task.FromResult(true);
        }

        public Task<string> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            State = StreamingConnectionState.Idle;
            return Task.FromResult(finalText);
        }

        public Task SendAudioAsync(byte[] pcmData, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A microphone capture that records what the view model did to it.</summary>
    private sealed class FakeStreamingSource : IStreamingAudioSource
    {
        public int? StartedOn { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public int Handlers { get; private set; }
        public TimeSpan Duration => TimeSpan.Zero;

        public event Action<byte[]>? AudioChunkAvailable { add => Handlers++; remove => Handlers--; }
        public event Action<float>? AudioLevelChanged { add => Handlers++; remove => Handlers--; }

        public void Start(int deviceNumber, int sampleRate) => StartedOn = deviceNumber;
        public void Stop() => Stopped = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class WhitespaceProvider : ITranscriptionProvider
    {
        public const string ProviderName = "G830 Silent Provider";
        public bool IsAvailable => true;
        public string Name => ProviderName;

        public Task<string> TranscribeAsync(
            string audioPath,
            string? language = null,
            IReadOnlyList<string>? vocabulary = null,
            CancellationToken cancellationToken = default) => Task.FromResult(" \t\r\n ");
    }

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, InstanceField) ?? throw new MissingFieldException(target.GetType().Name, name))
            .SetValue(target, value);

    /// <summary>
    /// Runs <paramref name="write"/> on a worker while a second connection holds a reader
    /// paused mid-read on the scratch database, and lets the reader go after a second
    /// (#2430). WaitedForReader: the write was still running when the reader let go.
    /// Every wait is bounded and the reader is released in finally, so a regression
    /// fails the check instead of hanging windows-ci.
    /// </summary>
    private static (Exception? Failure, bool WaitedForReader) WriteWhileAReaderHolds(Action write)
    {
        string databasePath;
        using (var context = new HyperWhisperDbContext())
            databasePath = context.Database.GetDbConnection().DataSource;

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Transcripts";
        Microsoft.Data.Sqlite.SqliteDataReader? reader = command.ExecuteReader();
        Task? worker = null;
        var waited = false;
        try
        {
            Assert(reader.Read(), "the reader found no row to pause on");
            // The worker must not capture the smoke suite's WPF context (#2366).
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                worker = Task.Run(write);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            // Task.Wait would rethrow a fault here; the fault is read once below.
            waited = !((IAsyncResult)worker).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }
        finally
        {
            reader.Dispose();
            reader = null;
        }

        if (!((IAsyncResult)worker!).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(45)))
            return (new TimeoutException("the write did not finish within 45 s of the reader letting go"), waited);
        return (worker.Exception?.GetBaseException(), waited);
    }

    /// <summary>
    /// Why recovery returned 0 (#2430). It logs and swallows a refused write, and it also
    /// returns 0 when no row is Processing in the database, which happens when the orphan
    /// was kept in the pending store instead (#1589). The two read the same to the check.
    /// </summary>
    private static string RecoveryZeroCause(HistoryService history, Guid orphanId)
    {
        var failure = history.LastRecoveryFailure;
        var cause = failure == null ? "it threw nothing" : $"it failed with {DescribeCause(failure)}";
        var pending = history.GetPendingTranscriptsSnapshot();
        var orphanPending = pending.Any(t => t.Id == orphanId) ? "is" : "is not";
        return $"{cause}; {pending.Count} pending row(s), and the orphan {orphanPending} one of them";
    }

    /// <summary>The exception and the innermost one, e.g. "DbUpdateException: SqliteException: ... locked".</summary>
    private static string DescribeCause(Exception? ex) => ex == null
        ? "none recorded"
        : ex.GetBaseException() == ex
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
