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
        run("history: audio deletion stays inside the trusted recording roots (#830, was scripts/verify_storage_delete_guard.ps1 + verify_isolated_storage_cleanup.ps1)", () =>
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
                Assert(deleted.IsSuccess && deleted.Value, $"the row was not deleted: {deleted.Error}");
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

        run("local api: only a socket bind failure on a remembered port retries on an ephemeral port (#830, was scripts/verify_local_api_bind_fallback.ps1)", () =>
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

        run("transcription: a repeated-phrase loop collapses to one copy, ordinary text is untouched (#830, was scripts/verify_long_recording_repetition_guard.ps1)", () =>
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
            Assert(TranscriptionService.CollapseRepetitionLoops(shortText) == shortText, "text with fewer than three repeats changed");
        });

        run("streaming: a multi-channel mic downmixes at 1/sqrt(n) and clamps (#830, was scripts/verify_streaming_downmix_scaling.ps1)", () =>
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

        run("orchestrator: whitespace from the engine is NoSpeechDetected and names the engine (#830, was scripts/verify_no_speech_terminal_status_wiring.ps1)", () =>
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

        run("history: startup recovery turns a dead session's Processing row into a retryable Failed row (#830, was scripts/verify_no_speech_terminal_status_wiring.ps1)", () =>
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
                Assert(recovered >= 1, $"recovery reported {recovered} rows");
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

        run("models: deleting a Whisper or Parakeet model whose file is in use fails as a Result and keeps it (#830, was scripts/verify_model_delete_failure_handling.ps1)", () =>
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

        run("prompt: each detected app type puts its own context block in the AI prompt (#830, was scripts/verify_app_classification.ps1)", () =>
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

        run("streaming: only HyperWhisper Cloud streams without the user's own API key (#830, was scripts/verify_streaming_failure_wiring.ps1)", () =>
        {
            // A direct provider marked keyless would start a session with no key and
            // fail on the wire; Cloud marked keyed would ask a subscriber for a key.
            foreach (var provider in Enum.GetValues<StreamingTranscriptionProvider>())
            {
                Assert(provider.RequiresApiKey() == (provider != StreamingTranscriptionProvider.HyperWhisperCloud),
                    $"{provider}: RequiresApiKey() is {provider.RequiresApiKey()}");
            }
        });

        run("local api: a /transcribe file reached through a junction under a trusted root is refused (#830, #740, was scripts/verify_local_api_transcribe_contract.ps1)", () =>
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

        run("file transcription: a cancelled conversion throws OperationCanceledException and leaves no temp WAV (#830, was scripts/verify_transcription_cancellation_wiring.ps1)", () =>
        {
            // Escape during a file transcription must end silently as a cancel. A
            // cancel reported as a Failure shows an error toast, and a converted
            // temp WAV left behind fills %TEMP%.
            var source = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"g830-cancel-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            HashSet<string> Converted() => Directory.EnumerateFiles(Path.GetTempPath(), "hyperwhisper_converted_*.wav").ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                // 44.1 kHz stereo, so the call would convert rather than pass through.
                using (var writer = new NAudio.Wave.WaveFileWriter(source, new NAudio.Wave.WaveFormat(44100, 16, 2)))
                    writer.Write(new byte[44100 * 4], 0, 44100 * 4);

                var before = Converted();
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Exception? thrown = null;
                try
                {
                    var result = FileTranscriptionService.ConvertToWhisperFormatAsync(source, cts.Token).GetAwaiter().GetResult();
                    Assert(false, $"a cancelled conversion returned a result (success {result.IsSuccess}, error '{result.Error}')");
                }
                catch (OperationCanceledException ex)
                {
                    thrown = ex;
                }
                Assert(thrown != null, "a cancelled conversion did not throw OperationCanceledException");
                var leaked = Converted().Except(before).ToList();
                Assert(leaked.Count == 0, $"a cancelled conversion left {string.Join(", ", leaked)}");
            }
            finally
            {
                TryDeleteFile(source);
            }
        });

        run("tray: Start/Stop item text and enabled state, and the empty mode submenus (#830, was scripts/verify_tray_menu_wiring.ps1)", () =>
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
