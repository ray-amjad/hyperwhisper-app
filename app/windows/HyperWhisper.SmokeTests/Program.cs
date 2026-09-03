// SMOKE TESTS
//
// Fast, dependency-free checks run in release CI (windows-release.yml) after the
// Rust core DLL is built and before installers are produced:
//   1. FFI touch — the FIRST call into hyperwhisper_core.dll trips the UniFFI
//      contract-version + API-checksum verification (uniffiCheckApiChecksums),
//      catching generated-binding ↔ DLL drift.
//   2. Unit-style checks over the pieces the 1.8.0 release review flagged as
//      highest-risk: RustRetry transport/timeout caps + per-attempt client
//      resolution + cancellation ordering, Grok's size-scaled timeout, the
//      hardened vocabulary replacement, phonetic matching, and the Parakeet
//      reload predicates.
//   3. The original WPF initialization smoke (BackupExportSettingsPage).
//
// Requires InternalsVisibleTo("HyperWhisper.SmokeTests") in HyperWhisper.csproj.
// Coverage is x64-only on the CI runner; ARM64 is exercised manually.

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Converters;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.AppClassification;
using HyperWhisper.Services.AppClassification;
using HyperWhisper.Services.LocalApi;
using HyperWhisper.Services.Onboarding;
using HyperWhisper.Services.Platform;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Services.Transcription;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels;
using HyperWhisper.ViewModels.Onboarding;
using HyperWhisper.Views.Controls;
using HyperWhisper.Views.Controls.Onboarding;
using HyperWhisper.Views.Pages.Onboarding;
using HyperWhisper.Views.Pages.Settings;
using HyperWhisper.Views.Windows;
// Safe to import, unlike Microsoft.AspNetCore.Http: none of these collide with
// the UniFFI binding's own type names. The Local API size-limit checks build a
// real Kestrel listener, which needs the builder, the host and the logging
// extension methods.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using uniffi.hyperwhisper_core;
using PlatformContracts = HyperWhisper.Platform.Abstractions;
// Aliased, not imported: HyperWhisper.SharedCore also declares
// CloudTranscriptionProvider, which would clash with HyperWhisper.Models.
using LlmPostProcessing = HyperWhisper.SharedCore.LlmPostProcessing;
using PortableEndpointStatus = HyperWhisper.SharedCore.PortableEndpointStatus;
using PortableLlmProvider = HyperWhisper.SharedCore.PortableLlmProvider;
using PortableLlmRequest = HyperWhisper.SharedCore.PortableLlmRequest;
using PortableModeIdentity = HyperWhisper.SharedCore.PortableModeIdentity;
using PortableNoSpeechDiagnostics = HyperWhisper.SharedCore.PortableNoSpeechDiagnostics;
using PortableNoSpeechInput = HyperWhisper.SharedCore.PortableNoSpeechInput;
using PortableNoSpeechOutcome = HyperWhisper.SharedCore.PortableNoSpeechOutcome;
using PortableSignalAccumulation = HyperWhisper.SharedCore.PortableSignalAccumulation;

namespace HyperWhisper.SmokeTests;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HyperWhisper.SmokeTests",
            Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, tempRoot);

        // A scratch profile has no settings.json, so ApplyDefaults seeds
        // OnboardingPending = true — which is exactly how the dev-box end-to-end
        // test makes the first-run flow appear. Today's suite never constructs
        // HyperWhisper.App or MainWindow (it builds its own bare Application), so
        // nothing here can trip the modal; this is set so that a future harness
        // which DOES boot the real App stays safe.
        Environment.SetEnvironmentVariable(OnboardingLaunchPolicy.SkipEnvironmentVariable, "1");

        try
        {
            Directory.CreateDirectory(tempRoot);

            // FFI touch FIRST: the first call into the native DLL runs the UniFFI
            // contract-version + checksum handshake — binding↔DLL drift fails
            // here, before anything else can produce a confusing error.
            Run("ffi checksum touch (CloudSttCreditsPerMinute)", () =>
            {
                var credits = HyperwhisperCoreMethods.CloudSttCreditsPerMinute("balanced");
                Assert(credits >= 0, $"expected credits/min >= 0, got {credits}");
            });

            Run("Windows shortcut seam round-trips WPF keys losslessly", () =>
            {
                foreach (var key in new[] { Key.A, Key.D9, Key.F24, Key.OemPeriod, Key.Return })
                {
                    var windows = new KeyboardShortcut
                    {
                        Control = true,
                        Alt = true,
                        Shift = true,
                        Win = true,
                        Key = key
                    };

                    var portable = WindowsShortcutMapper.ToPlatform(windows);
                    var roundTrip = WindowsShortcutMapper.FromPlatform(portable);
                    Assert(roundTrip.IsSuccess, roundTrip.Error?.Message ?? $"failed to map {key}");
                    Assert(roundTrip.Value == windows, $"{key} did not round-trip");
                }

                var unsupported = WindowsShortcutMapper.FromPlatform(
                    new PlatformContracts.GlobalShortcut(
                        PlatformContracts.ShortcutModifiers.Control,
                        new PlatformContracts.ShortcutKeyCode("NotAWindowsKey")));
                Assert(unsupported.IsFailure, "unknown platform key should fail explicitly");
                Assert(unsupported.Error?.Code == "shortcut.unsupported_key", "unexpected error code");
            });

            Run("Windows push-to-talk seam round-trips every modifier", () =>
            {
                foreach (var modifier in Enum.GetValues<PlatformContracts.ModifierSide>())
                {
                    var portable = new PlatformContracts.PushToTalkConfiguration(
                        PlatformContracts.PushToTalkMode.Modifier,
                        modifier,
                        DoublePressLock: true);
                    var windows = WindowsShortcutMapper.FromPlatform(portable);
                    Assert(windows.IsSuccess, windows.Error?.Message ?? $"failed to map {modifier}");

                    var roundTrip = WindowsShortcutMapper.ToPlatform(windows.Value!);
                    Assert(roundTrip.Modifier == modifier, $"{modifier} did not round-trip");
                    Assert(roundTrip.DoublePressLock, "double-press lock was lost");
                }

                Assert(
                    typeof(PlatformContracts.IGlobalShortcutService)
                        .IsAssignableFrom(typeof(KeyboardShortcutService)),
                    "KeyboardShortcutService does not implement the portable contract");
                Assert(
                    typeof(PlatformContracts.IPushToTalkMonitor)
                        .IsAssignableFrom(typeof(PushToTalkMonitor)),
                    "PushToTalkMonitor does not implement the portable contract");
            });

            Run("Windows application-context seam preserves portable fields", () =>
            {
                var windows = new Services.ApplicationContext
                {
                    ProcessName = "sample-app",
                    WindowTitle = "Sample window",
                    Category = "Communication",
                    BrowserTabTitle = "Sample tab",
                    BrowserHost = "example.invalid",
                    FocusedElementType = "TextField",
                    FocusedContent = "selected text",
                    TextFormat = "text",
                    AppType = AppType.WorkMessaging,
                    AppTypeConfidence = "strong",
                    AppTypeSource = "processName",
                    ScreenOCRText = "visible text"
                };

                var portable = WindowsApplicationContextMapper.ToPlatform(windows);
                Assert(portable.ProcessName == windows.ProcessName, "process name was lost");
                Assert(portable.BrowserHost == windows.BrowserHost, "browser host was lost");
                Assert(portable.FocusedContent == windows.FocusedContent, "focused content was lost");
                Assert(portable.AppType == "work_messaging", "app type was not canonicalized");
                Assert(portable.ScreenOcrText == windows.ScreenOCRText, "OCR text was lost");
                Assert(
                    typeof(PlatformContracts.IApplicationContextProvider)
                        .IsAssignableFrom(typeof(ApplicationContextService)),
                    "ApplicationContextService does not implement the portable contract");
            });

            Run("Windows text-injection seam maps every outcome", () =>
            {
                Assert(
                    WindowsTextInjectionMapper.ToPlatform(SmartPasteResult.Pasted)
                        == PlatformContracts.TextInjectionOutcome.Pasted,
                    "pasted outcome was lost");
                Assert(
                    WindowsTextInjectionMapper.ToPlatform(SmartPasteResult.CopiedToClipboard)
                        == PlatformContracts.TextInjectionOutcome.CopiedToClipboard,
                    "clipboard outcome was lost");
                Assert(
                    WindowsTextInjectionMapper.ToPlatform(SmartPasteResult.SecureFieldSkipped)
                        == PlatformContracts.TextInjectionOutcome.SecureFieldSkipped,
                    "secure-field outcome was lost");
                Assert(
                    WindowsTextInjectionMapper.ToPlatform(SmartPasteResult.Failed)
                        == PlatformContracts.TextInjectionOutcome.Failed,
                    "failure outcome was lost");
                Assert(
                    typeof(PlatformContracts.ITextInjectionService)
                        .IsAssignableFrom(typeof(SmartPasteService)),
                    "SmartPasteService does not implement the portable contract");
            });

            Run("Every paste outcome has a slug, and the reportable set is the failure set", () =>
            {
                var slugs = new HashSet<string>(StringComparer.Ordinal);

                foreach (PasteOutcome outcome in Enum.GetValues<PasteOutcome>())
                {
                    // Throws if an outcome was added without a slug arm, which is
                    // the failure this test exists to catch: an unslugged outcome
                    // would otherwise report under another outcome's identity.
                    var slug = SmartPasteDiagnostics.Slug(outcome);

                    Assert(!string.IsNullOrWhiteSpace(slug), $"{outcome} has an empty slug");
                    Assert(slugs.Add(slug), $"slug '{slug}' is used by more than one outcome");
                    Assert(
                        slug == slug.ToLowerInvariant(),
                        $"slug '{slug}' is not a stable lower-case slug");
                }

                // A delivered transcript and a deliberate refusal must never open a
                // Sentry issue: the first is not a fault, and the second is normal
                // and frequent enough to flood the issue stream.
                Assert(!SmartPasteDiagnostics.IsReportable(PasteOutcome.Pasted), "success is reported as a failure");
                Assert(!SmartPasteDiagnostics.IsReportable(PasteOutcome.ClipboardOnly), "clipboard-only is reported as a failure");
                Assert(!SmartPasteDiagnostics.IsReportable(PasteOutcome.SecureFieldSkipped), "secure-field refusal is reported as a failure");

                // Every way the transcript fails to arrive must be reportable.
                Assert(SmartPasteDiagnostics.IsReportable(PasteOutcome.EmptyText), "empty text is not reported");
                Assert(SmartPasteDiagnostics.IsReportable(PasteOutcome.ClipboardSetFailed), "clipboard failure is not reported");
                Assert(SmartPasteDiagnostics.IsReportable(PasteOutcome.NoTargetWindow), "missing target window is not reported");
                Assert(SmartPasteDiagnostics.IsReportable(PasteOutcome.KeystrokeFailed), "keystroke failure is not reported");

                // Only a fault this app can cause is an error. Everything the
                // user's desktop can cause is a warning, or an elevated window
                // would raise an error-level issue on every app run for ever.
                Assert(SmartPasteDiagnostics.IsDefect(PasteOutcome.EmptyText), "delivering no transcript is not a defect");
                Assert(!SmartPasteDiagnostics.IsDefect(PasteOutcome.KeystrokeFailed), "UIPI refusal is graded as a defect");
                Assert(!SmartPasteDiagnostics.IsDefect(PasteOutcome.ClipboardSetFailed), "clipboard conflict is graded as a defect");
                Assert(!SmartPasteDiagnostics.IsDefect(PasteOutcome.NoTargetWindow), "missing target window is graded as a defect");

                // The flood guard: streaming pastes once per final segment, so a
                // broken target must not send one Sentry event per sentence.
                // A slug no outcome uses, so claiming it cannot mask a real report.
                const string probe = "smoke_test_probe_outcome";
                Assert(SmartPasteDiagnostics.MarkReportedThisRun(probe), "first report of a slug was suppressed");
                Assert(!SmartPasteDiagnostics.MarkReportedThisRun(probe), "second report of a slug was not suppressed");
            });

            Run("Log path description names the extension and never the file", () =>
            {
                Assert(LoggingService.DescribePath(null) == "(none)", "null path was not described");
                Assert(LoggingService.DescribePath("   ") == "(none)", "blank path was not described");
                Assert(
                    LoggingService.DescribePath(@"C:\Users\someone\Documents\HyperWhisper\a.wav") == "*.wav",
                    "recording path did not reduce to its extension");
                Assert(
                    LoggingService.DescribePath(@"C:\Users\someone\Documents\quarterly-review") == "(no extension)",
                    "extensionless path was not described");

                // The point of the helper: nothing that can name the user or their
                // document survives into the log line.
                var described = LoggingService.DescribePath(@"C:\Users\someone\Documents\quarterly-review.m4a");
                Assert(!described.Contains("someone", StringComparison.OrdinalIgnoreCase), "path description leaked the user name");
                Assert(!described.Contains("quarterly", StringComparison.OrdinalIgnoreCase), "path description leaked the file name");
            });

            Run("Windows lifecycle seams implement contracts and isolate activation handlers", () =>
            {
                Assert(
                    typeof(PlatformContracts.IAutostartService)
                        .IsAssignableFrom(typeof(StartupService)),
                    "StartupService does not implement the portable contract");
                Assert(
                    typeof(PlatformContracts.ISingleInstanceCoordinator)
                        .IsAssignableFrom(typeof(WindowsSingleInstanceCoordinator)),
                    "WindowsSingleInstanceCoordinator does not implement the portable contract");

                var coordinator = WindowsSingleInstanceCoordinator.Instance;
                var goodHandlerCalled = false;
                EventHandler badHandler = (_, _) => throw new InvalidOperationException("expected test failure");
                EventHandler goodHandler = (_, _) => goodHandlerCalled = true;
                try
                {
                    coordinator.ActivationRequested += badHandler;
                    coordinator.ActivationRequested += goodHandler;
                    coordinator.NotifyActivationRequested();
                    Assert(goodHandlerCalled, "one bad activation subscriber blocked another");
                }
                finally
                {
                    coordinator.ActivationRequested -= badHandler;
                    coordinator.ActivationRequested -= goodHandler;
                }
            });

            Run("Windows audio seams implement portable contracts", () =>
            {
                Assert(typeof(PlatformContracts.IAudioInputDeviceService)
                    .IsAssignableFrom(typeof(WindowsAudioInputDeviceService)), "audio device adapter contract missing");
                Assert(typeof(PlatformContracts.IAudioRecorder)
                    .IsAssignableFrom(typeof(WindowsAudioRecorder)), "audio recorder adapter contract missing");
                Assert(typeof(PlatformContracts.IStreamingAudioCapture)
                    .IsAssignableFrom(typeof(WindowsStreamingAudioCapture)), "streaming capture adapter contract missing");
                Assert(typeof(PlatformContracts.IMicrophoneKeepWarmService)
                    .IsAssignableFrom(typeof(WindowsMicrophoneKeepWarmService)), "keep-warm adapter contract missing");
                Assert(typeof(PlatformContracts.IAudioPlaybackService)
                    .IsAssignableFrom(typeof(WindowsAudioPlaybackService)), "playback adapter contract missing");
                Assert(typeof(PlatformContracts.ISoundEffectsService)
                    .IsAssignableFrom(typeof(WindowsSoundEffectsService)), "sound-effects adapter contract missing");
                Assert(WindowsAudioRecorder.TryGetDeviceNumber("-1", out var defaultDevice) && defaultDevice == -1,
                    "default WaveIn device ID did not round-trip");
                Assert(!WindowsAudioRecorder.TryGetDeviceNumber("not-a-device", out _),
                    "invalid WaveIn device ID was accepted");
            });

            Run("Windows system seams preserve portable metadata", () =>
            {
                var windowsGpu = new GpuInfoService.GpuInfo
                {
                    Name = "Test GPU",
                    DedicatedVramBytes = 8L * 1024 * 1024 * 1024,
                    SharedMemoryBytes = 16L * 1024 * 1024 * 1024,
                    IsDiscrete = true
                };
                var portableGpu = WindowsGpuInfoProvider.ToPlatform(windowsGpu);
                Assert(portableGpu.Name == windowsGpu.Name, "GPU name was lost");
                Assert(portableGpu.DedicatedMemoryBytes == windowsGpu.DedicatedVramBytes, "dedicated GPU memory was lost");
                Assert(portableGpu.SharedMemoryBytes == windowsGpu.SharedMemoryBytes, "shared GPU memory was lost");
                Assert(portableGpu.IsDiscrete, "discrete GPU classification was lost");

                Assert(typeof(PlatformContracts.IGpuInfoProvider)
                    .IsAssignableFrom(typeof(WindowsGpuInfoProvider)), "GPU adapter contract missing");
                Assert(typeof(PlatformContracts.IScreenOcrService)
                    .IsAssignableFrom(typeof(ScreenOCRCaptureService)), "screen OCR contract missing");
                Assert(typeof(PlatformContracts.IAppPaths)
                    .IsAssignableFrom(typeof(WindowsAppPaths)), "app paths contract missing");
                Assert(typeof(PlatformContracts.IDeviceIdentityProvider)
                    .IsAssignableFrom(typeof(WindowsDeviceIdentityProvider)), "device identity contract missing");

                PlatformContracts.IAppPaths paths = new WindowsAppPaths();
                Assert(paths.DataDirectory == AppPaths.AppDataRoot, "data directory mapping changed");
                Assert(paths.LogsDirectory == AppPaths.LogsDirectory, "logs directory mapping changed");
                Assert(paths.RecordingsDirectory == AppPaths.ProfileRecordingsDirectory, "recordings directory mapping changed");
            });

            Run("Windows private files are atomic, owner-only, and handle-canonicalized", () =>
            {
                var directory = Path.Combine(tempRoot, "private-files");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "state.bin");
                var service = new WindowsPrivateFileService();
                Assert(service.WriteAllBytesAtomically(path, new byte[] { 1, 2, 3 }).IsSuccess, "initial private write failed");
                Assert(service.WriteAllBytesAtomically(path, new byte[] { 4, 5 }).IsSuccess, "atomic replacement failed");
                Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 4, 5 }), "atomic replacement content mismatch");
                var restricted = service.IsRestrictedToCurrentUser(path);
                Assert(restricted.IsSuccess && restricted.Value, restricted.Error?.Message ?? "private ACL was not owner-only");
                Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "private write left a temporary file");
                using var openFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var canonical = new WindowsFileCanonicalizer().GetCanonicalPath(openFile);
                Assert(canonical.IsSuccess, canonical.Error?.Message ?? "handle canonicalization failed");
                Assert(string.Equals(canonical.Value, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase),
                    $"canonical path mismatch: {canonical.Value}");
            });

            Run("Windows credential adapter round-trips arbitrary bytes without a real vault", () =>
            {
                var backend = new InMemoryCredentialBackend();
                var store = new WindowsCredentialStore(backend);
                var missing = store.Read("test-resource", "test-account");
                Assert(missing.IsSuccess && missing.Value == null, "missing credential was not distinguished from failure");
                var expected = new byte[] { 0, 1, 127, 128, 255 };
                Assert(store.Write("test-resource", "test-account", expected).IsSuccess, "credential write failed");
                var read = store.Read("test-resource", "test-account");
                Assert(read.IsSuccess && read.Value!.SequenceEqual(expected), "credential bytes did not round-trip");
                Assert(backend.StoredValue?.StartsWith("hyperwhisper-bytes-v1:", StringComparison.Ordinal) == true,
                    "credential encoding was not explicit/versioned");
                Assert(store.Delete("test-resource", "test-account").IsSuccess, "credential delete failed");
                Assert(store.Read("test-resource", "test-account").Value == null, "credential was not deleted");
                backend.ThrowOnRead = true;
                Assert(store.Read("test-resource", "test-account").IsFailure, "backend error was mistaken for missing credential");
            });

            Run("Windows runtime locator and child-process lifecycle are deterministic", () =>
            {
                var runtimeRoot = Path.Combine(tempRoot, "runtime-locator");
                var nativeDirectory = Path.Combine(runtimeRoot, "runtimes", "win-x64", "native");
                Directory.CreateDirectory(nativeDirectory);
                var whisperPath = Path.Combine(nativeDirectory, "whisper.dll");
                File.WriteAllBytes(whisperPath, Array.Empty<byte>());
                var engineDirectory = Path.Combine(runtimeRoot, "parakeet-engine");
                Directory.CreateDirectory(engineDirectory);
                var enginePath = Path.Combine(engineDirectory, "parakeet-engine.exe");
                File.WriteAllBytes(enginePath, Array.Empty<byte>());
                var locator = new WindowsNativeRuntimeLocator(runtimeRoot, System.Runtime.InteropServices.Architecture.X64);
                Assert(locator.FindLibrary("whisper", PlatformContracts.NativeComputeBackend.Cpu).Value == whisperPath,
                    "Whisper runtime path mismatch");
                Assert(locator.FindExecutable("parakeet-engine").Value == enginePath, "engine runtime path mismatch");
                Assert(locator.FindLibrary("whisper", PlatformContracts.NativeComputeBackend.Vulkan).IsFailure,
                    "Windows locator falsely claimed Vulkan support");
                Assert(!locator.Capabilities.ComputeBackends.Contains(PlatformContracts.NativeComputeBackend.Cuda),
                    "Windows locator falsely claimed CUDA support");

                var launcher = new WindowsChildProcessLauncher();
                var started = launcher.Start(new PlatformContracts.ChildProcessStartRequest
                {
                    ExecutablePath = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
                    Arguments = new[] { "/d", "/c", "ping -n 30 127.0.0.1 > nul" }
                });
                Assert(started.IsSuccess, started.Error?.Message ?? "child process did not start");
                var child = started.Value!;
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    child.WaitForExitAsync(cancelled.Token).AsTask().GetAwaiter().GetResult();
                    Assert(false, "cancelled process wait completed successfully");
                }
                catch (OperationCanceledException) { }
                child.TerminateAsync().AsTask().GetAwaiter().GetResult();
                Assert(child.HasExited, "terminated process remained alive");
                child.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });

            Run("WPF dispatcher seam invokes inline on its owning thread", () =>
            {
                PlatformContracts.IUiDispatcher dispatcher = new WpfUiDispatcher();
                Assert(dispatcher.CheckAccess(), "STA smoke thread does not own its dispatcher");
                var invoked = false;
                dispatcher.InvokeAsync(() =>
                {
                    invoked = true;
                    return ValueTask.CompletedTask;
                }).AsTask().GetAwaiter().GetResult();
                Assert(invoked, "dispatcher did not invoke the callback");
            });

            Run("ApplyHardenedReplacement keeps $-tokens literal", () =>
            {
                var result = HyperwhisperCoreMethods.ApplyHardenedReplacement(
                    "use claude code", "claude code", "X $0 $& $$");
                Assert(result == "use X $0 $& $$", $"got '{result}'");
            });

            Run("PhoneticEncode returns codes", () =>
            {
                var codes = HyperwhisperCoreMethods.PhoneticEncode("whisper");
                Assert(codes.Count > 0, "expected non-empty phonetic codes");
            });

            // The matcher itself now lives in hw-phonetic (#283); this drives the
            // bridge the local provider calls, so the entry mapping and the
            // match list are covered too, not just the raw FFI.
            Run("ApplyPhoneticVocabulary corrects a misrecognition", () =>
            {
                var result = HyperWhisper.SharedCore.SharedCoreBridge.ApplyPhoneticVocabulary(
                    "hyper wisper",
                    [new HyperWhisper.SharedCore.PortableVocabularyEntry("Whisper", null)]);
                Assert(result.Text == "hyper Whisper", $"got '{result.Text}'");
                Assert(result.EntryCount == 1, $"got entry count {result.EntryCount}");
                Assert(result.Matches.Count == 1, $"got {result.Matches.Count} matches");
                Assert(result.Matches[0].Token == "wisper", $"got token '{result.Matches[0].Token}'");
            });

            // NEW ON WINDOWS (#283): the unanchored, diacritic-insensitive pass
            // the four macOS local providers have always run. The search word is
            // unaccented and the text is decomposed, and the "Zo\u00EB" that is
            // NOT matched keeps its diaeresis and its capital. That is the whole
            // reason the core maps folded byte offsets back to the original
            // instead of returning the folded string.
            Run("ApplySubstringVocabulary matches through an accent", () =>
            {
                var result = HyperWhisper.SharedCore.SharedCoreBridge.ApplySubstringVocabulary(
                    "Zo\u00EB went to the Cafe\u0301 today",
                    [new HyperWhisper.SharedCore.PortableVocabularyEntry("cafe", "Coffee House")]);
                Assert(
                    result == "Zo\u00EB went to the Coffee House today",
                    $"got '{result}'");
            });

            Run("IsNoSpaceLanguage / NormalizeLanguage truth tables", () =>
            {
                foreach (var code in new[] { "ja", "zh", "ko", "yue" })
                    Assert(ParakeetTranscriptionService.IsNoSpaceLanguage(code), $"{code} should be no-space");
                foreach (var code in new[] { "en", "fr", "auto" })
                    Assert(!ParakeetTranscriptionService.IsNoSpaceLanguage(code), $"{code} should be spaced");

                Assert(ParakeetTranscriptionService.NormalizeLanguage(null) == "auto", "null → auto");
                Assert(ParakeetTranscriptionService.NormalizeLanguage("  ") == "auto", "whitespace → auto");
                Assert(ParakeetTranscriptionService.NormalizeLanguage("AUTO") == "auto", "AUTO → auto");
                Assert(ParakeetTranscriptionService.NormalizeLanguage(" EN ") == "en", "' EN ' → en");
            });

            // The daemon's join has THREE classes since #286, so the respawn
            // predicate cannot be a two-valued IsNoSpaceLanguage comparison: "en"
            // and "auto" are both "spaced" to that test, but an auto-language
            // daemon joins Japanese VAD segments with no space at all.
            Run("ResolveJoinClass separates auto from a declared spaced language", () =>
            {
                foreach (var code in new[] { "ja", "zh", "ko", "yue" })
                    Assert(ParakeetTranscriptionService.ResolveJoinClass(code) == "no-space", $"{code} → no-space");
                foreach (var code in new[] { "en", "fr", "de" })
                    Assert(ParakeetTranscriptionService.ResolveJoinClass(code) == "spaced", $"{code} → spaced");
                Assert(ParakeetTranscriptionService.ResolveJoinClass("auto") == "auto", "auto → auto");

                // The three pairs that must be a respawn, and the one that must not.
                string Class(string? language) => ParakeetTranscriptionService.ResolveJoinClass(
                    ParakeetTranscriptionService.NormalizeLanguage(language));
                Assert(Class("en") != Class(null), "en → no language must change class");
                Assert(Class("en") != Class("ja"), "en → ja must change class");
                Assert(Class("auto") != Class("ja"), "auto → ja must change class");
                Assert(Class("en") == Class("fr"), "en → fr must NOT change class");
            });

            Run("Grok GetRequestTimeout scales with file size", () =>
            {
                Assert(GrokSttService.GetRequestTimeout(0) == TimeSpan.FromMinutes(5), "0 bytes → 5min base");
                Assert(GrokSttService.GetRequestTimeout(100L * 1024 * 1024) == TimeSpan.FromMinutes(10),
                    "100MB → 5min + 300s");
                Assert(GrokSttService.GetRequestTimeout(600L * 1024 * 1024) == TimeSpan.FromMinutes(30),
                    "600MB → capped at 30min");
            });

            Run("XaiFormattingLanguages shared between Grok batch and streaming", () =>
            {
                Assert(XaiFormattingLanguages.TryGetSupportedCode("en", out var en) && en == "en", "en supported");
                Assert(XaiFormattingLanguages.TryGetSupportedCode("EN-US", out var enUs) && enUs == "en",
                    "EN-US → en");
                Assert(XaiFormattingLanguages.TryGetSupportedCode("tl", out var tl) && tl == "fil",
                    "tl aliases to fil");
                Assert(!XaiFormattingLanguages.TryGetSupportedCode("auto", out _), "auto unsupported");
                Assert(!XaiFormattingLanguages.TryGetSupportedCode(null, out _), "null unsupported");
                Assert(!XaiFormattingLanguages.TryGetSupportedCode("zz", out _), "unknown code unsupported");
            });

            Run("OpenAI post-processing omits an output-token cap", () =>
            {
                using var body = ReadLlmBody(PortableLlmProvider.OpenAi, "gpt-5.6-luna");

                Assert(!body.RootElement.TryGetProperty("max_tokens", out _),
                    "OpenAI request should not contain max_tokens");
                Assert(!body.RootElement.TryGetProperty("max_completion_tokens", out _),
                    "OpenAI request should not contain max_completion_tokens");
            });

            Run("Retired post-processing models migrate to selectable replacements", () =>
            {
                var cases = new (string OldId, string Replacement)[]
                {
                    ("gpt-4.1-nano", "gpt-5-nano"),
                    ("gemini-3-pro-preview", "gemini-3.1-pro-preview"),
                    ("gemini-3.1-flash-lite-preview", "gemini-3.1-flash-lite"),
                    ("gemini-2.0-flash", "gemini-3.6-flash"),
                    ("gemini-2.0-flash-lite", "gemini-3.1-flash-lite"),
                    ("llama3.1-8b", "gemma-4-31b"),
                    ("qwen-3-235b-a22b-instruct-2507", "gpt-oss-120b"),
                };

                foreach (var (oldId, replacement) in cases)
                {
                    Assert(LanguageModelInfo.MigrateModelId(oldId) == replacement,
                        $"{oldId} should migrate to {replacement}");
                    Assert(LanguageModelInfo.GetById(replacement) != null,
                        $"replacement {replacement} is missing from the picker");
                    Assert(LanguageModelInfo.AvailableModels.All(m => m.Id != oldId),
                        $"retired model {oldId} is still selectable");
                }

                Assert(LanguageModelInfo.GetDefaultForProvider(PostProcessingProvider.OpenAI)?.Id == "gpt-5.6-luna",
                    "new OpenAI modes should default to GPT-5.6 Luna");
            });

            Run("Retired cloud models resolve to selectable canonical models", () =>
            {
                var cases = new (string OldId, CloudTranscriptionProvider Provider, string Replacement)[]
                {
                    ("slam-1", CloudTranscriptionProvider.AssemblyAI, "universal-3-5-pro"),
                    ("universal-3-pro", CloudTranscriptionProvider.AssemblyAI, "universal-3-5-pro"),
                    ("universal-3-pro-medical", CloudTranscriptionProvider.AssemblyAI, "universal-3-5-pro-medical"),
                    ("stt-async-v4", CloudTranscriptionProvider.Soniox, "stt-async-v5"),
                    ("gemini-3.1-flash-lite-preview", CloudTranscriptionProvider.Gemini, "gemini-3.1-flash-lite"),
                    ("gemini-2.0-flash", CloudTranscriptionProvider.Gemini, "gemini-3.6-flash"),
                };

                foreach (var (oldId, provider, replacement) in cases)
                {
                    Assert(CloudTranscriptionModels.ResolveModelAlias(oldId, provider) == replacement,
                        $"{oldId} should resolve to {replacement}");
                    Assert(CloudTranscriptionModels.GetById(replacement, provider) != null,
                        $"replacement {replacement} is missing from the picker");
                    Assert(CloudTranscriptionModels.GetModelsForProvider(provider).All(m => m.Id != oldId),
                        $"retired model {oldId} is still selectable");
                }
            });

            // NATIVE CAPTURE (issue #277, phase 1a). Drives every
            // shared-conformance/backup-vectors.json modeNormalization row through the
            // SHIPPING Windows mode-import path — UniversalBackupMapper.MapToMode, which
            // composes CloudSttCatalog.NormalizeCloudProvider, the core's
            // MigrateCloudAccuracyTier / MigrateCloudPpModel, CloudTranscriptionModels
            // .ResolveModelAlias and the cloudTranscriptionDomain gate — and pins the
            // answer this build produces. It changes no behavior; it records it, so the
            // Rust port can be diffed on the same inputs before the native copies go.
            //
            // A row carries "expected" when Windows and Linux already agree, and
            // "expectedWindows"/"expectedLinux" when they do not. The same file is read
            // by app/shared-dotnet/HyperWhisper.Backup.Application.Tests.
            Run("backup mode-normalization vectors — native capture", () =>
            {
                var vectorsPath = Path.Combine(AppContext.BaseDirectory, "backup-vectors.json");
                Assert(File.Exists(vectorsPath), $"backup-vectors.json not found at {vectorsPath}");

                using var vectors = JsonDocument.Parse(File.ReadAllText(vectorsPath));
                var rows = vectors.RootElement.GetProperty("modeNormalization");
                Assert(rows.GetArrayLength() > 0, "backup-vectors.json has no modeNormalization rows");

                foreach (var row in rows.EnumerateArray())
                {
                    var label = row.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("a vector row has no name");
                    var expected = row.TryGetProperty("expected", out var shared)
                        ? shared
                        : row.GetProperty("expectedWindows");

                    var universal = JsonSerializer.Deserialize<UniversalMode>(
                        row.GetProperty("mode").GetRawText())
                        ?? throw new InvalidOperationException($"vector '{label}' did not deserialize");

                    var mode = UniversalBackupMapper.MapToMode(universal);

                    AssertModeVectorField(label, "cloudProvider", expected, mode.CloudProvider);
                    AssertModeVectorField(label, "cloudTranscriptionModel", expected, mode.CloudTranscriptionModel);
                    AssertModeVectorField(label, "cloudTranscriptionDomain", expected, mode.CloudTranscriptionDomain);
                    AssertModeVectorField(label, "cloudAccuracyTier", expected, mode.CloudAccuracyTier);
                    AssertModeVectorField(label, "cloudPostProcessingModel", expected, mode.CloudPostProcessingModel);
                }

                var nameRows = vectors.RootElement.GetProperty("modeNameNormalization");
                Assert(nameRows.GetArrayLength() > 0,
                    "backup-vectors.json has no modeNameNormalization rows");
                foreach (var row in nameRows.EnumerateArray())
                {
                    var label = row.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("a mode-name vector row has no name");
                    var universal = JsonSerializer.Deserialize<UniversalMode>(
                        row.GetProperty("mode").GetRawText())
                        ?? throw new InvalidOperationException($"vector '{label}' did not deserialize");
                    var mode = UniversalBackupMapper.MapToMode(universal);
                    Assert(mode.Name == row.GetProperty("expectedName").GetString(),
                        $"vector '{label}': normalized mode name was '{mode.Name}'");
                }
            });

            // NATIVE CAPTURE (issue #277, phase 2a). Drives every
            // shared-conformance/backup-vectors.json windowsSettings row through the
            // SHIPPING Windows settings adapters — UniversalBackupMapper.MapSettings and
            // BuildPlatformExtensions on the way out, ApplySettings on the way in — over
            // the real SettingsService, which Program.Main has already re-rooted at a
            // temp AppData directory. It changes no behavior; it records it, so the Rust
            // pairs table written in 2b can be diffed on the same inputs while the native
            // mapper still exists. This is the ONLY observation of the Windows answer:
            // net10.0-windows/WPF cannot run in the authoring sandbox, so windows-ci is
            // the instrument.
            //
            // What the rows pin:
            //   * the (native, universal) RENAMES. Windows' settings.json is PascalCase
            //     (SettingsService.Save uses a plain JsonSerializerOptions
            //     { WriteIndented = true } — no camelCase policy), and the native names
            //     diverge from the universal ones where macOS's do not:
            //     textOutput.pasteResultText <- AutoPasteEnabled, plus all seven streaming
            //     keys, whose native properties are StreamingEnabled / StreamingProvider /
            //     StreamingLanguage / StreamingDeepgramModel / StreamingCloudTier /
            //     StreamingFastFormatting / StreamingShortcut.
            //   * StreamingCloudTier reads through a CLAMPING getter: unset answers
            //     deepgramNova3, so the export never carries a null tier, and an id
            //     outside the live-eligible set is rejected back to that default.
            //   * StreamingShortcut is NOT a scalar: export calls ToPersistedString() and
            //     import calls KeyboardShortcut.FromPersistedString(), so the persisted
            //     string form is what crosses the wire — and the setter re-canonicalises it.
            //   * platformExtensions.windows.settings is a CURATED list
            //     (WindowsSettingsExtensions), not everything-else. The forbidden-key
            //     assertion is the guard against a 2b port that copies the macOS adapter's
            //     "park every unpromoted key" rule into a PUBLIC backup file.
            //   * the three universal keys this build does NOT carry at all
            //     (storage.keepAudioFiles, advanced.maxRecordingDuration,
            //     textOutput.storeWordTimestamps) are asserted ABSENT, so phase 3 has a
            //     failing-to-passing record of the gap it closes.
            Run("backup windows-settings vectors — native capture", () =>
            {
                var vectorsPath = Path.Combine(AppContext.BaseDirectory, "backup-vectors.json");
                Assert(File.Exists(vectorsPath), $"backup-vectors.json not found at {vectorsPath}");

                var document = JsonNode.Parse(File.ReadAllText(vectorsPath))!.AsObject();
                var rows = document["windowsSettings"]!.AsArray();
                Assert(rows.Count > 0, "backup-vectors.json has no windowsSettings rows");

                var settings = SettingsService.Instance;

                foreach (var rowNode in rows)
                {
                    var row = rowNode!.AsObject();
                    var label = row["name"]!.GetValue<string>();
                    var direction = row["direction"]!.GetValue<string>();

                    if (direction == "export")
                    {
                        SeedWindowsSettings(settings, row["native"]!.AsObject(), label);

                        var exported = JsonSerializer.SerializeToNode(
                            UniversalBackupMapper.MapSettings(settings), UniversalCaptureOptions);
                        AssertVectorJson(label, "settings", row["expectedUniversal"], exported);

                        foreach (var absent in row["absentUniversalKeys"]!.AsArray())
                        {
                            var path = absent!.GetValue<string>().Split('.');
                            Assert(exported![path[0]]?[path[1]] is null,
                                $"vector '{label}': this build is not supposed to export '{absent}' — "
                                + "if that changed, the vectors are stale, not the code");
                        }

                        var extensions = UniversalBackupMapper.BuildPlatformExtensions(settings);
                        Assert(extensions.Count == 1 && extensions.ContainsKey("windows"),
                            $"vector '{label}': BuildPlatformExtensions emitted "
                            + $"{extensions.Count} top-level slices; today it emits only \"windows\"");

                        var windowsSlice = JsonNode.Parse(extensions["windows"].GetRawText())!.AsObject();
                        var extensionSettings = windowsSlice["settings"];
                        AssertVectorJson(label, "platformExtensions.windows.settings",
                            row["expectedPlatformExtensions"], extensionSettings);

                        foreach (var forbidden in row["forbiddenPlatformExtensionKeys"]!.AsArray())
                            Assert(extensionSettings![forbidden!.GetValue<string>()] is null,
                                $"vector '{label}': platformExtensions.windows.settings leaked "
                                + $"'{forbidden}'. That slice is a CURATED list; parking every "
                                + "unpromoted setting there would publish user paths and device names.");

                        continue;
                    }

                    Assert(direction == "import", $"vector '{label}': unknown direction '{direction}'");
                    SeedWindowsSettings(settings, row["baselineNative"]!.AsObject(), label);

                    var universal = JsonSerializer.Deserialize<UniversalSettings>(
                        row["universal"]!.ToJsonString(), UniversalCaptureOptions)
                        ?? throw new InvalidOperationException($"vector '{label}' did not deserialize");
                    UniversalBackupMapper.ApplySettings(universal, settings);

                    AssertVectorJson(label, "native settings",
                        row["expectedNative"], ReadWindowsSettings(settings));

                    if (row["expectedUniversalAfterImport"] is { } reExported)
                        AssertVectorJson(label, "re-export after import", reExported,
                            JsonSerializer.SerializeToNode(
                                UniversalBackupMapper.MapSettings(settings), UniversalCaptureOptions));
                }
            });

            // PHASE 3b / 3c — the unknownKeyRoundTrip vectors, driven through the real
            // Windows import + export glue.
            //
            // These are the assertions no core-only harness can make: the storage is
            // native (SettingsData.BackupUnknownSettings, .BackupForeignPlatformExtensions)
            // and the re-emit runs through UniversalBackupMapper, which a
            // HyperWhisper.SharedCore-only project cannot even reference.
            //
            // What each kind proves:
            //   * settingsUnknownKey — textOutput.storeWordTimestamps has no Windows
            //     property and is not gaining one. Before 3b it died at deserialize, so
            //     a mac -> Windows -> mac trip lost it. It must now come back at its
            //     ORIGINAL PATH: under textOutput, never at the settings root.
            //   * topLevelPlatformExtensions — until #288 BuildPlatformExtensions
            //     returned {"windows": ...} and nothing else, so a macOS or Linux
            //     top-level slice died even though the per-mode slices survived. Our
            //     own "windows" slice must still WIN over a stale preserved copy.
            Run("backup unknown-key and foreign-slice round trip vectors", () =>
            {
                var vectorsPath = Path.Combine(AppContext.BaseDirectory, "backup-vectors.json");
                Assert(File.Exists(vectorsPath), $"backup-vectors.json not found at {vectorsPath}");

                var document = JsonNode.Parse(File.ReadAllText(vectorsPath))!.AsObject();
                var rows = document["unknownKeyRoundTrip"]!.AsArray();
                Assert(rows.Count > 0, "backup-vectors.json has no unknownKeyRoundTrip rows");

                var settings = SettingsService.Instance;
                var ran = 0;

                foreach (var rowNode in rows)
                {
                    var row = rowNode!.AsObject();
                    var label = row["name"]!.GetValue<string>();
                    if (!row["heads"]!.AsArray().Any(h => h!.GetValue<string>() == "windows"))
                        continue;
                    ran++;

                    switch (row["kind"]!.GetValue<string>())
                    {
                        case "settingsUnknownKey":
                            RunSettingsUnknownKeyRow(settings, row, label);
                            break;
                        case "topLevelPlatformExtensions":
                            RunForeignSliceRow(settings, row, label);
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"vector '{label}': unknown kind '{row["kind"]}'");
                    }
                }

                Assert(ran > 0, "no unknownKeyRoundTrip row names the windows head");
            });

            // PHASE 3a — the mac -> Windows -> mac trip for the two settings Windows
            // gained. Both must survive AND the safety ceiling must hold: a backup can
            // tighten the recording cap and can never loosen it.
            Run("backup mac->Windows->mac keeps keepAudioFiles and clamps maxRecordingDuration", () =>
            {
                var settings = SettingsService.Instance;
                SeedWindowsSettings(settings, new JsonObject
                {
                    ["KeepAudioFiles"] = true,
                    ["MaxRecordingDuration"] = 1200,
                }, "mac-trip");

                // A macOS/Linux-shaped settings block: keepAudioFiles off, and the
                // one-hour cap both of those platforms default to.
                UniversalBackupMapper.ApplySettings(new UniversalSettings
                {
                    Storage = new UniversalStorageSettings { KeepAudioFiles = false },
                    Advanced = new UniversalAdvancedSettings { MaxRecordingDuration = 3600 },
                }, settings);

                Assert(!settings.KeepAudioFiles,
                    "keepAudioFiles must survive the import; discarding it is the #288 bug");
                Assert(settings.MaxRecordingDurationSeconds == 1200,
                    "a 3600s cap must be clamped to the 20-minute ceiling, got "
                    + settings.MaxRecordingDurationSeconds);

                // And back out again, at the right universal paths.
                var exported = JsonSerializer.SerializeToNode(
                    UniversalBackupMapper.MapSettings(settings), UniversalCaptureOptions)!;
                Assert(exported["storage"]!["keepAudioFiles"]!.GetValue<bool>() == false,
                    "the restored keepAudioFiles must come back out under storage");
                Assert(exported["advanced"]!["maxRecordingDuration"]!.GetValue<int>() == 1200,
                    "the clamped cap must come back out under advanced");

                // A shorter cap is a user choice and must be kept verbatim.
                UniversalBackupMapper.ApplySettings(new UniversalSettings
                {
                    Advanced = new UniversalAdvancedSettings { MaxRecordingDuration = 600 },
                }, settings);
                Assert(settings.MaxRecordingDurationSeconds == 600,
                    "a backup must be able to TIGHTEN the recording cap");

                // macOS's two sentinels must not rewrite it.
                foreach (var sentinel in new[] { 300, 0 })
                {
                    UniversalBackupMapper.ApplySettings(new UniversalSettings
                    {
                        Advanced = new UniversalAdvancedSettings { MaxRecordingDuration = sentinel },
                    }, settings);
                    Assert(settings.MaxRecordingDurationSeconds == 600,
                        $"the sentinel {sentinel} must read as unset, leaving the live 600s cap; got "
                        + settings.MaxRecordingDurationSeconds);
                }
            });

            // PHASE 2b — the new SettingsService.BuildBackupSettingsSnapshot() accessor
            // is the ONLY thing that decides which native settings reach the shared
            // core, on export AND as the import baseline. SettingsData is private and
            // holds RecordingsFolder (a real user filesystem path),
            // LastSelectedMicrophone (a device name), GettingStartedCompletedSteps and
            // LocalApiServerPersistedPort. A .hwbackup.json is a file users share, and
            // this repo is public. Pin the key set exactly: growing it is a decision,
            // never an accident.
            Run("backup settings snapshot carries exactly the promoted keys", () =>
            {
                var settings = SettingsService.Instance;
                var snapshot = JsonNode.Parse(settings.BuildBackupSettingsSnapshot())!.AsObject();

                string[] expected =
                [
                    "LaunchMinimized", "ShowRecordingWindow", "CheckForUpdatesAutomatically",
                    "EnableErrorLogging", "ShareAnonymousSpeedData", "EnableSoundEffects",
                    "AutoPasteEnabled", "RemoveFillerWords", "RestoreClipboardAfterPaste",
                    "HideFromClipboardHistory", "ClipboardRestoreDelaySeconds", "AutocapitalizeInsert",
                    // KeepAudioFiles and MaxRecordingDuration joined in phase 3a. Both
                    // are plain cross-platform settings values — neither is a path nor
                    // a device name — so the privacy rule below is unaffected.
                    "StoreAsM4A", "KeepAudioFiles",
                    "StreamingEnabled", "StreamingProvider", "StreamingLanguage",
                    // StreamingCloudTier joined with the catalog-v8 live tier picker.
                    // It reads through a clamping getter, so it is always present and
                    // never null — a tier id, not a path or a device name.
                    "StreamingDeepgramModel", "StreamingCloudTier",
                    "StreamingFastFormatting", "StreamingShortcut",
                    "TypingSpeedWPM", "MaxRecordingDuration",
                ];

                var actual = snapshot.Select(entry => entry.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
                var want = expected.OrderBy(key => key, StringComparer.Ordinal).ToArray();
                Assert(actual.SequenceEqual(want, StringComparer.Ordinal),
                    "the backup settings snapshot key set changed. Expected "
                    + $"[{string.Join(", ", want)}], got [{string.Join(", ", actual)}]");

                foreach (var forbidden in new[]
                {
                    "RecordingsFolder", "LastSelectedMicrophone", "LastSelectedModel",
                    "GettingStartedCompletedSteps", "LocalApiServerPersistedPort",
                    "LocalApiServerEnabled", "SelectedModeId", "RecordingOverlayXRatio",
                    "RecordingOverlayYRatio", "PushToTalk",
                    // First-run bookkeeping for THIS install. Restoring a backup
                    // taken before setup finished must not re-open the onboarding
                    // window on a machine that is already configured.
                    "OnboardingPending",
                    // Phase 3 bookkeeping. Raw preserved JSON, not settings: each has
                    // its own merge point, and running them through the pairs tables
                    // would re-emit them at the wrong path.
                    "BackupUnknownSettings", "BackupForeignPlatformExtensions",
                    "BackupUnknownRootKeys",
                })
                    Assert(snapshot[forbidden] is null,
                        $"the backup settings snapshot leaked '{forbidden}'. That value is "
                        + "device-local or a user path and must never reach a shared backup file.");

                // StreamingShortcut is a KeyboardShortcut, not a scalar: it must cross
                // as the persisted string, never as an object.
                Assert(snapshot["StreamingShortcut"] is JsonValue
                    && snapshot["StreamingShortcut"]!.GetValue<string>() == settings.StreamingShortcut.ToPersistedString(),
                    "StreamingShortcut must be serialised via ToPersistedString()");
            });

            // PHASE 2b — the deep-merge on the Windows import half.
            //
            // ApplySettings now converts in the shared core and deep-merges the core's
            // native-shaped answer over a baseline snapshot before running the setter
            // chain. The merge is INERT today: the core is present-only, so a key it
            // omits is filled from the baseline and its setter's dirty-check does
            // nothing. Asserting the no-op is the point — the day the core returns a
            // COMPLETE native blob, this is what stops an absent backup key arriving as
            // a core default and clobbering a live setting.
            Run("backup settings import deep-merges over the live baseline", () =>
            {
                var settings = SettingsService.Instance;
                var seed = new JsonObject
                {
                    ["LaunchMinimized"] = false,
                    ["TypingSpeedWPM"] = 95,
                    ["StreamingProvider"] = "deepgram",
                    ["StreamingLanguage"] = "de",
                    ["StreamingDeepgramModel"] = "nova-3-medical",
                    ["ClipboardRestoreDelaySeconds"] = 2.5,
                    ["StoreAsM4A"] = true,
                };
                SeedWindowsSettings(settings, seed, "deep-merge");
                var before = ReadWindowsSettings(settings);

                // (1) An empty universal block must change NOTHING at all.
                UniversalBackupMapper.ApplySettings(new UniversalSettings(), settings);
                AssertVectorJson("deep-merge", "native settings after an empty import",
                    before, ReadWindowsSettings(settings));

                // (2) A block carrying ONE key must change exactly that key. Every
                // other native value has to come from the baseline, not from a default.
                UniversalBackupMapper.ApplySettings(
                    new UniversalSettings { General = new UniversalGeneralSettings { LaunchMinimized = true } },
                    settings);

                var expected = before.DeepClone().AsObject();
                expected["LaunchMinimized"] = true;
                AssertVectorJson("deep-merge", "native settings after a one-key import",
                    expected, ReadWindowsSettings(settings));
            });

            Run("Groq post-processing sends an explicit output-token cap", () =>
            {
                using var body = ReadLlmBody(PortableLlmProvider.Groq, "openai/gpt-oss-20b");

                Assert(body.RootElement.GetProperty("max_completion_tokens").GetUInt32()
                        == LlmPostProcessing.GroqMaxCompletionTokens,
                    "Groq request should cap completions at GroqMaxCompletionTokens");
                Assert(!body.RootElement.TryGetProperty("max_tokens", out _),
                    "Groq request should use max_completion_tokens, not max_tokens");
            });

            Run("Custom endpoint pointed at Groq's API is recognized", () =>
            {
                // The shared builder sniffs the host, so a custom endpoint aimed at
                // Groq gets the same cap a first-class Groq mode gets.
                using var groq = ReadLlmBody(
                    PortableLlmProvider.Custom, "openai/gpt-oss-20b",
                    "https://api.groq.com/openai/v1/chat/completions");
                Assert(groq.RootElement.GetProperty("max_completion_tokens").GetUInt32()
                        == LlmPostProcessing.GroqMaxCompletionTokens,
                    "api.groq.com should be recognized as a Groq endpoint");

                using var upper = ReadLlmBody(
                    PortableLlmProvider.Custom, "openai/gpt-oss-20b",
                    "https://API.GROQ.COM/openai/v1/chat/completions");
                Assert(upper.RootElement.TryGetProperty("max_completion_tokens", out _),
                    "host match should be case-insensitive");

                using var local = ReadLlmBody(
                    PortableLlmProvider.Custom, "llama3.2",
                    "http://localhost:1234/v1/chat/completions");
                Assert(!local.RootElement.TryGetProperty("max_completion_tokens", out _),
                    "a local/self-hosted endpoint should not be recognized as Groq");

                Assert(LlmPostProcessing.NormalizeCustomEndpoint("not a url", "m").Status
                        != PortableEndpointStatus.Valid,
                    "an unparsable URL should not be accepted at all");
            });

            // Only possible since CustomEndpointManager took its HttpClient as a
            // constructor parameter: the singleton built its own, so the "Test"
            // button's response handling needed a live provider and a live key.
            RunAsync("Custom endpoint test maps every upstream reply", async () =>
            {
                const string Url = "http://localhost:1234/v1/chat/completions";
                const string Model = "llama3.2";

                var handler = new CapturingHandler();
                using var client = new HttpClient(handler);
                using var manager = new CustomEndpointManager(client);

                // An upstream error body carries the reason the key was refused.
                // Reporting "HTTP 401" instead loses it.
                handler.Next = () => Respond(HttpStatusCode.Unauthorized,
                    """{"error":{"message":"Incorrect API key provided"}}""");
                var refused = await manager.TestEndpointAsync(Url, Model, "sk-wrong");
                Assert(!refused.success, "a 401 should not report success");
                Assert(refused.message == "Incorrect API key provided",
                    $"401 body should be parsed, got '{refused.message}'");

                // An error body with no "error" object has nothing to quote, so
                // the status line is the fallback — not an empty message.
                handler.Next = () => Respond(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}""");
                var unparsable = await manager.TestEndpointAsync(Url, Model, "sk-test");
                Assert(!unparsable.success, "a 503 should not report success");
                Assert(unparsable.message == "HTTP 503", $"expected the status fallback, got '{unparsable.message}'");

                // The happy path returns the completion text itself.
                handler.Next = () => Respond(HttpStatusCode.OK,
                    """{"choices":[{"message":{"content":"Hello World"}}]}""");
                var ok = await manager.TestEndpointAsync(Url, Model, "sk-test");
                Assert(ok.success, $"a valid completion should succeed, got '{ok.message}'");
                Assert(ok.message == "Hello World", $"expected the completion text, got '{ok.message}'");

                // A 200 that is not an OpenAI completion is the shape a wrong
                // base URL produces — a proxy's HTML, say. It must not throw.
                handler.Next = () => Respond(HttpStatusCode.OK, "<html>not json</html>");
                var garbage = await manager.TestEndpointAsync(Url, Model, "sk-test");
                Assert(!garbage.success, "a non-JSON 200 should not report success");
                Assert(garbage.message == "Invalid response format - expected OpenAI-compatible response",
                    $"expected the shape error, got '{garbage.message}'");

                // Every reply above came from exactly one probe, and the probe
                // went to the endpoint the user configured.
                Assert(handler.Sends == 4, $"expected 4 upstream calls, saw {handler.Sends}");
                Assert(handler.LastRequestUri?.Host == "localhost"
                        && handler.LastRequestUri?.Port == 1234
                        && handler.LastRequestUri?.AbsolutePath == "/v1/chat/completions",
                    $"probe went to '{handler.LastRequestUri}'");
            });

            Run("Deepgram parses every message shape of its \"channel\" field", () =>
            {
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.Deepgram);

                // "channel":[0,1] — the array form used by the endpointing frames.
                Assert(strategy.ParseMessage("""{"type":"SpeechStarted","channel":[0,1],"timestamp":1.2}""")
                        is StreamingProviderEvent.Metadata,
                    "SpeechStarted should parse to a Metadata event");
                Assert(strategy.ParseMessage("""{"type":"UtteranceEnd","channel":[0,1],"last_word_end":2.5}""")
                        is StreamingProviderEvent.Metadata,
                    "UtteranceEnd should parse to a Metadata event");

                // "channel":{...} — the transcript form, which must keep working.
                Assert(strategy.ParseMessage(
                        """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"hello"}]}}""")
                        is StreamingProviderEvent.FinalTranscript { Text: "hello" },
                    "Results should still yield its transcript");
            });

            // These checks call straight into the generated FFI surface
            // (HyperwhisperCoreMethods.EvaluateLlmResponseJson / EvaluateCompletion /
            // NormalizeTermination), which doubles as the uniffi API-checksum drift
            // gate for the completion-policy functions on the real Windows DLL.

            Run("OpenAI wire: complete + wrapped content is accepted", () =>
            {
                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>>clean transcript<<END>>"},"finish_reason":"stop"}]}""",
                    raw);

                Assert(evaluation.accepted, $"rejected as {evaluation.failure}");
                Assert(evaluation.text == "clean transcript", $"got '{evaluation.text}'");
            });

            Run("OpenAI wire: finish_reason=length is rejected and returns original", () =>
            {
                const string raw = "complete raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>>partial output"},"finish_reason":"length"}]}""",
                    raw);

                Assert(!evaluation.accepted, "length response should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
                Assert(evaluation.failure == CompletionFailure.OutputLimit, $"failure {evaluation.failure}");
            });

            Run("OpenAI wire: missing finish_reason proceeds (Unspecified)", () =>
            {
                Assert(
                    HyperwhisperCoreMethods.NormalizeTermination(WireProtocol.OpenAiChat, null) == CompletionState.Unspecified,
                    "missing finish_reason should normalize to Unspecified");

                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>>clean<<END>>"}}]}""",
                    raw);

                Assert(evaluation.accepted, $"missing finish_reason should still proceed to lenient evaluation, got {evaluation.failure}");
                Assert(evaluation.text == "clean", $"got '{evaluation.text}'");
            });

            Run("OpenAI wire: markerless content is accepted (lenient variant matching)", () =>
            {
                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"clean transcript, no markers"},"finish_reason":"stop"}]}""",
                    raw);

                Assert(evaluation.accepted, $"markerless complete content should be accepted leniently, got {evaluation.failure}");
                Assert(evaluation.text == "clean transcript, no markers", $"got '{evaluation.text}'");
            });

            Run("OpenAI wire: malformed JSON is rejected and returns original", () =>
            {
                const string raw = "complete raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    "not json",
                    raw);

                Assert(!evaluation.accepted, "malformed body should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
            });

            Run("wrapped prompt leakage is rejected", () =>
            {
                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>><APPLICATION_CONTEXT>\nApp: Mail\n</APPLICATION_CONTEXT><<END>>"},"finish_reason":"stop"}]}""",
                    raw);

                Assert(!evaluation.accepted, "leaked application-context content should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
                Assert(evaluation.failure == CompletionFailure.PromptLeakage, $"failure {evaluation.failure}");
            });

            Run("OpenAI-compatible providers retain their endpoints", () =>
            {
                // The endpoint table now lives in the Rust core (#282). These
                // assertions pin the URLs the Windows app used before the move.
                var expected = new (PortableLlmProvider Provider, string Url)[]
                {
                    (PortableLlmProvider.OpenAi, "https://api.openai.com/v1/chat/completions"),
                    (PortableLlmProvider.Groq, "https://api.groq.com/openai/v1/chat/completions"),
                    (PortableLlmProvider.Grok, "https://api.x.ai/v1/chat/completions"),
                    (PortableLlmProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"),
                    (PortableLlmProvider.Cerebras, "https://api.cerebras.ai/v1/chat/completions"),
                    (PortableLlmProvider.Mistral, "https://api.mistral.ai/v1/chat/completions"),
                    (PortableLlmProvider.Anthropic, "https://api.anthropic.com/v1/messages"),
                };

                foreach (var (provider, url) in expected)
                {
                    using var request = BuildLlmRequest(provider, "model");
                    Assert(request.RequestUri?.ToString() == url, $"{provider} endpoint");
                }
            });

            Run("Anthropic keeps its required 8192 output limit", () =>
            {
                using var body = ReadLlmBody(PortableLlmProvider.Anthropic, "model");
                Assert(body.RootElement.GetProperty("max_tokens").GetUInt32() == 8192,
                    "Anthropic max_tokens should be 8192");
                Assert(body.RootElement.GetProperty("max_tokens").GetUInt32()
                        == LlmPostProcessing.MaxOutputTokens,
                    "Anthropic max_tokens should be the shared output cap");
            });

            Run("Anthropic wire: max_tokens stop is rejected", () =>
            {
                const string raw = "complete raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.AnthropicMessages,
                    """{"content":[{"type":"text","text":"<<CLEANED>>partial<<END>>"}],"stop_reason":"max_tokens"}""",
                    raw);

                Assert(!evaluation.accepted, "max_tokens response should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
                Assert(evaluation.failure == CompletionFailure.OutputLimit, $"failure {evaluation.failure}");

                Assert(HyperwhisperCoreMethods.NormalizeTermination(WireProtocol.AnthropicMessages, "end_turn") == CompletionState.Complete,
                    "end_turn should be complete");
                Assert(HyperwhisperCoreMethods.NormalizeTermination(WireProtocol.AnthropicMessages, "max_tokens") == CompletionState.OutputLimit,
                    "max_tokens should be output-limited");
            });

            Run("Local/in-process completions (Unspecified state) require lenient acceptance", () =>
            {
                const string raw = "complete raw transcript";
                var rejected = HyperwhisperCoreMethods.EvaluateCompletion(raw, "", CompletionState.Unspecified);
                var accepted = HyperwhisperCoreMethods.EvaluateCompletion(raw, "<<CLEANED>>clean<<END>>", CompletionState.Unspecified);
                var acceptedMarkerless = HyperwhisperCoreMethods.EvaluateCompletion(raw, "clean, no markers", CompletionState.Unspecified);

                Assert(!rejected.accepted && rejected.text == raw, "empty unspecified response should preserve raw text");
                Assert(accepted.accepted && accepted.text == "clean", "wrapped unspecified response should be accepted");
                Assert(acceptedMarkerless.accepted && acceptedMarkerless.text == "clean, no markers",
                    "markerless unspecified response should be accepted leniently");
            });

            Run("Local LLM reserves an 8192-token output without context shifting", () =>
            {
                Assert(LocalLlmService.MaxTokens == 8_192, "local output ceiling should be 8192");
                Assert(LocalLlmService.ContextSize >= 16_384, "local context should be at least 16384");

                var promptBudget = LocalLlmService.ContextSize
                    - LocalLlmService.MaxTokens
                    - LocalLlmService.ChatTemplateTokenReserve;
                LocalLlmService.EnsureTokenBudgetFits(500, promptBudget - 500);

                Expect<InvalidOperationException>(() =>
                    LocalLlmService.EnsureTokenBudgetFits(500, promptBudget - 499));
            });

            Run("Local LLM backend plan matrix (issue #77)", () =>
            {
                static LocalLlmBackend Decide(
                    string gpuName,
                    bool discrete,
                    bool cudaRuntime = true,
                    bool vulkanRuntime = true,
                    bool vulkanLoader = true,
                    bool cudaPinned = false,
                    bool vulkanPinned = false)
                    => LocalLlmGpuHelper.DecideBackend(
                        gpuName, discrete, cudaRuntime, vulkanRuntime, vulkanLoader, cudaPinned, vulkanPinned);

                // The issue #77 machine: discrete AMD with a Vulkan-capable driver.
                Assert(Decide("AMD Radeon RX 6900 XT", discrete: true) == LocalLlmBackend.Vulkan,
                    "RX 6900 XT should plan Vulkan");
                // The CUDA runtime files ship on every x64 install, so they must
                // never route a non-NVIDIA adapter to CUDA.
                Assert(Decide("AMD Radeon RX 6900 XT", discrete: true, cudaRuntime: true) != LocalLlmBackend.Cuda,
                    "AMD must never plan CUDA");

                // Integrated adapters stay on CPU.
                Assert(Decide("AMD Radeon(TM) Graphics", discrete: false) == LocalLlmBackend.None,
                    "AMD APU should plan CPU");
                Assert(Decide("Intel(R) UHD Graphics 770", discrete: false) == LocalLlmBackend.None,
                    "Intel UHD should plan CPU");
                Assert(Decide("Intel(R) Arc(TM) A770 Graphics", discrete: true) == LocalLlmBackend.Vulkan,
                    "Intel Arc should plan Vulkan");

                // NVIDIA: CUDA first; Vulkan when CUDA is pinned or its runtime is
                // absent; CPU when both backends are pinned.
                Assert(Decide("NVIDIA GeForce RTX 4060", discrete: true) == LocalLlmBackend.Cuda,
                    "NVIDIA should plan CUDA");
                Assert(Decide("NVIDIA GeForce RTX 4060", discrete: true, cudaPinned: true) == LocalLlmBackend.Vulkan,
                    "CUDA-pinned NVIDIA should plan Vulkan");
                Assert(Decide("NVIDIA GeForce RTX 4060", discrete: true, cudaRuntime: false) == LocalLlmBackend.Vulkan,
                    "NVIDIA without cuda12 runtime should plan Vulkan");
                Assert(Decide("NVIDIA GeForce RTX 4060", discrete: true, cudaPinned: true, vulkanPinned: true) == LocalLlmBackend.None,
                    "doubly-pinned NVIDIA should plan CPU");

                // Vulkan needs the system loader and the shipped runtime files.
                Assert(Decide("AMD Radeon RX 6900 XT", discrete: true, vulkanLoader: false) == LocalLlmBackend.None,
                    "no vulkan-1.dll should plan CPU");
                Assert(Decide("AMD Radeon RX 6900 XT", discrete: true, vulkanRuntime: false) == LocalLlmBackend.None,
                    "missing vulkan runtime files should plan CPU");
                Assert(Decide("AMD Radeon RX 6900 XT", discrete: true, vulkanPinned: true) == LocalLlmBackend.None,
                    "vulkan-pinned AMD should plan CPU");
            });

            Run("GPU name classification gates local-LLM offload eligibility", () =>
            {
                static bool EligibleForMl(string name) => new GpuInfoService.GpuInfo
                {
                    Name = name,
                    PriorityScore = GpuInfoService.GetGpuPriorityScore(name)
                }.IsDiscreteForMl;

                Assert(EligibleForMl("NVIDIA GeForce RTX 4060"), "NVIDIA discrete should be ML-eligible");
                Assert(EligibleForMl("AMD Radeon RX 6900 XT"), "AMD RX should be ML-eligible");
                Assert(EligibleForMl("Intel(R) Arc(TM) A770 Graphics"), "Intel Arc discrete should be ML-eligible");
                Assert(!EligibleForMl("AMD Radeon(TM) Graphics"), "AMD APU should not be ML-eligible");
                Assert(!EligibleForMl("Intel(R) UHD Graphics 770"), "Intel UHD should not be ML-eligible");
                // Old discrete AMD parts outside the RX/PRO/VEGA families stay on
                // CPU — the accepted safe direction for unrecognised hardware.
                Assert(!EligibleForMl("AMD Radeon HD 7970"), "pre-RX AMD discrete should not be ML-eligible");
            });

            Run("Crash-pin entries are scoped per backend, legacy entries pin all", () =>
            {
                // Legacy entries (written before the Backend field existed) pin
                // whichever backend the plan picks — i.e. every backend.
                Assert(LocalLlmService.CrashEntryPinsBackend(null, LocalLlmGpuHelper.CudaBackendId),
                    "legacy entry should pin CUDA");
                Assert(LocalLlmService.CrashEntryPinsBackend(null, LocalLlmGpuHelper.VulkanBackendId),
                    "legacy entry should pin Vulkan");

                // A CUDA crash must not pin Vulkan (and vice versa) so the next
                // launch can still try the other backend.
                Assert(!LocalLlmService.CrashEntryPinsBackend(LocalLlmGpuHelper.CudaBackendId, LocalLlmGpuHelper.VulkanBackendId),
                    "cuda entry must not pin Vulkan");
                Assert(!LocalLlmService.CrashEntryPinsBackend(LocalLlmGpuHelper.VulkanBackendId, LocalLlmGpuHelper.CudaBackendId),
                    "vulkan entry must not pin CUDA");
                Assert(LocalLlmService.CrashEntryPinsBackend("CUDA", LocalLlmGpuHelper.CudaBackendId),
                    "backend match should be case-insensitive");
            });

            RunAsync("RustRetry caps transport failures at 4 and resolves the client per attempt", async () =>
            {
                var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
                using var client = new HttpClient(handler);
                var providerCalls = 0;

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    () => { providerCalls++; return client; },
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.NetworkError, $"code {ex.Code}");
                Assert(handler.Sends == RustRetry.MaxTransportAttempts, $"sends {handler.Sends}");
                Assert(providerCalls == RustRetry.MaxTransportAttempts, $"provider calls {providerCalls}");
            });

            RunAsync("RustRetry caps per-attempt timeouts at 2", async () =>
            {
                var handler = new StubHandler(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("unreachable");
                });
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    CancellationToken.None,
                    perAttemptTimeout: TimeSpan.FromMilliseconds(100)));

                Assert(ex.Code == TranscriptionErrorCode.NetworkError, $"code {ex.Code}");
                Assert(handler.Sends == RustRetry.MaxTimeoutAttempts, $"sends {handler.Sends}");
            });

            // Issue #379: 8 attempts of raw exponential backoff is ~127s of sleep,
            // so an always-503 provider used to hang for ~150s. The budget is a
            // THIRD bound, orthogonal to the two transport caps above (which the
            // two cases above still pin at 4 and 2).
            RunAsync("RustRetry's backoff budget stops a hard-down provider early", async () =>
            {
                // The stub 503s twice then succeeds — the shape of a real
                // transient blip, and the shape that separates the two budgets
                // without waiting out the full 127s series.
                static StubHandler FlakyStub()
                {
                    var sends = 0;
                    return new StubHandler(_ =>
                    {
                        sends++;
                        return Task.FromResult(sends <= 2
                            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            {
                                Content = new StringContent("{\"error\":\"provider down\"}")
                            }
                            : new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent("{\"text\":\"ok\"}")
                            });
                    });
                }

                // budgetMs: 0 is unbounded — the pre-#379 behaviour. Both retries
                // are taken (1s + 2s of sleep) and the third attempt succeeds.
                var unbounded = FlakyStub();
                using var unboundedClient = new HttpClient(unbounded);
                var ok = await RustRetry.PerformAsync(
                    unboundedClient,
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    CancellationToken.None,
                    budgetMs: 0);

                Assert(ok.@status == 200, $"status {ok.@status}");
                Assert(unbounded.Sends == 3, $"unbounded sends {unbounded.Sends}");

                // A 2s budget: attempt 1's 1s sleep fits, and attempt 2's 2s sleep
                // would take the RUNNING TOTAL to 3s, past the budget — so the
                // sequence gives up at 2 sends and the SAME stub never reaches its
                // success. Note the stub answers instantly: the budget counts the
                // backoff only, never the requests' own duration.
                var budgeted = FlakyStub();
                using var budgetedClient = new HttpClient(budgeted);
                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    budgetedClient,
                    BuildDummyRequest,
                    resp => new TranscriptionException(
                        TranscriptionErrorCode.ProviderUnavailable, "budget", null, (int)resp.@status),
                    CancellationToken.None,
                    budgetMs: 2_000));

                Assert(ex.Code == TranscriptionErrorCode.ProviderUnavailable, $"code {ex.Code}");
                Assert(budgeted.Sends == 2, $"budgeted sends {budgeted.Sends}");
            });

            RunAsync("RustRetry's budget never overrides the core attempt ceiling", async () =>
            {
                // `Retry-After: 0` makes every backoff zero, so `slept + delay`
                // can never exceed any budget. The core's MAX_ATTEMPTS is still
                // the ceiling — the budget only ever turns a Retry into a GiveUp,
                // it never extends one. Costs no wall-clock time.
                var handler = new StubHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{\"error\":\"provider down\"}")
                    };
                    response.Headers.TryAddWithoutValidation("Retry-After", "0");
                    return Task.FromResult(response);
                });
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    resp => new TranscriptionException(
                        TranscriptionErrorCode.ProviderUnavailable, "exhausted", null, (int)resp.@status),
                    CancellationToken.None,
                    budgetMs: 5_000));

                Assert(ex.Code == TranscriptionErrorCode.ProviderUnavailable, $"code {ex.Code}");
                Assert(handler.Sends == (int)uniffi.hyperwhisper_core.HyperwhisperCoreMethods.RetryMaxAttempts(),
                    $"sends {handler.Sends}");
            });

            RunAsync("RustRetry never retries caller cancellation", async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                var handler = new StubHandler(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("unreachable");
                });
                using var client = new HttpClient(handler);

                await ExpectAsync<OperationCanceledException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    cts.Token,
                    perAttemptTimeout: TimeSpan.FromSeconds(30)));

                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            RunAsync("RustRetry gives up immediately on a non-retryable status via parseError", async () =>
            {
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"bad\"}")
                }));
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    resp => new TranscriptionException(TranscriptionErrorCode.InvalidRequest, "mapped", null, (int)resp.@status),
                    CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.InvalidRequest, $"code {ex.Code}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            // RustSingleShot now solely owns build-error mapping, the retry
            // give-up mapping, the post-retry cancellation check, parse-error
            // mapping and the completion log for the services that call it. Its
            // build and parse arguments are caller-supplied delegates, so the
            // whole sequence is exercisable here without an FFI call or a
            // network hop.
            //
            // Each case below asserts WHICH path produced its outcome, not just
            // that something happened, via one shared mechanism: the test's own
            // build/parse delegates append to a `steps` list, and the case asserts
            // the exact ordered join. "Threw" alone is not evidence — the runner
            // hands the same parseResponse delegate to RustRetry as its give-up
            // mapper, so several distinct paths produce indistinguishable
            // exceptions unless something pins the path down.

            RunAsync("RustSingleShot maps a build-fn core error with the provider tag", async () =>
            {
                var handler = new StubHandler(_ => throw new InvalidOperationException("must not send"));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "Groq",
                    buildRequest: () => { steps.Add("build"); throw new HwTranscriptionException.Unauthorized(); },
                    parseResponse: _ => { steps.Add("parse"); throw new InvalidOperationException("must not parse"); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.Unauthorized, $"code {ex.Code}");
                Assert(ex.ProviderName == "Groq", $"provider {ex.ProviderName}");
                Assert(ex.Message == "Invalid Groq API key", $"message {ex.Message}");
                // The build fn threw before the executor was reached, and nothing
                // downstream ran.
                Assert(string.Join(",", steps) == "build", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 0, $"sends {handler.Sends}");
            });

            RunAsync("RustSingleShot maps a parse-fn core error from the POST-RETRY parse", async () =>
            {
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"\"}")
                }));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "Deepgram",
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ => { steps.Add("parse"); throw new HwTranscriptionException.NoSpeech(); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.NoSpeechDetected, $"code {ex.Code}");
                Assert(ex.ProviderName == "Deepgram", $"provider {ex.ProviderName}");
                Assert(string.Join(",", steps) == "build,parse", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
                // THE discriminator. The give-up mapper runs the very same
                // parseResponse delegate and produces the same code, provider and
                // send count, so none of the above can tell the two apart. It maps
                // with the response status; the post-retry parse maps without one.
                // A null status is therefore proof this came from the post-retry
                // parse — and matches what the six services emitted on main. The
                // next case is that other path, asserting 401 where this one
                // asserts null, so the premise is executed rather than assumed.
                Assert(ex.HttpStatusCode == null, $"http status {ex.HttpStatusCode}");
            });

            RunAsync("RustSingleShot maps a non-2xx give-up with the provider tag and the status", async () =>
            {
                // The give-up mapper is the only symbol this refactor relocated,
                // and nothing else in the suite reaches it: it runs solely from
                // RustRetry's GiveUp branch, which needs a non-2xx response.
                // 401 classifies Unauthorized in the core, which is terminal, so
                // the wrapper gives up on attempt 1 — one send, no backoff sleep.
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"bad key\"}")
                }));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "ElevenLabs",
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ => { steps.Add("parse"); throw new HwTranscriptionException.Unauthorized(); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.Unauthorized, $"code {ex.Code}");
                Assert(ex.ProviderName == "ElevenLabs", $"provider {ex.ProviderName}");
                // The point of the mapper. Drop the status it passes, or hand it
                // the wrong provider, and every BYOK 401/429/413 still surfaces —
                // untagged and status-less — to the UI and to
                // TranscriptionDiagnosticsService.
                Assert(ex.HttpStatusCode == 401, $"http status {ex.HttpStatusCode}");
                // parse ran once, against the non-2xx, from inside the give-up
                // branch; the runner's own post-retry parse is never reached.
                Assert(string.Join(",", steps) == "build,parse", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            RunAsync("RustSingleShot fails a non-2xx the core parser accepts", async () =>
            {
                // The give-up mapper's other branch. A parser that returns a
                // transcript for an error response must not turn that body into a
                // successful transcription.
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"text\":\"not an error body\"}")
                }));
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "Mistral",
                    buildRequest: BuildDummyRequest,
                    parseResponse: _ => new HwTranscript("must not be returned", null, null, null),
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.Unknown, $"code {ex.Code}");
                Assert(ex.Message == "Unexpected non-error response", $"message {ex.Message}");
                Assert(ex.ProviderName == "Mistral", $"provider {ex.ProviderName}");
                Assert(ex.HttpStatusCode == 401, $"http status {ex.HttpStatusCode}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            RunAsync("RustSingleShot honours cancellation after the retry loop returns", async () =>
            {
                using var cts = new CancellationTokenSource();
                var steps = new List<string>();

                // Cancel from the response's Dispose, which the executor runs
                // strictly AFTER SendAsync returned and after the body was read.
                // Nothing else cancels this token, so neither HttpClient nor the
                // executor's content read can be the source of the throw: if the
                // post-retry ThrowIfCancellationRequested were gone, the token
                // would still be cancelled but nothing would observe it, parse
                // would run, and ExpectAsync would fail. (The previous shape
                // cancelled inside the handler and relied on BCL buffering detail
                // to decide which of two paths threw — under dotnet-quality
                // "preview" that floats between runs.)
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SignalOnDisposeContent("{\"text\":\"ignored\"}", cts.Cancel)
                }));
                using var client = new HttpClient(handler);

                await ExpectAsync<OperationCanceledException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "OpenAI",
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ =>
                    {
                        steps.Add("parse");
                        return new HwTranscript("must not be returned", null, null, null);
                    },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: cts.Token));

                Assert(handler.Sends == 1, $"sends {handler.Sends}");
                // A cancelled sequence must never parse the body or hand back a
                // transcript.
                Assert(string.Join(",", steps) == "build", $"steps {string.Join(",", steps)}");
            });

            RunAsync("RustSingleShot returns the transcript and logs the banner derived from provider", async () =>
            {
                // The PR's headline claim is byte-identical logs. main hard-coded
                // one completion banner per service; the runner derives it from
                // `provider`. This is Groq's literal, verbatim from main.
                //
                // ONE provider, not the six. The other five differ from this one
                // only by what ToUpperInvariant returns, so running them proves
                // the BCL uppercases and nothing about the services — their
                // "Groq"/"OpenAI"/… arguments are inline literals at six call
                // sites that no test reaches, before this PR or after it. Five
                // more iterations would buy that non-coverage at 15 extra writes
                // through a sink that swallows its own IO errors.
                const string provider = "Groq";
                const string banner = "========== GROQ TRANSCRIPTION COMPLETE ==========";

                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"body is parsed by the stub below\"}")
                }));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                // Read back what LoggingService actually wrote. Main() points
                // AppPaths at a disposable temp root, so this is a private log
                // directory and no production seam is added to observe it.
                // Offsets are per FILE across that directory, not one captured
                // path: CurrentLogPath is DateTime.Now-derived and re-evaluated
                // on every write, so a run crossing local midnight appends these
                // lines to a file that did not exist when this case started.
                var offsets = SnapshotLogOffsets();

                var text = await RustSingleShot.TranscribeAsync(
                    client,
                    provider,
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ => { steps.Add("parse"); return new HwTranscript("hello world", null, null, null); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None);

                Assert(text == "hello world", $"text {text}");
                Assert(string.Join(",", steps) == "build,parse", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");

                // Every assertion below carries the captured window verbatim.
                // LoggingService.WriteLog swallows its own IOException, so a red
                // here has two possible causes — the banner changed, or the write
                // was dropped — and only the captured text tells them apart.
                var emitted = ReadLogSince(offsets);
                var lines = SplitLogLines(emitted);
                var context = $"log dir '{LoggingService.LogDirectory}', {lines.Length} line(s) captured: <<<{emitted}>>>";

                // Positional, not Contains: the two detail lines must be THIS
                // banner's own next two lines. A Contains pair would be satisfied
                // by any banner's detail lines anywhere in the window.
                var bannerLine = Array.FindIndex(lines, l => l.EndsWith(banner, StringComparison.Ordinal));
                Assert(bannerLine >= 0, $"banner line not emitted — {context}");
                Assert(bannerLine + 2 < lines.Length, $"fewer than two lines follow the banner — {context}");
                Assert(lines[bannerLine + 1].EndsWith("  Characters: 11", StringComparison.Ordinal),
                    $"line after banner is '{lines[bannerLine + 1]}' — {context}");
                // Anchored digits+"ms", because "  Total time: " on its own is a
                // prefix of every unit a future edit could switch to.
                var totalTime = lines[bannerLine + 2];
                Assert(
                    System.Text.RegularExpressions.Regex.IsMatch(totalTime, @"  Total time: [0-9]+ms$"),
                    $"second line after banner is '{totalTime}' — {context}");
            });

            Run("AssemblyAI MapError classifies via the step's own parser", () =>
            {
                var unauthorized = new uniffi.hyperwhisper_core.HttpResponse(
                    401, new List<Header>(), System.Text.Encoding.UTF8.GetBytes("{\"error\":\"bad key\"}"));
                var ex = AssemblyAIService.MapError(
                    unauthorized, "upload", r => HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(r));
                Assert(ex.Code == TranscriptionErrorCode.Unauthorized, $"code {ex.Code}");
                Assert(ex.HttpStatusCode == 401, $"status {ex.HttpStatusCode}");
            });

            Run("AssemblyAI IsSyncEligible gates on exact duration vs the core's sync cap, and excludes medical models", () =>
            {
                var cap = HyperwhisperCoreMethods.AssemblyaiSyncMaxDurationSecs();
                Assert(cap > 0, $"expected a positive sync cap from the core, got {cap}");

                Assert(
                    AssemblyAIService.IsSyncEligible(Result<double>.Success(cap - 1), cap, isMedicalModel: false),
                    "a duration just under the cap should be sync-eligible");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Success(cap), cap, isMedicalModel: false),
                    "a duration AT the cap should NOT be sync-eligible (falls back to async)");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Success(cap + 1), cap, isMedicalModel: false),
                    "a duration over the cap should NOT be sync-eligible");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Failure("duration probe failed"), cap, isMedicalModel: false),
                    "an unknown (failed) duration probe should NOT be sync-eligible — fail closed to async");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Success(cap - 1), cap, isMedicalModel: true),
                    "a medical model should NOT be sync-eligible even with an otherwise-eligible duration — sync has no medical/domain concept");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech skips confirmed dead silence", () =>
            {
                // NonSilentRatio == 0 with a very low peak is the "nothing was recorded
                // at all" case - always benign, must always be skipped. Regression guard
                // for the confirmed-dead-silence early-out.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 3.0,
                    FileSizeBytes: 1024,
                    PeakDbfs: -80.0,
                    RmsDbfs: -90.0,
                    NonSilentRatio: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "confirmed dead silence should be skipped");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech skips the real HYPERWHISPER-PA no-speech sample (fix for the widened low-signal thresholds)", () =>
            {
                // The actual values from the HYPERWHISPER-PA/-QB/-VY Sentry sample: quiet
                // room tone the backend correctly called "no speech", but the old -50dBFS /
                // 0.02 thresholds were too strict to catch it, so it was captured as a full
                // Sentry issue on every occurrence. This is the fix.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -39.64,
                    NonSilentRatio: 0.046);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "the real HYPERWHISPER-PA no-speech sample should now be skipped");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic returns Skip - not EmptyRecording - for confirmed dead silence", () =>
            {
                // ShouldCaptureAsNoSpeech is false for BOTH Skip and EmptyRecording, so
                // every "should be skipped" assertion above would keep passing if a
                // suppressed input started emitting a full empty_recording Sentry event
                // instead of nothing. Only an assertion on the outcome itself proves
                // suppression, so the two genuine suppression cases get one each.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 3.0,
                    FileSizeBytes: 1024,
                    PeakDbfs: -80.0,
                    RmsDbfs: -90.0,
                    NonSilentRatio: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.Skip,
                    $"confirmed dead silence must report nothing at all, got {TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)}");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic returns Skip - not EmptyRecording - for the real HYPERWHISPER-PA no-speech sample", () =>
            {
                // Same guard for the backend-confirmed low-signal arm: the whole point of
                // the widened thresholds is that this sample produces NO Sentry event, not
                // that it merely stops being labelled no-speech.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -39.64,
                    NonSilentRatio: 0.046);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.Skip,
                    $"the backend-confirmed low-signal sample must report nothing at all, got {TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)}");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech skips exactly at the low-signal threshold boundary (inclusive <=)", () =>
            {
                // The gate's comparisons are inclusive (<=), so a reading sitting exactly on
                // both thresholds must still be treated as low-signal and skipped, not
                // captured. Boundary-exact regression guard, same shape as the
                // AssemblyAI IsSyncEligible "AT the cap" boundary test above.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -38.0,
                    NonSilentRatio: 0.06);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a reading exactly at the low-signal thresholds should be skipped (inclusive boundary)");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech still captures a loud backend-disagreement anomaly", () =>
            {
                // Anomaly-detection guard: loud audio (high RMS, high non-silent ratio) that
                // the backend nonetheless flags as no-speech is exactly the genuine
                // backend-disagreement case this diagnostic exists to catch - must not
                // regress to being skipped just because the low-signal thresholds widened.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 5.0,
                    FileSizeBytes: 131072,
                    PeakDbfs: -5.0,
                    RmsDbfs: -18.0,
                    NonSilentRatio: 0.35);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a loud backend-disagreement anomaly should still be captured");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech always captures EmptyTranscriptWithoutFlag, unaffected by the threshold change", () =>
            {
                // A real bug class (backend/local mismatch) that must keep firing
                // regardless of RMS/ratio values.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -39.64,
                    NonSilentRatio: 0.046);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true, EmptyTranscriptWithoutFlag: true);

                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "EmptyTranscriptWithoutFlag should always be captured");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech always captures a failed audio analysis, unaffected by the threshold change", () =>
            {
                // A real bug class (backend/local mismatch) that must keep firing
                // regardless of RMS/ratio values.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: false,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -80.0,
                    RmsDbfs: -90.0,
                    NonSilentRatio: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a failed audio analysis should always be captured");
            });

            Run("TranscriptionDiagnosticsService payload: no extra key is silently redacted by SentryService.beforeSend", () =>
            {
                // The bug this guards. SentryService.IsRedactedExtraKey matches on the
                // KEY, as a substring, and replaces the value with "[redacted]". Three
                // fields of this diagnostic were named "transcript_id",
                // "transcription_provider_display_name" and
                // "backend_empty_transcript_without_flag", so every event of
                // HYPERWHISPER-PA/-RM/-XR arrived with all three empty - including the
                // empty-vs-flag discriminator the whole diagnostic turns on. Nothing at
                // the call site said so, and nothing failed. The names are chosen to
                // clear the filter now; this asserts the WHOLE payload does, so the next
                // field named with one of those four words fails in CI instead.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 1.7,
                    FileSizeBytes: 54446,
                    SampleRate: 16000,
                    Channels: 1,
                    PeakDbfs: -18.47,
                    RmsDbfs: -39.1,
                    NonSilentRatio: 0.1072,
                    DecodedSampleCount: 27200,
                    MeasuredSampleCount: 27200);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "HyperWhisper Cloud",
                    BackendRequestId: "21944e5f-15d6-4e6f-8021-262d8fc7958b",
                    BackendSttProvider: "elevenlabs/scribe_v2",
                    BackendNoSpeechDetected: true,
                    HttpStatusCode: 200,
                    ResponseLatencyMs: 724,
                    EmptyTranscriptWithoutFlag: false,
                    AttemptSource: TranscriptionAttemptSource.CloudInstrumented,
                    AttemptElapsedMs: 724,
                    RawResultLength: 0);

                var (tags, extras) = TranscriptionDiagnosticsService.BuildDiagnosticPayload(
                    transcriptId: Guid.NewGuid(),
                    audioPath: @"C:\does-not-exist\sample.wav",
                    audioDiagnostics: audio,
                    presentation: TranscriptionDiagnosticsService.ResolveDiagnosticPresentation(
                        PortableNoSpeechOutcome.NoSpeech),
                    mode: new Mode { Preset = "hyper", ProviderType = "cloud", Language = "en" },
                    diagnosticStage: "live_recording",
                    diagnosticSource: "provider_no_speech",
                    inputDeviceName: "Microphone (USB Audio Device)",
                    transcriptionProviderDisplayName: "HyperWhisper Cloud",
                    providerDiagnostics: provider,
                    exception: null,
                    captureDeviceCount: 2);

                foreach (var key in extras.Keys)
                {
                    Assert(!SentryService.IsRedactedExtraKey(key),
                        $"extra '{key}' will arrive at Sentry as [redacted] - rename it");
                }

                // The three renamed fields must actually be present under their new
                // names, or the rename above would "pass" by having deleted them.
                Assert(extras.ContainsKey("diagnostic_record_id"), "diagnostic_record_id is missing");
                Assert(extras.ContainsKey("provider_display_name"), "provider_display_name is missing");
                Assert(extras.ContainsKey("backend_empty_without_flag"), "backend_empty_without_flag is missing");

                // The attempt fields the local and BYOK-cloud arms exist to fill.
                Assert(extras.ContainsKey("provider_attempt_ms"), "provider_attempt_ms is missing");
                Assert(extras.ContainsKey("raw_result_length"), "raw_result_length is missing");
                Assert(tags.ContainsKey("provider_attempt_source"), "provider_attempt_source tag is missing");
                Assert(tags["provider_attempt_source"] == TranscriptionAttemptSource.CloudInstrumented,
                    $"provider_attempt_source should carry the record's own source, got {tags["provider_attempt_source"]}");
                Assert(tags["mode_language"] == "en", $"mode_language should be the mode's code, got {tags["mode_language"]}");

                // mode_name carried whatever the user typed when they named a custom
                // mode. It is user content and it is gone; mode_preset answers the same
                // question without it.
                Assert(!extras.ContainsKey("mode_name"), "mode_name is user-typed text and must not be reported");
                Assert(extras.ContainsKey("mode_preset"), "mode_preset is missing");
            });

            Run("TranscriptionDiagnosticsService payload: a provider that reports nothing says unknown, never zero", () =>
            {
                // A local engine makes no HTTP request. Reporting status 0 and 0 ms for
                // it reads like a request that failed instantly, which is how
                // HYPERWHISPER-RM/-XR look today. Absent must be reported as absent.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 2.4,
                    FileSizeBytes: 76846,
                    PeakDbfs: -21.79,
                    RmsDbfs: -39.48,
                    NonSilentRatio: 0.1996,
                    DecodedSampleCount: 38400,
                    MeasuredSampleCount: 38400);

                var (tags, extras) = TranscriptionDiagnosticsService.BuildDiagnosticPayload(
                    transcriptId: Guid.NewGuid(),
                    audioPath: @"C:\does-not-exist\sample.wav",
                    audioDiagnostics: audio,
                    presentation: TranscriptionDiagnosticsService.ResolveDiagnosticPresentation(
                        PortableNoSpeechOutcome.NoSpeech),
                    mode: new Mode { Preset = "custom", ProviderType = "local", Language = "auto" },
                    diagnosticStage: "live_recording",
                    diagnosticSource: "provider_no_speech",
                    inputDeviceName: null,
                    transcriptionProviderDisplayName: null,
                    providerDiagnostics: null,
                    exception: null,
                    captureDeviceCount: null);

                Assert((string)extras["backend_http_status"] == "unknown",
                    $"backend_http_status should be unknown with no diagnostics, got {extras["backend_http_status"]}");
                Assert((string)extras["backend_response_latency_ms"] == "unknown",
                    $"backend_response_latency_ms should be unknown with no diagnostics, got {extras["backend_response_latency_ms"]}");
                Assert((string)extras["backend_empty_without_flag"] == "unknown",
                    $"backend_empty_without_flag should be unknown with no diagnostics, got {extras["backend_empty_without_flag"]}");
                Assert((string)extras["provider_attempt_ms"] == "unknown",
                    $"provider_attempt_ms should be unknown with no diagnostics, got {extras["provider_attempt_ms"]}");
                Assert(tags["provider_attempt_source"] == TranscriptionAttemptSource.Unknown,
                    $"provider_attempt_source should be unknown with no diagnostics, got {tags["provider_attempt_source"]}");

                foreach (var key in extras.Keys)
                {
                    Assert(!SentryService.IsRedactedExtraKey(key),
                        $"extra '{key}' will arrive at Sentry as [redacted] - rename it");
                }
            });

            Run("SentryService.IsRedactedExtraKey keeps matching the four denied words", () =>
            {
                // The predicate was inlined in beforeSend and is now a named method the
                // payload test can call. Nothing about WHAT it matches changed, and this
                // pins that.
                Assert(SentryService.IsRedactedExtraKey("transcript_id"), "transcript is no longer denied");
                Assert(SentryService.IsRedactedExtraKey("raw_text"), "text is no longer denied");
                Assert(SentryService.IsRedactedExtraKey("system_prompt"), "prompt is no longer denied");
                Assert(SentryService.IsRedactedExtraKey("audio_path"), "path is no longer denied");
                Assert(SentryService.IsRedactedExtraKey("AUDIO_PATH"), "the match should be case-insensitive");
                Assert(!SentryService.IsRedactedExtraKey("audio_file_extension"), "a plain metadata key is denied");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic reclassifies a zero-frame recording as EmptyRecording, not no-speech", () =>
            {
                // A header-only / zero-frame WAV means the recorder captured nothing at
                // all, so calling it "no speech" is wrong - it is a recorder failure.
                // 4 of the 6 events on 1.11.0 in the HYPERWHISPER-PA/-QB/-RM/-XB/-XR
                // cluster were this. It must leave the no-speech group but must still be
                // reported (under its own name/fingerprint) rather than dropped.
                //
                // DurationSeconds is 5.0 on purpose: the live-recording call site passes
                // RecordingDuration.TotalSeconds as the fallback, so a header-only file
                // from a 5-second recording reports a full 5 seconds. Keying the rule on
                // duration missed exactly this case and let it fall into the
                // dead-silence skip below (NonSilentRatio 0 + PeakDbfs -120), i.e.
                // reported nothing at all for the recorder failure we most want to see.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 5.0,
                    FileSizeBytes: 44,
                    PeakDbfs: -120.0,
                    RmsDbfs: -120.0,
                    NonSilentRatio: 0,
                    DecodedSampleCount: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.EmptyRecording,
                    "a zero-frame recording should classify as EmptyRecording");
                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "an empty recording must no longer be reported as a no-speech diagnostic");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic keeps capturing the production zero-frame cohort (audio_rms_dbfs_bucket=silent on 1.11.0)", () =>
            {
                // The events this PR exists to reclassify: analysis succeeded, the file
                // has real bytes and the container reports no duration, and the signal
                // reads as silent. Under the old duration rule they were captured (as
                // no-speech); they must keep being captured after the change, as
                // EmptyRecording - not fall into the dead-silence skip.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 0,
                    FileSizeBytes: 44,
                    PeakDbfs: -120.0,
                    RmsDbfs: -120.0,
                    NonSilentRatio: 0,
                    DecodedSampleCount: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        != PortableNoSpeechOutcome.Skip,
                    "the 1.11.0 zero-frame cohort must still be reported, never skipped");
                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.EmptyRecording,
                    "the 1.11.0 zero-frame cohort should now report as EmptyRecording");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic does not call a decodable file with no container duration an EmptyRecording", () =>
            {
                // The other direction. The file-transcription call site passes a fallback
                // duration that is 0 when nothing probed it, so a perfectly decodable file
                // whose container reports no duration used to be labelled a
                // microphone-capture failure on a path where no recorder ever ran. The
                // decoder produced frames, so this is a no-speech result, not an empty
                // recording.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 0,
                    FileSizeBytes: 65536,
                    PeakDbfs: -5.0,
                    RmsDbfs: -18.0,
                    NonSilentRatio: 0.35,
                    DecodedSampleCount: 67200);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.NoSpeech,
                    "a decodable file with no container duration must stay a no-speech diagnostic");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic keeps the failed-analysis check ahead of the empty-recording check", () =>
            {
                // Precedence guard. A failed analysis decodes nothing, so if the
                // empty-recording rule ever moved above it every analysis failure would
                // silently be relabelled a recorder failure. !AnalysisSucceeded must stay
                // the first check.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: false,
                    DurationSeconds: 0,
                    FileSizeBytes: 0,
                    AnalysisError: "synthetic analysis failure",
                    DecodedSampleCount: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.NoSpeech,
                    "a failed analysis must classify as NoSpeech even with zero duration/size");
                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a failed analysis should still be captured");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic never calls a normal clip an EmptyRecording", () =>
            {
                // The reclassification must not leak into the genuine anomaly case: real
                // audio with real signal stays a no-speech diagnostic.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -5.0,
                    RmsDbfs: -18.0,
                    NonSilentRatio: 0.35,
                    DecodedSampleCount: 67200);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == PortableNoSpeechOutcome.NoSpeech,
                    "a normal 4.2s clip should stay a no-speech diagnostic");
            });

            Run("TranscriptionDiagnosticsService.ResolveDiagnosticPresentation gives every reportable outcome its own name, message and fingerprint root", () =>
            {
                // The name, message and fingerprint root used to be three separate
                // ternaries off one bool, so a fourth outcome would compile clean and
                // report under the old identity - the exact mislabelling this diagnostic
                // exists to fix. One mapping now owns all three.
                var noSpeech = TranscriptionDiagnosticsService.ResolveDiagnosticPresentation(
                    PortableNoSpeechOutcome.NoSpeech);

                Assert(noSpeech.Name == "no_speech", $"expected 'no_speech', got '{noSpeech.Name}'");
                Assert(noSpeech.Message == "Windows transcription no-speech diagnostic",
                    "the no-speech message is the Sentry group identity for eight live issues - it must stay character-identical");
                Assert(noSpeech.FingerprintRoot == "transcription-no-speech",
                    $"expected 'transcription-no-speech', got '{noSpeech.FingerprintRoot}'");

                var emptyRecording = TranscriptionDiagnosticsService.ResolveDiagnosticPresentation(
                    PortableNoSpeechOutcome.EmptyRecording);

                Assert(emptyRecording.Name == "empty_recording", $"expected 'empty_recording', got '{emptyRecording.Name}'");
                Assert(emptyRecording.Message == "Windows transcription empty recording diagnostic",
                    $"unexpected empty-recording message '{emptyRecording.Message}'");
                Assert(emptyRecording.FingerprintRoot == "transcription-empty-recording",
                    $"expected 'transcription-empty-recording', got '{emptyRecording.FingerprintRoot}'");

                // Every reportable outcome must have its own arm. A new outcome with no
                // mapping throws here, and one that copies an existing identity trips the
                // uniqueness checks - so it fails in CI rather than in Sentry.
                var names = new HashSet<string>(StringComparer.Ordinal);
                var roots = new HashSet<string>(StringComparer.Ordinal);
                foreach (var outcome in Enum.GetValues<PortableNoSpeechOutcome>())
                {
                    if (outcome == PortableNoSpeechOutcome.Skip)
                    {
                        // Skip is filtered out before anything is reported, so it has no
                        // presentation by design.
                        continue;
                    }

                    var presentation = TranscriptionDiagnosticsService.ResolveDiagnosticPresentation(outcome);

                    Assert(names.Add(presentation.Name),
                        $"outcome {outcome} reports under an already-used diagnostic name '{presentation.Name}'");
                    Assert(roots.Add(presentation.FingerprintRoot),
                        $"outcome {outcome} reports under an already-used fingerprint root '{presentation.FingerprintRoot}'");
                    Assert(!string.IsNullOrWhiteSpace(presentation.Message),
                        $"outcome {outcome} has no Sentry message");
                }
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint ignores a stale CloudProvider on a local mode", () =>
            {
                // Mode.CloudProvider and Mode.ProviderType are independent persisted
                // fields, so a mode switched from cloud to local keeps its old vendor
                // forever. Grouping on it regardless of provider type is what split one
                // local-mode condition across HYPERWHISPER-QB/-RM/-XB/-XR.
                var mode = new Mode { ProviderType = "local", CloudProvider = "groq", LocalEngine = "whisper" };

                var fingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", mode);

                Assert(fingerprint.Length == 5, $"expected 5 fingerprint elements, got {fingerprint.Length}");
                Assert(fingerprint[3] == "local", $"expected provider type 'local', got '{fingerprint[3]}'");
                Assert(fingerprint[4] == "whisper",
                    $"a local mode should group on its local engine, got '{fingerprint[4]}'");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(mode) == "none",
                    "the cloud_provider tag must not report a stale vendor for a local mode");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint treats a null or non-canonical ProviderType as local, like the routing sites do", () =>
            {
                // ProviderType is nullable with no initializer and nothing backfills it,
                // and every dispatch site routes anything that is not "cloud" to a local
                // provider (MainViewModel.GetLocalProvider, TranscriptionRetryHandler).
                // Matching only the literal "local" would leave this cohort grouping on
                // its stale CloudProvider, i.e. still fragmented.
                var nullProviderType = new Mode { ProviderType = null, CloudProvider = "groq", LocalEngine = "whisper" };
                var emptyProviderType = new Mode { ProviderType = "", CloudProvider = "gemini", LocalEngine = "whisper" };

                var nullFingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", nullProviderType);
                var emptyFingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", emptyProviderType);

                Assert(nullFingerprint[4] == "whisper",
                    $"a null ProviderType routes local, so it should group on its local engine, got '{nullFingerprint[4]}'");
                Assert(emptyFingerprint[4] == "whisper",
                    $"an empty ProviderType routes local, so it should group on its local engine, got '{emptyFingerprint[4]}'");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(nullProviderType) == "none",
                    "a null ProviderType must not report a stale cloud vendor");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(emptyProviderType) == "none",
                    "an empty ProviderType must not report a stale cloud vendor");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint groups two local modes with different stale vendors together", () =>
            {
                // The actual production regression: same local engine, same condition,
                // different leftover CloudProvider values - one Sentry group, not four.
                var staleGroq = new Mode { ProviderType = "local", CloudProvider = "groq", LocalEngine = "parakeet" };
                var staleGemini = new Mode { ProviderType = "local", CloudProvider = "gemini", LocalEngine = "parakeet" };

                var first = string.Join("|", TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", staleGroq));
                var second = string.Join("|", TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", staleGemini));

                Assert(first == second, $"expected identical fingerprints, got '{first}' vs '{second}'");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint groups a local mode and a null/empty-ProviderType mode on the same engine identically", () =>
            {
                // The residual half of the same split. Widening IsLocalMode fixed
                // element [4] (the engine) but element [3] was still the raw
                // ProviderType, so the very cohort that widening pulled in kept its own
                // Sentry group: "local" vs null vs "" = three groups for one condition,
                // exactly the HYPERWHISPER-QB/-RM shape. Element [3] is now canonicalized
                // through the same predicate, so all three collapse.
                var canonicalLocal = new Mode { ProviderType = "local", CloudProvider = "groq", LocalEngine = "whisper" };
                var nullProviderType = new Mode { ProviderType = null, CloudProvider = "hyperwhisper", LocalEngine = "whisper" };
                var emptyProviderType = new Mode { ProviderType = "", CloudProvider = "gemini", LocalEngine = "whisper" };

                static string Fingerprint(Mode mode) => string.Join("|",
                    TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                        "transcription-no-speech", "live_recording", "provider_no_speech", mode));

                var canonical = Fingerprint(canonicalLocal);

                Assert(canonical == Fingerprint(nullProviderType),
                    $"expected identical fingerprints, got '{canonical}' vs '{Fingerprint(nullProviderType)}'");
                Assert(canonical == Fingerprint(emptyProviderType),
                    $"expected identical fingerprints, got '{canonical}' vs '{Fingerprint(emptyProviderType)}'");

                // A genuinely absent mode is a different fact from a mode whose
                // ProviderType was never written, and must keep its own value.
                var noMode = string.Join("|", TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", null));

                Assert(canonical != noMode,
                    $"a null mode must not group with a local mode, both were '{canonical}'");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint still separates cloud vendors", () =>
            {
                // The fix must not collapse every provider into one group: for a cloud
                // mode the vendor is live data and two vendors are two conditions.
                var groq = new Mode { ProviderType = "cloud", CloudProvider = "groq" };
                var openai = new Mode { ProviderType = "cloud", CloudProvider = "openai" };

                var groqFingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", groq);
                var openaiFingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", openai);

                Assert(groqFingerprint[4] == "groq", $"expected 'groq', got '{groqFingerprint[4]}'");
                Assert(openaiFingerprint[4] == "openai", $"expected 'openai', got '{openaiFingerprint[4]}'");
                Assert(string.Join("|", groqFingerprint) != string.Join("|", openaiFingerprint),
                    "two different cloud vendors must keep grouping separately");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(groq) == "groq",
                    "a cloud mode's cloud_provider tag must keep reporting its vendor");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint handles a null mode without throwing", () =>
            {
                // The diagnostic runs on failure paths where the mode can be gone.
                var fingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-empty-recording", "file_transcription", "provider_no_speech", null);

                Assert(fingerprint.Length == 5, $"expected 5 fingerprint elements, got {fingerprint.Length}");
                Assert(fingerprint[0] == "transcription-empty-recording",
                    $"expected the empty-recording root, got '{fingerprint[0]}'");
                Assert(fingerprint[3] == "unknown", $"expected 'unknown', got '{fingerprint[3]}'");
                Assert(fingerprint[4] == "none", $"expected 'none', got '{fingerprint[4]}'");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(null) == "none",
                    "a null mode's cloud_provider tag should be 'none'");
            });

            Run("TranscriptionDiagnosticsService keeps the Windows fingerprint roots platform-distinct from macOS (#291)", () =>
            {
                // The classifier is shared with macOS now; the Sentry identity is
                // deliberately NOT. macOS reports "macos-transcription-*" roots and
                // "macOS transcription ..." messages. If either side ever adopted the
                // other's, macOS events would land in the eight live Windows issues.
                foreach (var outcome in Enum.GetValues<PortableNoSpeechOutcome>())
                {
                    if (outcome == PortableNoSpeechOutcome.Skip)
                    {
                        continue;
                    }

                    var presentation = TranscriptionDiagnosticsService.ResolveDiagnosticPresentation(outcome);
                    Assert(!presentation.FingerprintRoot.StartsWith("macos-", StringComparison.Ordinal),
                        $"outcome {outcome} reports under a macOS fingerprint root '{presentation.FingerprintRoot}'");
                    Assert(presentation.Message.StartsWith("Windows ", StringComparison.Ordinal),
                        $"outcome {outcome} reports under a non-Windows message '{presentation.Message}'");
                }
            });

            Run("TranscriptionDiagnosticsService's dBFS floor still agrees with the shared core (#291)", () =>
            {
                // AudioAnalysisDiagnostics needs a compile-time constant for its
                // optional parameters, so -120.0 is spelled out there as well as in
                // hw-audio. Nothing else may drift: a floor mismatch would make the
                // "silent" bucket and the dead-silence arm disagree about the same clip.
                var defaults = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: false,
                    DurationSeconds: 0,
                    FileSizeBytes: 0);

                Assert(defaults.PeakDbfs == PortableNoSpeechDiagnostics.MinimumDbfs,
                    $"the local dBFS floor {defaults.PeakDbfs} does not match the core's {PortableNoSpeechDiagnostics.MinimumDbfs}");
                Assert(defaults.RmsDbfs == PortableNoSpeechDiagnostics.MinimumDbfs,
                    $"the local dBFS floor {defaults.RmsDbfs} does not match the core's {PortableNoSpeechDiagnostics.MinimumDbfs}");
            });

            Run("PortableNoSpeechDiagnostics dBFS maths and bucketing come from the core (#291)", () =>
            {
                Assert(PortableNoSpeechDiagnostics.ToDbfs(0) == PortableNoSpeechDiagnostics.MinimumDbfs,
                    "digital silence must report the floor, not -infinity");
                Assert(PortableNoSpeechDiagnostics.ToDbfs(-1) == PortableNoSpeechDiagnostics.MinimumDbfs,
                    "a negative amplitude must report the floor");
                Assert(PortableNoSpeechDiagnostics.ToDbfs(1.0) == 0.0,
                    $"full scale should be 0 dBFS, got {PortableNoSpeechDiagnostics.ToDbfs(1.0)}");

                // Floors, does not truncate: a negative buckets DOWNWARD. Truncation
                // would put -38.2 in "-35dbfs" and silently shift every bucket.
                Assert(PortableNoSpeechDiagnostics.BucketDbfs(-38.2) == "-40dbfs",
                    $"expected '-40dbfs', got '{PortableNoSpeechDiagnostics.BucketDbfs(-38.2)}'");
                Assert(PortableNoSpeechDiagnostics.BucketDbfs(PortableNoSpeechDiagnostics.MinimumDbfs) == "silent",
                    "the floor buckets to 'silent'");

                Assert(PortableNoSpeechDiagnostics.SilenceThreshold == 0.01f,
                    $"the silence threshold drifted: {PortableNoSpeechDiagnostics.SilenceThreshold}");
                Assert(PortableNoSpeechDiagnostics.ConfirmedSilencePeakDbfs == -50.0,
                    "the confirmed-silence peak drifted");
                Assert(PortableNoSpeechDiagnostics.LowSignalRmsDbfs == -38.0,
                    "the low-signal RMS threshold drifted");
                Assert(PortableNoSpeechDiagnostics.LowSignalNonSilentRatio == 0.06,
                    "the low-signal ratio threshold drifted");
            });

            Run("PortableNoSpeechDiagnostics.Summarize guards an empty accumulation and reports full scale as 0 dBFS (#291)", () =>
            {
                var empty = PortableNoSpeechDiagnostics.Summarize(
                    new PortableSignalAccumulation(0, 0, 0, 0));
                Assert(empty.PeakDbfs == PortableNoSpeechDiagnostics.MinimumDbfs,
                    "an empty accumulation must summarize to the floor, not NaN");
                Assert(empty.RmsDbfs == PortableNoSpeechDiagnostics.MinimumDbfs,
                    "an empty accumulation must summarize to the floor, not NaN");
                Assert(empty.NonSilentRatio == 0, "an empty accumulation has no non-silent ratio");

                var fullScale = PortableNoSpeechDiagnostics.Summarize(
                    new PortableSignalAccumulation(4, 4, 4.0, 1.0));
                Assert(fullScale.PeakDbfs == 0.0, $"expected 0 dBFS peak, got {fullScale.PeakDbfs}");
                Assert(fullScale.RmsDbfs == 0.0, $"expected 0 dBFS RMS, got {fullScale.RmsDbfs}");
                Assert(fullScale.NonSilentRatio == 1.0, $"expected a ratio of 1, got {fullScale.NonSilentRatio}");
            });

            Run("PortableNoSpeechDiagnostics.Classify keeps the empty-recording arm ahead of the empty-transcript arm (#291)", () =>
            {
                // Arm order is load-bearing and now lives in Rust. A zero-sample
                // recording is a recorder failure and must keep its own identity even
                // when the provider ALSO returned an empty transcript without its flag.
                var outcome = PortableNoSpeechDiagnostics.Classify(new PortableNoSpeechInput(
                    AnalysisSucceeded: true,
                    DecodedSampleCount: 0,
                    EmptyTranscriptWithoutFlag: true,
                    BackendNoSpeechDetected: false,
                    PeakDbfs: -120,
                    RmsDbfs: -120,
                    NonSilentRatio: 0));

                Assert(outcome == PortableNoSpeechOutcome.EmptyRecording,
                    $"expected EmptyRecording, got {outcome}");

                // An unknown count (no decode loop ran) is deliberately NOT empty.
                var unknown = PortableNoSpeechDiagnostics.Classify(new PortableNoSpeechInput(
                    AnalysisSucceeded: true,
                    DecodedSampleCount: null,
                    EmptyTranscriptWithoutFlag: false,
                    BackendNoSpeechDetected: true,
                    PeakDbfs: -120,
                    RmsDbfs: -120,
                    NonSilentRatio: 0));

                Assert(unknown == PortableNoSpeechOutcome.Skip,
                    $"an unknown sample count must fall through to the ordinary arms, got {unknown}");
            });

            Run("PortableNoSpeechDiagnostics fingerprints an absent mode differently from a blank one (#291)", () =>
            {
                var absent = string.Join("|", PortableNoSpeechDiagnostics.BuildFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", null));
                var blank = string.Join("|", PortableNoSpeechDiagnostics.BuildFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech",
                    new PortableModeIdentity(null, null, null)));

                Assert(absent != blank,
                    $"'no mode at all' and 'a mode with nothing written on it' must not group together, both were '{absent}'");
                Assert(absent.EndsWith("|unknown|none", StringComparison.Ordinal),
                    $"an absent mode should end 'unknown|none', got '{absent}'");
                Assert(blank.EndsWith("|local|none", StringComparison.Ordinal),
                    $"a blank mode routes local with no engine, got '{blank}'");
            });

            Run("TranscriptionDiagnosticsService.AnalyzeAudioFile measures 16 kHz mono, not the container format (#291)", () =>
            {
                // Decision 2: Windows used to accumulate over the container's own
                // interleaved samples, so a 48 kHz stereo recording and the 16 kHz mono
                // audio actually sent to the provider produced different non-silent
                // ratios for the same clip - and a different one again from macOS, which
                // already measured post-conversion. The reported sample rate and channel
                // count must still be the SOURCE container's facts.
                var wavPath = Path.Combine(
                    Path.GetTempPath(),
                    $"HyperWhisper.SmokeTests.NoSpeech.{Guid.NewGuid():N}.wav");

                try
                {
                    var format = new NAudio.Wave.WaveFormat(48000, 16, 2);
                    using (var writer = new NAudio.Wave.WaveFileWriter(wavPath, format))
                    {
                        var frame = new float[2];
                        for (var i = 0; i < 48000; i++)
                        {
                            var value = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
                            frame[0] = value;
                            frame[1] = value;
                            writer.WriteSamples(frame, 0, 2);
                        }
                    }

                    var diagnostics = TranscriptionDiagnosticsService.AnalyzeAudioFile(wavPath, null);

                    Assert(diagnostics.AnalysisSucceeded, "the generated WAV should analyze cleanly");
                    Assert(diagnostics.SampleRate == 48000,
                        $"audio_sample_rate_hz must stay the source rate, got {diagnostics.SampleRate}");
                    Assert(diagnostics.Channels == 2,
                        $"audio_channels must stay the source channel count, got {diagnostics.Channels}");

                    // One second of audio. The two counts mean different things:
                    // DecodedSampleCount is pre-resample mono frames (48000 here, one per
                    // source frame, NOT the 96000 interleaved samples the container holds),
                    // and MeasuredSampleCount is the ~16000 samples the dBFS figures were
                    // measured over.
                    var decoded = diagnostics.DecodedSampleCount ?? -1;
                    Assert(decoded == 48000,
                        $"expected 48000 pre-resample mono frames, got {decoded}");
                    var measured = diagnostics.MeasuredSampleCount ?? -1;
                    Assert(measured > 15000 && measured < 17000,
                        $"expected ~16000 post-resample mono samples, got {measured}");

                    // A 0.5-amplitude sine is ~-6 dBFS peak and audible throughout.
                    Assert(diagnostics.PeakDbfs > -8 && diagnostics.PeakDbfs < -4,
                        $"expected a peak near -6 dBFS, got {diagnostics.PeakDbfs}");
                    Assert(diagnostics.NonSilentRatio > 0.9,
                        $"a continuous sine should be almost entirely non-silent, got {diagnostics.NonSilentRatio}");
                }
                finally
                {
                    try
                    {
                        File.Delete(wavPath);
                    }
                    catch
                    {
                        // Best-effort cleanup; a leftover temp file must not fail CI.
                    }
                }
            });

            Run("TranscriptionDiagnosticsService.AnalyzeAudioFile folds any channel count to mono (#291)", () =>
            {
                // NAudio's ToMono()/StereoToMonoSampleProvider throw on more than two
                // channels, so a 3-channel file used to stay interleaved: one live channel
                // among two silent ones measured a third of the true non-silent ratio and
                // several dB of extra RMS headroom, which is enough to move it across the
                // backend-confirmed low-signal threshold and skip an event that should be
                // reported. Folding must handle any channel count and must never downgrade
                // a readable file to AnalysisSucceeded: false.
                var wavPath = Path.Combine(
                    Path.GetTempPath(),
                    $"HyperWhisper.SmokeTests.NoSpeech.Multichannel.{Guid.NewGuid():N}.wav");

                try
                {
                    var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 3);
                    using (var writer = new NAudio.Wave.WaveFileWriter(wavPath, format))
                    {
                        var frame = new float[3];
                        for (var i = 0; i < 48000; i++)
                        {
                            // One channel at a steady 0.06, two digitally silent.
                            frame[0] = 0.06f;
                            frame[1] = 0f;
                            frame[2] = 0f;
                            writer.WriteSamples(frame, 0, 3);
                        }
                    }

                    var diagnostics = TranscriptionDiagnosticsService.AnalyzeAudioFile(wavPath, null);

                    Assert(diagnostics.AnalysisSucceeded,
                        "a 3-channel file must analyze, not fall back to AnalysisSucceeded: false");
                    Assert(diagnostics.Channels == 3,
                        $"audio_channels must stay the source channel count, got {diagnostics.Channels}");
                    Assert(diagnostics.SampleRate == 48000,
                        $"audio_sample_rate_hz must stay the source rate, got {diagnostics.SampleRate}");

                    // 48000 source frames folded to 48000 mono frames - not 144000
                    // interleaved samples.
                    var decoded = diagnostics.DecodedSampleCount ?? -1;
                    Assert(decoded == 48000,
                        $"expected 48000 folded mono frames, got {decoded}");

                    // Folded, every sample is 0.06/3 = 0.02, above the 0.01 silence
                    // threshold, so the ratio is ~1. Interleaved it would have been the
                    // ~0.3333 of one live channel in three.
                    Assert(diagnostics.NonSilentRatio > 0.9,
                        $"the folded signal should be non-silent throughout, got {diagnostics.NonSilentRatio}");
                    // 0.02 linear is -33.98 dBFS. Interleaved, the peak would have been
                    // the raw 0.06 of the live channel, about -24.4 dBFS.
                    Assert(diagnostics.PeakDbfs > -35 && diagnostics.PeakDbfs < -33,
                        $"expected a folded peak near -34 dBFS, got {diagnostics.PeakDbfs}");
                    Assert(diagnostics.RmsDbfs > -35 && diagnostics.RmsDbfs < -33,
                        $"expected a folded RMS near -34 dBFS, got {diagnostics.RmsDbfs}");
                }
                finally
                {
                    try
                    {
                        File.Delete(wavPath);
                    }
                    catch
                    {
                        // Best-effort cleanup; a leftover temp file must not fail CI.
                    }
                }
            });

            Run("TranscriptionDiagnosticsService.AnalyzeAudioFile does not call a tiny file an empty recording (#291)", () =>
            {
                // WdlResamplingSampleProvider.Read returns 0 until it can emit a whole
                // output frame, so a decodable file shorter than the sinc window measures
                // zero 16 kHz samples. DecodedSampleCount is counted before the resampler
                // precisely so arm 2 does not report "the recorder produced nothing" for a
                // file the recorder plainly did produce.
                var wavPath = Path.Combine(
                    Path.GetTempPath(),
                    $"HyperWhisper.SmokeTests.NoSpeech.Tiny.{Guid.NewGuid():N}.wav");

                try
                {
                    var format = new NAudio.Wave.WaveFormat(48000, 16, 1);
                    using (var writer = new NAudio.Wave.WaveFileWriter(wavPath, format))
                    {
                        var samples = new float[8];
                        for (var i = 0; i < samples.Length; i++)
                        {
                            samples[i] = 0.5f;
                        }

                        writer.WriteSamples(samples, 0, samples.Length);
                    }

                    var diagnostics = TranscriptionDiagnosticsService.AnalyzeAudioFile(wavPath, null);

                    Assert(diagnostics.AnalysisSucceeded, "a tiny WAV should still analyze");
                    Assert(diagnostics.DecodedSampleCount == 8,
                        $"the decoder produced 8 frames, got {diagnostics.DecodedSampleCount}");

                    var provider = new TranscriptionProviderDiagnostics(
                        ProviderDisplayName: "test", BackendNoSpeechDetected: true);
                    Assert(
                        TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(diagnostics, provider)
                            != PortableNoSpeechOutcome.EmptyRecording,
                        "a decodable file must never be reported as 'the recorder produced nothing'");
                }
                finally
                {
                    try
                    {
                        File.Delete(wavPath);
                    }
                    catch
                    {
                        // Best-effort cleanup; a leftover temp file must not fail CI.
                    }
                }
            });

            Run("MonoFoldSampleProvider carries a partial frame instead of ending the stream (#291)", () =>
            {
                // A source may legally return any count, including one that ends mid-frame.
                // Dividing that count by the channel count gave 0 frames, and BOTH the analysis
                // loop and WdlResamplingSampleProvider read a 0 as end-of-stream - so the
                // measurement silently stopped at the prefix and still reported
                // AnalysisSucceeded: true. Dropping the remainder instead of carrying it would
                // also rotate every later frame across the channels.
                var interleaved = new[] { 1f, 3f, 5f, 7f, 9f, 11f, 13f, 15f, 17f, 19f };
                // The first read is ONE sample of a two-sample frame.
                var source = new ChoppySampleProvider(interleaved, 2, [1, 2, 3, 4]);
                var fold = new TranscriptionDiagnosticsService.MonoFoldSampleProvider(source);

                var buffer = new float[16];
                var produced = new List<float>();
                int read;
                while ((read = fold.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        produced.Add(buffer[i]);
                    }
                }

                Assert(produced.Count == 5,
                    $"expected all 5 folded frames, got {produced.Count} - a short read ended the stream");
                for (var i = 0; i < produced.Count; i++)
                {
                    var expected = 2f + (4f * i);
                    Assert(Math.Abs(produced[i] - expected) < 1e-6,
                        $"frame {i} should be {expected}, got {produced[i]} - the frames are channel-rotated");
                }
            });

            Run("Deepgram's SessionStartsOnWebSocketOpen is true, and only Deepgram's (regression for #100)", () =>
            {
                // Deepgram never sends its only session-shaped message (Metadata) until
                // after audio is sent, so startup must not block waiting for it — the
                // client should treat the WebSocket handshake itself as session-start.
                // A regression to false here reintroduces a guaranteed 10s connect
                // timeout on every Windows Deepgram live session.
                //
                // The flag now comes off the shared core's connect descriptor
                // (issue #281) rather than a per-strategy literal, so the other four
                // are asserted here too: the core answering true for one of them
                // would deadlock its first chunk in the opposite direction.
                using var deepgram = LiveStrategy(StreamingTranscriptionProvider.Deepgram);
                Assert(deepgram.SessionStartsOnWebSocketOpen,
                    "Deepgram must start streaming on WebSocket open, not wait for a Metadata message");

                foreach (var provider in new[]
                         {
                             StreamingTranscriptionProvider.HyperWhisperCloud,
                             StreamingTranscriptionProvider.ElevenLabs,
                             StreamingTranscriptionProvider.OpenAI,
                             StreamingTranscriptionProvider.Xai,
                         })
                {
                    using var strategy = LiveStrategy(provider);
                    Assert(!strategy.SessionStartsOnWebSocketOpen,
                        $"{provider} must wait for its own session-started message");
                }
            });

            Run("Deepgram still classifies a late Metadata message as SessionStarted", () =>
            {
                // Even though startup no longer blocks on Metadata, a Metadata message
                // can still legitimately arrive later (after audio starts flowing) and
                // must keep parsing correctly.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.Deepgram);
                var evt = strategy.ParseMessage("{\"type\":\"Metadata\",\"request_id\":\"abc-123\"}");

                Assert(evt is StreamingProviderEvent.SessionStarted, $"expected SessionStarted, got {evt}");
                var sessionStarted = (StreamingProviderEvent.SessionStarted)evt!;
                Assert(sessionStarted.SessionId == "abc-123", $"expected request id 'abc-123', got '{sessionStarted.SessionId}'");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats an abnormal provider close (1008) as terminal", () =>
            {
                // Deepgram's real DATA-xxxx payload errors close with 1008 - before the fix,
                // HandleCloseResult only recognized HyperWhisper's own 4001/4002 codes and let
                // everything else fall through to ~3s of doomed reconnect churn before finally
                // surfacing a generic message instead of the provider's own close description.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                // HandleCloseResult's own shutdown guard no-ops on a freshly constructed
                // (Idle) client - drive it into a realistic in-session state first.
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1008, "DATA-0000: invalid audio codec"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for an abnormal close code");
                Assert(capturedMessage!.Contains("DATA-0000"), $"expected the provider's close description to surface, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats an abnormal provider close (1011) as terminal", () =>
            {
                // Deepgram's NET-xxxx errors (timeout / no audio) close with 1011.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1011, "NET-0000: timeout"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for an abnormal close code");
                Assert(capturedMessage!.Contains("NET-0000"), $"expected the provider's close description to surface, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult still recognizes HyperWhisper's own 4001 (credits exhausted)", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)4001, "credits exhausted"));

                Assert(capturedMessage == "Streaming stopped because credits are exhausted.", $"got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult still recognizes HyperWhisper's own 4002 (max session duration)", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)4002, "max duration reached"));

                Assert(capturedMessage == "Streaming stopped because the maximum session duration was reached.", $"got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult does not treat a normal closure (1000) as terminal", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, ""));

                Assert(capturedMessage == null, $"expected no ErrorReceived for a normal closure, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Streaming, $"expected State to remain Streaming, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult does not treat a null close status as terminal", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, null, null));

                Assert(capturedMessage == null, $"expected no ErrorReceived when CloseStatus is null (ambiguous), got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Streaming, $"expected State to remain Streaming, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats a transient close (1006 abnormal) as recoverable", () =>
            {
                // 1006 (no close frame) is a textbook-transient WebSocket close code - it must
                // keep falling through to the existing reconnect/backoff path, not be treated
                // as terminal (the earlier blanket "any non-1000 code is terminal" diff broke
                // this).
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1006, "abnormal closure"));

                Assert(capturedMessage == null, $"expected no ErrorReceived for a transient close code, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Streaming, $"expected State to remain Streaming, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult does not reclassify our own shutdown as a provider error", () =>
            {
                // Even a code that would otherwise be terminal (e.g. 1011) must not overwrite
                // an in-flight StopAsync/Dispose shutdown - HandleCloseResult can still observe
                // a close arriving concurrently with our own Disconnecting/Idle transition.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Disconnecting);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1011, "NET-0000: timeout"));

                Assert(capturedMessage == null, $"expected no ErrorReceived while Disconnecting, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Disconnecting, $"expected State to remain Disconnecting, got {client.State}");
            });

            Run("every StreamingTranscriptionProvider round-trips through its storage value", () =>
            {
                // The enum has SIX hand-maintained switches and not one of them is
                // compiler-enforced - every arm ends in `_ =>`. The quiet one is
                // IsValidStorageValue: SettingsService.StreamingProvider's setter
                // resets anything it rejects to hyperwhisperCloud, so a member
                // missing from it makes the user's selection silently revert on the
                // next save. Nothing tested this before; walk the whole enum.
                foreach (var provider in Enum.GetValues<StreamingTranscriptionProvider>())
                {
                    var storage = provider.StorageValue();
                    Assert(!string.IsNullOrWhiteSpace(storage), $"{provider}: empty storage value");
                    Assert(StreamingTranscriptionProviderExtensions.IsValidStorageValue(storage),
                        $"{provider}: storage value '{storage}' is rejected by IsValidStorageValue, so the setting would silently revert");
                    Assert(StreamingTranscriptionProviderExtensions.FromStorageValue(storage) == provider,
                        $"{provider}: '{storage}' round-trips to {StreamingTranscriptionProviderExtensions.FromStorageValue(storage)}");
                    Assert(!string.IsNullOrWhiteSpace(provider.DisplayName()), $"{provider}: empty display name");
                }
                Assert(!StreamingTranscriptionProviderExtensions.IsValidStorageValue("noSuchProvider"),
                    "an unknown storage value must stay invalid");
            });

            Run("the Gemini live setup frame puts the config at setup.input_audio_transcription", () =>
            {
                // TRAP: the LIVE model takes its transcription config here, while the
                // PRE-RECORDED model takes the same object at
                // setup.generation_config.transcription_config. Sending the
                // pre-recorded shape to the live socket closes it with 1007. Pinned
                // against shared-conformance/live-frame-vectors.json.
                // Asserted by PATH, not as raw text: the frame is built by the
                // shared core now (issue #281), and serde_json sorts object keys
                // where the old hand-written builder emitted them in declaration
                // order. Google's protobuf-JSON reader is order-insensitive, so
                // pinning the literal string would pin the serializer rather
                // than the contract. The contract is WHERE each value sits.
                static JsonElement Setup(StreamingSessionConfig config)
                {
                    using var strategy = LiveStrategy(
                        StreamingTranscriptionProvider.GeminiTranscribe, config);
                    Assert(strategy.BuildWebSocketUri(config) is not null,
                        "Gemini must build a connect URL from an API key");
                    var frames = strategy.GetStartMessages(config);
                    Assert(frames.Count == 1, $"expected one setup frame, got {frames.Count}");
                    return JsonDocument.Parse(frames[0].Data).RootElement.Clone();
                }

                var config = new StreamingSessionConfig(
                    null, null, "en-US", ["HyperWhisper"], "AIza-test", null, false, false);
                var setup = Setup(config).GetProperty("setup");
                Assert(setup.GetProperty("model").GetString() == "models/gemini-3.5-transcribe-live",
                    "unexpected live model");
                var transcription = setup.GetProperty("input_audio_transcription");
                Assert(transcription.GetProperty("language_codes")[0].GetString() == "en-US",
                    "language_codes must carry the selected tag");
                Assert(transcription.GetProperty("custom_vocabulary")[0].GetString() == "HyperWhisper",
                    "custom_vocabulary must carry the terms");
                // The wrong-but-plausible position must be ABSENT, not merely unused.
                Assert(!setup.TryGetProperty("generation_config", out _),
                    "the pre-recorded config position must stay empty on the live socket");

                // Auto-detect drops language_codes but KEEPS custom_vocabulary. The
                // "no vocabulary without a language" rule is Deepgram's, not Google's,
                // and vocabulary is the headline reason to pick this provider.
                var auto = new StreamingSessionConfig(
                    null, null, "auto", ["Kalamazoo"], "AIza-test", null, false, false);
                var autoTranscription = Setup(auto)
                    .GetProperty("setup").GetProperty("input_audio_transcription");
                Assert(!autoTranscription.TryGetProperty("language_codes", out _),
                    "auto means send no language at all");
                Assert(autoTranscription.GetProperty("custom_vocabulary")[0].GetString() == "Kalamazoo",
                    "Gemini takes custom_vocabulary under auto-detect");

                // Region preserved, unlike the primary-subtag providers.
                var region = new StreamingSessionConfig(
                    null, null, "en-GB", null, "AIza-test", null, false, false);
                Assert(Setup(region).GetProperty("setup")
                        .GetProperty("input_audio_transcription")
                        .GetProperty("language_codes")[0].GetString() == "en-GB",
                    "en-GB must not be flattened to en");
            });

            Run("Gemini maps interim to partial and inputTranscription to final without diffing", () =>
            {
                // NOT xAI-shaped. interimInputTranscription is cumulative only WITHIN
                // a turn and restarts after each final; inputTranscription carries
                // only that turn's committed text. Prefix-diffing would emit nothing
                // for a second turn whose text repeats the first.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.GeminiTranscribe);
                Assert(strategy.ParseMessage("{\"setupComplete\":{}}") is StreamingProviderEvent.SessionStarted,
                    "setupComplete must start the session");
                Assert(strategy.ParseMessage("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"hel\"}}}")
                    is StreamingProviderEvent.PartialTranscript { Text: "hel" }, "interim must be a partial");
                Assert(strategy.ParseMessage("{\"serverContent\":{\"inputTranscription\":{\"text\":\"again.\"}}}")
                    is StreamingProviderEvent.FinalTranscript { Text: "again." }, "inputTranscription must be a final");
                Assert(strategy.ParseMessage("{\"serverContent\":{\"inputTranscription\":{\"text\":\"again.\"}}}")
                    is StreamingProviderEvent.FinalTranscript { Text: "again." },
                    "a repeated turn must be emitted whole - diffing would swallow it");
                // Maps to SessionComplete, but whether that is TERMINAL is decided
                // by CompleteEndsSessionBeforeStop - see the turn-boundary test.
                Assert(strategy.ParseMessage("{\"serverContent\":{\"generationComplete\":true}}")
                    is StreamingProviderEvent.SessionComplete, "generationComplete must map to the completion event");
                Assert(strategy.ParseMessage("{\"usageMetadata\":{\"totalTokenCount\":3}}") == null,
                    "an unmodelled frame must be ignored, not an error");
                Assert(strategy.ParseMessage("{\"error\":{\"code\":1007,\"message\":\"invalid setup\"}}")
                    is StreamingProviderEvent.Error { Message: "invalid setup" }, "error frame must surface its message");
            });

            Run("the HyperWhisper Cloud route is derived from the selected cloud tier", () =>
            {
                // /ws/streaming-{sttProvider}, derived by the shared core from the
                // catalog entry the tier names. deepgramNova3 must stay
                // byte-identical to the literal it replaced, because every installed
                // client sends no tier at all; an unknown tier must fall back rather
                // than derive a path the backend will 404.
                static Uri Route(string? tier, string language, IReadOnlyList<string>? vocabulary = null)
                {
                    var config = new StreamingSessionConfig(
                        "lic", null, language, vocabulary, null, null, false, false, tier);
                    using var strategy = LiveStrategy(
                        StreamingTranscriptionProvider.HyperWhisperCloud, config);
                    var uri = strategy.BuildWebSocketUri(config);
                    Assert(uri is not null, $"tier '{tier}' must build a connect URL");
                    return uri!;
                }

                Assert(Route("deepgramNova3", "en").AbsolutePath == "/ws/streaming-deepgram",
                    "deepgramNova3 must derive /ws/streaming-deepgram");
                Assert(Route("geminiTranscribe", "en").AbsolutePath == "/ws/streaming-gemini-transcribe",
                    "geminiTranscribe must derive /ws/streaming-gemini-transcribe");
                foreach (var bogus in new string?[] { null, "", "   ", "notATier", "groqWhisper" })
                {
                    Assert(Route(bogus, "en").AbsolutePath == "/ws/streaming-deepgram",
                        $"tier '{bogus}' must fall back to Deepgram");
                }

                // The auto-detect vocabulary gate is Deepgram's constraint, so it must
                // NOT be applied to the Gemini tier.
                Assert(!Route("deepgramNova3", "auto", ["Kalamazoo"]).Query.Contains("vocabulary="),
                    "Deepgram must withhold vocabulary in auto-detect");
                Assert(Route("geminiTranscribe", "auto", ["Kalamazoo"]).Query.Contains("vocabulary=Kalamazoo"),
                    "Gemini must send vocabulary in auto-detect");
            });

            Run("the live cloud tier picker offers exactly the vendors we serve a WS route for", () =>
            {
                // The STT catalog has no `enabled` gate, and the client derives its
                // route with no allow-list of its own, so this list IS the guard
                // against shipping a picker row that 404s at dictation time.
                var ids = CloudSttCatalog.Shared.StreamingCloudTierEntries().Select(e => e.Id).ToArray();
                Assert(ids.SequenceEqual(new[] { "deepgramNova3", "geminiTranscribe" }),
                    $"unexpected live tier set: {string.Join(", ", ids)}");

                // The settings ComboBox binds SelectedValue to the stored id with
                // SelectedValuePath="Id", and WPF matches that case-SENSITIVELY. A
                // hand-edited settings.json or a retired tier therefore has to come
                // back in the catalog's own casing, or the row renders blank while
                // the session quietly runs on the fallback.
                Assert(SettingsService.NormalizeStreamingCloudTier(" geminiTranscribe ") == "geminiTranscribe",
                    "a padded id must normalize");
                Assert(SettingsService.NormalizeStreamingCloudTier("GEMINITRANSCRIBE") == "geminiTranscribe",
                    "a case variant must come back in the catalog's casing, not the caller's");
                foreach (var bogus in new string?[] { null, "", "   ", "notATier", "googleChirp3" })
                {
                    Assert(SettingsService.NormalizeStreamingCloudTier(bogus) == "deepgramNova3",
                        $"tier '{bogus}' must clamp to the default");
                }
                Assert(ids.Contains(LiveProtocolStreamingStrategy.DefaultCloudTier),
                    "the fallback tier must itself be offered, or the picker is blank by default");
            });

            Run("IStreamingProviderStrategy.IsTerminalCloseCode default covers the standard fatal WebSocket protocol codes", () =>
            {
                // The terminal-code allowlist moved from a StreamingTranscriptionClient-private,
                // Deepgram-flavored comment into the strategy interface as a default interface
                // method, and from there into the shared Rust core (issue #281) - so this runs
                // through the real FFI and is the Windows half of a cross-platform check.
                // No implementation overrides the default, so any strategy exercises it.
                // Typed as the interface (not the concrete class) so this actually calls through
                // to the default interface method - C# only considers a DIM when the member is
                // accessed via the interface type.
                IStreamingProviderStrategy strategy = new NoOpStreamingProviderStrategy();
                foreach (var fatalCode in new[] { 1002, 1003, 1007, 1008, 1009, 1011 })
                {
                    Assert(strategy.IsTerminalCloseCode(fatalCode), $"expected close code {fatalCode} to be terminal by default");
                }
                foreach (var transientCode in new[] { 1000, 1001, 1006, 1012, 1013 })
                {
                    Assert(!strategy.IsTerminalCloseCode(transientCode), $"expected close code {transientCode} to remain recoverable");
                }
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats Message Too Big (1009) as terminal", () =>
            {
                // Confirmed non-hypothetical: hyperwhisper-cloud/src/routes/ws-streaming-deepgram.ts
                // sends 1009 for an oversized audio stream from the HyperWhisperCloud backend.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1009, "message too big"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for close code 1009");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats Protocol Error (1002) as terminal", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1002, "protocol error"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for close code 1002");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult still records terminal-close bookkeeping when State is already Error", () =>
            {
                // Regression for: an in-band provider error (HandleProviderEvent's Error case)
                // already moved State to Error before the socket's terminal close frame arrives.
                // TryChangeStateUnless(Error, ...) used to fail in that case because State already
                // equalled the target, so ErrorReceived never fired again and _receivedTerminalClose
                // never got set for this close - losing the last partial transcript in StopAsync
                // (which falls back to FinalText instead of preserving CurrentPartial). The "already
                // at target state" case must still be treated as success for this bookkeeping.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Error);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1011, "NET-0000: timeout"));

                Assert(capturedMessage != null, "expected ErrorReceived to still fire when State was already Error");
                Assert(client.State == StreamingConnectionState.Error, $"expected State to remain Error, got {client.State}");
            });

            // THE FIVE LIVE PROTOCOLS, from the shared Rust core (issue #281).
            //
            // Windows no longer implements any of them. These run through the real
            // FFI on the real DLL, so between them they are this head's proof that
            // the provider mapping, the connect descriptor, the framing rule, the
            // parsers and the ordered stop paths survived the move. The portable
            // HyperWhisper.SharedCore.Tests suite asserts the same values from the
            // Linux head; a difference between the two would be a mapping bug here.

            Run("Every streaming provider maps onto the shared core's capability table", () =>
            {
                var expected = new (StreamingTranscriptionProvider Provider, string Label, int SampleRate, bool Vocabulary)[]
                {
                    (StreamingTranscriptionProvider.HyperWhisperCloud, "HyperWhisper Cloud (Streaming)", 16000, true),
                    (StreamingTranscriptionProvider.Deepgram, "Deepgram (Streaming)", 16000, true),
                    (StreamingTranscriptionProvider.ElevenLabs, "ElevenLabs (Streaming)", 16000, false),
                    (StreamingTranscriptionProvider.OpenAI, "OpenAI (Streaming)", 24000, false),
                    (StreamingTranscriptionProvider.Xai, "SpaceXAI (Streaming)", 16000, true),
                };

                foreach (var (provider, label, sampleRate, vocabulary) in expected)
                {
                    using var strategy = LiveStrategy(provider);

                    // The " (Streaming)" suffix is persisted on every history entry:
                    // changing one splits the vendor in two in the history list.
                    Assert(strategy.TranscriptionProviderLabel == label,
                        $"{provider}: expected label '{label}', got '{strategy.TranscriptionProviderLabel}'");
                    // Wrong sample rate is not an error, it is a transcript at the
                    // wrong speed - the capture graph is configured from this.
                    Assert(strategy.AudioSampleRate == sampleRate,
                        $"{provider}: expected {sampleRate}Hz, got {strategy.AudioSampleRate}Hz");
                    Assert(strategy.SupportsVocabulary == vocabulary,
                        $"{provider}: expected SupportsVocabulary {vocabulary}");
                    // The settings page reads the capability with no credential and
                    // no session, straight off the free function.
                    Assert(StreamingTranscriptionSessionFactory.SupportsVocabulary(provider) == vocabulary,
                        $"{provider}: the factory and the strategy disagree about vocabulary support");
                }
            });

            Run("History provider labels normalize current and legacy SpaceXAI names", () =>
            {
                var converter = new ProviderNameDisplayConverter();

                Assert(
                    converter.Convert("SpaceXAI (Streaming)", typeof(string), null!, CultureInfo.InvariantCulture)
                        as string == "SpaceXAI (Streaming)",
                    "the current persisted label must keep the SpaceXAI capitalization");
                Assert(
                    converter.Convert("xAI (Streaming)", typeof(string), null!, CultureInfo.InvariantCulture)
                        as string == "SpaceXAI (Streaming)",
                    "the legacy persisted label must display as SpaceXAI");
            });

            Run("Every streaming provider builds its shipped connect URL", () =>
            {
                using (var deepgram = LiveStrategy(StreamingTranscriptionProvider.Deepgram))
                {
                    var url = deepgram.BuildWebSocketUri(LiveConfig())!.AbsoluteUri;
                    // The thirteen constant parameters, plus the explicit language.
                    // macOS sends ten; the .NET set won (issue #281).
                    Assert(url.StartsWith("wss://api.deepgram.com/v1/listen?model=nova-3-general&", StringComparison.Ordinal),
                        $"expected the resolved default model to lead the query, got '{url}'");
                    foreach (var pair in new[]
                             {
                                 "encoding=linear16", "sample_rate=16000", "channels=1", "smart_format=true",
                                 "punctuate=true", "filler_words=true", "no_delay=false", "endpointing=300",
                                 "utterance_end_ms=1500", "interim_results=true", "vad_events=true",
                                 "mip_opt_out=true", "language=en",
                             })
                    {
                        Assert(url.Contains(pair, StringComparison.Ordinal), $"expected '{pair}' in '{url}'");
                    }
                    Assert(!url.Contains("detect_language", StringComparison.Ordinal),
                        "an explicit language must not also ask Deepgram to detect one");

                    // Auto-detect is spelled with a parameter, not by omitting one.
                    var auto = deepgram.BuildWebSocketUri(LiveConfig(language: "auto"))!.AbsoluteUri;
                    Assert(auto.Contains("detect_language=true", StringComparison.Ordinal),
                        $"expected detect_language=true under auto, got '{auto}'");
                    // "&language=", not "language=": detect_language=true contains
                    // the shorter needle, so the bare form can never be absent.
                    Assert(!auto.Contains("&language=", StringComparison.Ordinal),
                        $"expected no language= under auto, got '{auto}'");
                }

                using (var cloud = LiveStrategy(StreamingTranscriptionProvider.HyperWhisperCloud))
                {
                    var url = cloud.BuildWebSocketUri(LiveConfig())!.AbsoluteUri;
                    Assert(url.StartsWith(
                            "wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-deepgram?license_key=smoke-license-key",
                            StringComparison.Ordinal),
                        $"expected the production relay with the license key first, got '{url}'");
                    Assert(url.Contains("language=en", StringComparison.Ordinal), $"expected language=en, got '{url}'");
                }

                using (var elevenLabs = LiveStrategy(StreamingTranscriptionProvider.ElevenLabs))
                {
                    var url = elevenLabs.BuildWebSocketUri(LiveConfig())!.AbsoluteUri;
                    Assert(url.StartsWith("wss://api.elevenlabs.io/v1/speech-to-text/realtime?", StringComparison.Ordinal),
                        $"got '{url}'");
                    foreach (var pair in new[]
                             {
                                 "model_id=scribe_v2_realtime", "audio_format=pcm_16000", "commit_strategy=vad",
                                 "vad_silence_threshold_secs=1.5", "vad_threshold=0.4", "language_code=en",
                             })
                    {
                        Assert(url.Contains(pair, StringComparison.Ordinal), $"expected '{pair}' in '{url}'");
                    }
                }

                using (var openAi = LiveStrategy(StreamingTranscriptionProvider.OpenAI))
                {
                    var url = openAi.BuildWebSocketUri(LiveConfig())!.AbsoluteUri;
                    Assert(url == "wss://api.openai.com/v1/realtime?intent=transcription", $"got '{url}'");

                    // turn_detection null is load-bearing: it disables server-side
                    // VAD, which is what makes the commit gate above ours to get right.
                    var start = openAi.GetStartMessages(LiveConfig());
                    Assert(start.Count == 1, $"expected exactly one start message, got {start.Count}");
                    var frame = System.Text.Encoding.UTF8.GetString(start[0].Data);
                    Assert(start[0].Type == WebSocketMessageType.Text, "the session update is a text frame");
                    Assert(frame.Contains("\"type\":\"session.update\"", StringComparison.Ordinal), $"got '{frame}'");
                    Assert(frame.Contains("\"model\":\"gpt-realtime-whisper\"", StringComparison.Ordinal), $"got '{frame}'");
                    Assert(frame.Contains("\"rate\":24000", StringComparison.Ordinal), $"got '{frame}'");
                    Assert(frame.Contains("\"turn_detection\":null", StringComparison.Ordinal), $"got '{frame}'");
                    Assert(frame.Contains("\"language\":\"en\"", StringComparison.Ordinal), $"got '{frame}'");
                }

                using (var xai = LiveStrategy(StreamingTranscriptionProvider.Xai))
                {
                    var url = xai.BuildWebSocketUri(
                        LiveConfig(vocabulary: ["HyperWhisper", "Deepgram"]))!.AbsoluteUri;
                    Assert(url.StartsWith("wss://api.x.ai/v1/stt?", StringComparison.Ordinal), $"got '{url}'");
                    Assert(url.Contains("language=en", StringComparison.Ordinal), $"got '{url}'");
                    // keyterm is repeated once per term - the xAI vendor shape.
                    Assert(url.Contains("keyterm=HyperWhisper", StringComparison.Ordinal), $"got '{url}'");
                    Assert(url.Contains("keyterm=Deepgram", StringComparison.Ordinal), $"got '{url}'");
                }

                // Four of the five refuse to build a URL with no credential, and the
                // client reads null as "cannot start" without opening a socket.
                foreach (var provider in new[]
                         {
                             StreamingTranscriptionProvider.Deepgram,
                             StreamingTranscriptionProvider.ElevenLabs,
                             StreamingTranscriptionProvider.OpenAI,
                             StreamingTranscriptionProvider.Xai,
                             StreamingTranscriptionProvider.HyperWhisperCloud,
                         })
                {
                    var bare = new StreamingSessionConfig(null, null, "en", null, null, null, false, false);
                    using var strategy = new LiveProtocolStreamingStrategy(provider, bare);
                    Assert(strategy.BuildWebSocketUri(bare) is null,
                        $"{provider} must not build a connect URL with no credential");
                }
            });

            Run("Audio framing comes from the core's descriptor, and the samples never cross the FFI", () =>
            {
                // The core answers HOW to wrap a chunk once, at connect time, and the
                // base64 and the concatenation happen here on bytes this process
                // already holds. Three providers take the PCM untouched; two wrap it
                // in a fixed JSON envelope with one hole in the middle.
                foreach (var provider in new[]
                         {
                             StreamingTranscriptionProvider.Deepgram,
                             StreamingTranscriptionProvider.Xai,
                             StreamingTranscriptionProvider.HyperWhisperCloud,
                         })
                {
                    using var strategy = LiveStrategy(provider);
                    var pcm = new byte[] { 1, 2, 3, 4 };
                    var (data, type) = strategy.EncodeAudioChunk(pcm);
                    Assert(type == WebSocketMessageType.Binary, $"{provider}: expected a binary frame, got {type}");
                    Assert(data.Length == pcm.Length && data[0] == 1 && data[3] == 4,
                        $"{provider}: expected the PCM to go out untouched");
                }

                using (var elevenLabs = LiveStrategy(StreamingTranscriptionProvider.ElevenLabs))
                {
                    var (data, type) = elevenLabs.EncodeAudioChunk(new byte[] { 1, 2, 3, 4 });
                    var frame = System.Text.Encoding.UTF8.GetString(data);
                    Assert(type == WebSocketMessageType.Text, $"expected a text frame, got {type}");
                    Assert(frame ==
                        "{\"message_type\":\"input_audio_chunk\",\"audio_base_64\":\"AQIDBA==\",\"commit\":false,\"sample_rate\":16000}",
                        $"got '{frame}'");
                }

                using (var openAi = LiveStrategy(StreamingTranscriptionProvider.OpenAI))
                {
                    var (data, type) = openAi.EncodeAudioChunk(new byte[] { 1, 2, 3, 4 });
                    var frame = System.Text.Encoding.UTF8.GetString(data);
                    Assert(type == WebSocketMessageType.Text, $"expected a text frame, got {type}");
                    Assert(frame == "{\"type\":\"input_audio_buffer.append\",\"audio\":\"AQIDBA==\"}",
                        $"got '{frame}'");
                }
            });

            Run("Every streaming provider answers its shipped ordered stop path", () =>
            {
                // ORDER IS LOAD-BEARING, and a flat frame list plus one drain timeout
                // could not express it. Deepgram needs the 500ms gap - sending
                // Finalize and CloseStream back to back lets the close be processed
                // before the flush and loses the finalized tail. HyperWhisper Cloud
                // and xAI wait on an EVENT, which is what carries credits_used.
                using (var deepgram = LiveStrategy(StreamingTranscriptionProvider.Deepgram))
                {
                    var steps = deepgram.GetStopSequence();
                    Assert(steps.Count == 4, $"expected 4 Deepgram stop steps, got {steps.Count}");
                    Assert(steps[0].Action == StreamingStopAction.SendMessage
                        && System.Text.Encoding.UTF8.GetString(steps[0].Payload!) == "{\"type\":\"Finalize\"}",
                        "expected Finalize first");
                    Assert(steps[1].Action == StreamingStopAction.Wait
                        && steps[1].WaitAfter == TimeSpan.FromMilliseconds(500),
                        $"expected a 500ms gap, got {steps[1].Action}/{steps[1].WaitAfter}");
                    Assert(steps[2].Action == StreamingStopAction.SendMessage
                        && System.Text.Encoding.UTF8.GetString(steps[2].Payload!) == "{\"type\":\"CloseStream\"}",
                        "expected CloseStream after the gap");
                    Assert(steps[3].Action == StreamingStopAction.Close, "expected a trailing close");
                }

                foreach (var (provider, frame) in new[]
                         {
                             (StreamingTranscriptionProvider.HyperWhisperCloud, "{\"type\":\"stop\"}"),
                             (StreamingTranscriptionProvider.Xai, "{\"type\":\"audio.done\"}"),
                         })
                {
                    using var strategy = LiveStrategy(provider);
                    var steps = strategy.GetStopSequence();
                    Assert(steps.Count == 3, $"{provider}: expected 3 stop steps, got {steps.Count}");
                    Assert(steps[0].Action == StreamingStopAction.SendMessage
                        && System.Text.Encoding.UTF8.GetString(steps[0].Payload!) == frame,
                        $"{provider}: expected '{frame}' first");
                    Assert(steps[1].Action == StreamingStopAction.WaitForSessionComplete
                        && steps[1].WaitAfter == TimeSpan.FromSeconds(10),
                        $"{provider}: expected a 10s wait on the completion EVENT, got {steps[1].Action}/{steps[1].WaitAfter}");
                    Assert(steps[2].Action == StreamingStopAction.Close, $"{provider}: expected a trailing close");
                }

                using (var elevenLabs = LiveStrategy(StreamingTranscriptionProvider.ElevenLabs))
                {
                    // commit_strategy=vad means the server has already committed
                    // everything it intends to: there is nothing to flush or drain.
                    var steps = elevenLabs.GetStopSequence();
                    Assert(steps.Count == 1 && steps[0].Action == StreamingStopAction.Close,
                        $"expected ElevenLabs to close immediately, got {steps.Count} steps");
                }
            });

            Run("Every streaming provider's parser reaches this head's event type", () =>
            {
                using (var cloud = LiveStrategy(StreamingTranscriptionProvider.HyperWhisperCloud))
                {
                    Assert(cloud.ParseMessage("{\"type\":\"ready\",\"sessionId\":\"s-1\"}")
                            is StreamingProviderEvent.SessionStarted { SessionId: "s-1" },
                        "expected ready to carry the session id");
                    Assert(cloud.ParseMessage("{\"type\":\"transcript\",\"text\":\"hi\",\"is_final\":true}")
                            is StreamingProviderEvent.FinalTranscript { Text: "hi" },
                        "expected a final transcript");
                    Assert(cloud.ParseMessage("{\"type\":\"transcript\",\"text\":\"hi\"}")
                            is StreamingProviderEvent.PartialTranscript { Text: "hi" },
                        "expected a partial transcript");
                    // BILLING DATA. The stop path waits on this frame rather than
                    // closing on a timer precisely so it is not lost.
                    Assert(cloud.ParseMessage(
                            "{\"type\":\"session_complete\",\"duration_seconds\":12.5,\"credits_used\":3.25}")
                            is StreamingProviderEvent.SessionComplete { DurationSeconds: 12.5, CreditsUsed: 3.25 },
                        "expected session_complete to carry the duration and the credits");
                    Assert(cloud.ParseMessage("{\"type\":\"warning\",\"message\":\"almost out\"}")
                            is StreamingProviderEvent.Warning { Message: "almost out" },
                        "expected a warning to stay a warning and never end the session");
                    Assert(cloud.ParseMessage("{\"type\":\"error\",\"message\":\"Credit balance exhausted\"}")
                            is StreamingProviderEvent.Error { Message: "Credit balance exhausted" },
                        "expected the provider's own wording to survive, so the classifier can read it");
                    // A frame that is not JSON must not end a recording in progress.
                    Assert(cloud.ParseMessage("not json at all") is null, "expected junk to be ignored");
                    Assert(cloud.ParseMessage("{\"type\":\"something_new\"}") is null,
                        "expected an unknown frame type to be ignored");
                }

                using (var xai = LiveStrategy(StreamingTranscriptionProvider.Xai))
                {
                    // transcript.done is BOTH the last final and the end of the
                    // session; splitting it would drop the trailing words.
                    Assert(xai.ParseMessage("{\"type\":\"transcript.done\",\"text\":\"all done\",\"duration\":4}")
                            is StreamingProviderEvent.FinalTranscriptAndSessionComplete
                            { Text: "all done", DurationSeconds: 4 },
                        "expected transcript.done with text to carry both");
                    // A second one with nothing new left is a plain completion.
                    Assert(xai.ParseMessage("{\"type\":\"transcript.done\",\"text\":\"all done\",\"duration\":4}")
                            is StreamingProviderEvent.SessionComplete,
                        "expected the prefix delta to leave nothing new the second time");
                }

                using (var elevenLabs = LiveStrategy(StreamingTranscriptionProvider.ElevenLabs))
                {
                    // ELEVENLABS' auth_error WORDING CHANGED ON WINDOWS (issue #281).
                    // A shared core cannot name one platform's settings screen, so the
                    // shared sentence names neither. This head used to name its own
                    // "Model Library API keys manager" and macOS said "in Settings";
                    // on THIS head "Settings" dead-ends, because the streaming settings
                    // page only reports whether a key is configured and the field lives
                    // on the API-keys page. The sentence now names the action instead.
                    Assert(elevenLabs.ParseMessage("{\"message_type\":\"auth_error\"}")
                            is StreamingProviderEvent.Error
                            { Message: "ElevenLabs authentication failed. Check that your ElevenLabs API key is correct and still active." },
                        "expected the shared auth_error wording");
                    Assert(elevenLabs.ParseMessage("{\"message_type\":\"quota_exceeded\"}")
                            is StreamingProviderEvent.Error
                            { Message: "ElevenLabs quota exceeded. Please check your account billing." },
                        "quota_exceeded was character-identical and must be unchanged");
                    Assert(elevenLabs.ParseMessage("{\"message_type\":\"rate_limited\"}")
                            is StreamingProviderEvent.Error
                            { Message: "ElevenLabs rate limit reached. Please try again in a moment." },
                        "rate_limited was character-identical and must be unchanged");
                }

                using (var openAi = LiveStrategy(StreamingTranscriptionProvider.OpenAI))
                {
                    Assert(openAi.ParseMessage("{\"type\":\"session.updated\",\"session\":{\"id\":\"o-1\"}}")
                            is StreamingProviderEvent.SessionStarted { SessionId: "o-1" },
                        "expected session.updated to carry the session id");
                    // Deltas accumulate per item: the client's contract for a partial
                    // is the whole interim utterance, not what changed.
                    openAi.ParseMessage(
                        "{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\"he\"}");
                    Assert(openAi.ParseMessage(
                            "{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\"llo\"}")
                            is StreamingProviderEvent.PartialTranscript { Text: "hello" },
                        "expected the deltas to accumulate into one partial");
                    Assert(openAi.ParseMessage(
                            "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"hello\"}")
                            is StreamingProviderEvent.FinalTranscript { Text: "hello" },
                        "expected the completion to confirm the item");
                    Assert(openAi.ParseMessage("{\"type\":\"error\",\"error\":{\"message\":\"buffer too small\"}}")
                            is StreamingProviderEvent.Error { Message: "buffer too small" },
                        "expected OpenAI's nested error wording to survive");
                }
            });

            Run("ConfigureWebSocket carries the core's handshake credentials onto the socket", () =>
            {
                // ClientWebSocketOptions exposes no reader for what was set, so this
                // pins that every provider's handshake is applied without throwing -
                // Deepgram's is two subprotocols ("token", <key>) rather than a header,
                // and AddSubProtocol validates its argument.
                foreach (var provider in new[]
                         {
                             StreamingTranscriptionProvider.HyperWhisperCloud,
                             StreamingTranscriptionProvider.Deepgram,
                             StreamingTranscriptionProvider.ElevenLabs,
                             StreamingTranscriptionProvider.OpenAI,
                             StreamingTranscriptionProvider.Xai,
                         })
                {
                    var config = LiveConfig();
                    using var strategy = LiveStrategy(provider, config);
                    using var webSocket = new ClientWebSocket();
                    Assert(strategy.BuildWebSocketUri(config) != null, $"{provider}: expected a connect URL");
                    strategy.ConfigureWebSocket(webSocket, config);
                }
            });

            // OPENAI'S COMMIT GATE, now the shared core's (issue #281).
            //
            // These eleven checks are the Windows twin of macOS's
            // OpenAIStreamingCommitGateTests.swift. The gate itself moved into
            // hw_net::live::openai and its assertions were ported into
            // live/tests.rs, but they stay here as well and drive the real FFI:
            // this is the Windows half of the cross-platform parity check, and it
            // is the only thing that pins the coupling between
            // StreamingAudioCapture's buffer length and the server's floor.
            //
            // One shared difference from the deleted C# strategy: the core reads
            // no clock of its own, so it seeds its commit mark on the FIRST send
            // opportunity instead of at construction. A session's first
            // opportunity therefore never commits. In production that costs one
            // 100 ms chunk and the 1.2 s interval had not elapsed at that point
            // anyway, but a test that drives the periodic path has to prime it.

            Run("OpenAI's stop sequence omits the commit frame below the 100ms server minimum (HYPERWHISPER-S8/S9)", () =>
            {
                // OpenAI Realtime rejects input_audio_buffer.commit when under 100ms of
                // audio was appended since the previous commit. 4080 bytes is the shape
                // the production events reported: 2040 samples of 24kHz 16-bit mono PCM
                // = 85ms, under the server floor of 4800 bytes. The stop sequence must
                // drop that tail rather than provoke a "buffer too small" error frame.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI);
                strategy.EncodeAudioChunk(new byte[4080]);

                var steps = strategy.GetStopSequence();

                Assert(steps.Count == 2, $"expected 2 stop steps with no commit, got {steps.Count}");
                Assert(steps[0].Action == StreamingStopAction.Wait, $"expected the sequence to still wait, got {steps[0].Action}");
                Assert(steps[1].Action == StreamingStopAction.Close, $"expected the sequence to still close, got {steps[1].Action}");
            });

            Run("OpenAI's stop sequence commits a buffer sitting exactly on the 100ms minimum", () =>
            {
                // THE case Windows actually produces: StreamingAudioCapture sets
                // BufferMilliseconds = 100, so every capture chunk is exactly 4800 bytes
                // (2400 samples of 24kHz 16-bit mono PCM) = exactly 100ms. The server
                // rule is "at least 100ms", so this must commit. Any margin over 100ms
                // would silently discard the user's whole final capture buffer, and with
                // turn_detection null there is no server-side VAD auto-commit to save it.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI);
                strategy.EncodeAudioChunk(new byte[4800]);

                var steps = strategy.GetStopSequence();

                Assert(steps.Count == 3, $"expected 3 stop steps for a 4800-byte tail, got {steps.Count}");
                Assert(steps[0].Action == StreamingStopAction.SendMessage, $"expected a SendMessage step first, got {steps[0].Action}");
                Assert(steps[0].Payload != null, "expected the commit step to carry a payload");
                var boundaryFrame = System.Text.Encoding.UTF8.GetString(steps[0].Payload!);
                Assert(boundaryFrame.Contains("input_audio_buffer.commit"), $"expected the commit frame, got '{boundaryFrame}'");
            });

            Run("OpenAI: a full capture buffer always clears the commit minimum", () =>
            {
                // PINS THE COUPLING, not another byte count. Windows can only ever
                // produce chunks of exactly StreamingAudioCapture.CaptureBufferMilliseconds
                // worth of audio: Stop() clears IsCapturing before StopRecording(), so
                // NAudio's short trailing buffer never reaches EncodeAudioChunk, and the
                // multi-channel path mixes down to the same size. So the pending counter
                // is only ever 0 or a multiple of one buffer.
                //
                // That makes the capture buffer length load-bearing for the commit gate,
                // and silently so: halve it for latency and the last buffer of every
                // OpenAI streaming session stops clearing the 100ms floor and is dropped
                // with no error - the smoke suite would otherwise stay green throughout.
                // Both numbers below come from production code (the sample rate now from
                // the shared core's capability table), so this fails the moment either
                // side of the coupling moves.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI);
                const int bytesPerSample = 2; // 16-bit PCM, as CreateWaveIn requests.
                var captureChunkBytes =
                    StreamingAudioCapture.CaptureBufferMilliseconds * strategy.AudioSampleRate * bytesPerSample / 1000;

                strategy.EncodeAudioChunk(new byte[captureChunkBytes]);
                var steps = strategy.GetStopSequence();

                Assert(
                    steps.Count == 3,
                    $"a single {StreamingAudioCapture.CaptureBufferMilliseconds}ms capture buffer ({captureChunkBytes} bytes at {strategy.AudioSampleRate}Hz) no longer clears the OpenAI commit minimum - Windows will now silently drop the final buffer of every streaming session; got {steps.Count} stop steps");
                Assert(steps[0].Action == StreamingStopAction.SendMessage, $"expected a SendMessage step first, got {steps[0].Action}");
            });

            Run("OpenAI's stop sequence commits once enough audio has accumulated", () =>
            {
                // 12000 bytes = 6000 samples of 24kHz 16-bit mono PCM = 250ms, well over
                // the threshold, so the commit frame must lead the stop sequence.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI);
                strategy.EncodeAudioChunk(new byte[12000]);

                var steps = strategy.GetStopSequence();

                Assert(steps.Count == 3, $"expected 3 stop steps, got {steps.Count}");
                Assert(steps[0].Action == StreamingStopAction.SendMessage, $"expected a SendMessage step first, got {steps[0].Action}");
                Assert(steps[0].Payload != null, "expected the commit step to carry a payload");
                var frame = System.Text.Encoding.UTF8.GetString(steps[0].Payload!);
                Assert(frame.Contains("input_audio_buffer.commit"), $"expected the commit frame, got '{frame}'");
            });

            Run("OpenAI's stop sequence commits the same audio only once", () =>
            {
                // The accumulated bytes are claimed and zeroed in one operation, so a
                // second read of the stop sequence must not re-commit audio the first
                // one already covered.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI);
                strategy.EncodeAudioChunk(new byte[12000]);

                var first = strategy.GetStopSequence();
                var second = strategy.GetStopSequence();

                Assert(first[0].Action == StreamingStopAction.SendMessage, $"expected the first sequence to commit, got {first[0].Action}");
                Assert(second.Count == 2, $"expected the second sequence to drop the commit, got {second.Count} steps");
                Assert(second[0].Action == StreamingStopAction.Wait, $"expected the second sequence to start with a wait, got {second[0].Action}");
            });

            Run("Opening a connection clears audio accumulated by a previous session", () =>
            {
                // A fresh session starts with an empty server-side buffer, so bytes
                // counted before it must not license a commit afterwards.
                //
                // The reset moved with the protocol: the deleted strategy cleared its
                // counter in GetStartMessages, the core clears it in connect() - which
                // this head reaches through BuildWebSocketUri, the call the client makes
                // once before every socket it opens, including a reconnect. Doing it
                // there rather than at the start message also covers the four providers
                // that send no start message at all.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI);
                strategy.EncodeAudioChunk(new byte[12000]);

                Assert(strategy.BuildWebSocketUri(LiveConfig()) != null, "expected a connect URL for a keyed OpenAI session");
                var steps = strategy.GetStopSequence();

                Assert(steps.Count == 2, $"expected 2 stop steps after a session restart, got {steps.Count}");
                Assert(steps[0].Action == StreamingStopAction.Wait, $"expected the sequence to start with a wait, got {steps[0].Action}");
            });

            Run("OpenAI holds back a periodic commit under the 100ms minimum", () =>
            {
                // The clock is injected so the periodic path can be driven with no
                // sleeping; advancing it past the 1.2s interval is what opens the gate.
                // The interval has elapsed, but only 85ms has accumulated, so no commit
                // frame may go out.
                long now = 0;
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI, nowMs: () => now);
                var sent = new List<byte[]>();
                Func<byte[], WebSocketMessageType, CancellationToken, Task> send =
                    (data, type, ct) => { sent.Add(data); return Task.CompletedTask; };

                // Seed the core's commit mark - see the note above this group.
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();
                Assert(sent.Count == 0, $"the seeding opportunity must never commit, got {sent.Count} frames");

                strategy.EncodeAudioChunk(new byte[4080]);
                now += 2000;
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 0, $"expected no periodic commit under the minimum, got {sent.Count} frames");
            });

            Run("OpenAI sends exactly one periodic commit once the minimum is met", () =>
            {
                long now = 0;
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI, nowMs: () => now);
                var sent = new List<byte[]>();
                Func<byte[], WebSocketMessageType, CancellationToken, Task> send =
                    (data, type, ct) => { sent.Add(data); return Task.CompletedTask; };

                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                strategy.EncodeAudioChunk(new byte[12000]);
                now += 2000;
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 1, $"expected exactly one periodic commit, got {sent.Count}");
                var periodicFrame = System.Text.Encoding.UTF8.GetString(sent[0]);
                Assert(periodicFrame.Contains("input_audio_buffer.commit"), $"expected the commit frame, got '{periodicFrame}'");

                // A commit DOES stamp the last-commit time, so with the clock held still
                // the next opportunity must stay quiet.
                strategy.EncodeAudioChunk(new byte[12000]);
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 1, $"expected the interval to gate the second commit, got {sent.Count} frames");
            });

            Run("OpenAI commits on the next qualifying chunk after a byte-gate rejection", () =>
            {
                // The byte gate deliberately leaves the last-commit time STALE when it
                // rejects, so the commit fires on the next chunk that clears the floor
                // rather than a full interval later.
                long now = 0;
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI, nowMs: () => now);
                var sent = new List<byte[]>();
                Func<byte[], WebSocketMessageType, CancellationToken, Task> send =
                    (data, type, ct) => { sent.Add(data); return Task.CompletedTask; };

                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                strategy.EncodeAudioChunk(new byte[4080]);
                now += 2000;
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 0, $"expected the byte gate to reject 4080 bytes, got {sent.Count} frames");

                // The clock does not move again: 4080 + 4080 = 8160 bytes clears the
                // 4800-byte floor, so this must commit immediately. Had the rejection
                // above stamped the timestamp, nothing could commit for another 1.2s.
                strategy.EncodeAudioChunk(new byte[4080]);
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 1, $"expected the next qualifying chunk to commit immediately, got {sent.Count} frames");
            });

            Run("OpenAI: a stop right after a periodic commit drops the sub-100ms tail", () =>
            {
                // The bug this whole change exists to kill, reproduced end to end on a
                // SINGLE session rather than in two isolated halves.
                //
                // Every other case above drives EITHER the periodic path OR the stop
                // sequence, so both stay green even if the periodic path stopped zeroing
                // the counter - and then a real session that periodically commits 250ms
                // and then captures one 85ms buffer before the user releases the key
                // would still have 12000 + 4080 bytes pending at stop, clear the gate on
                // audio the server already has, and emit a commit covering 85ms. That is
                // exactly the rejected frame of HYPERWHISPER-S8/S9. The periodic commit
                // must CONSUME its bytes, not merely observe them.
                long now = 0;
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI, nowMs: () => now);
                var sent = new List<byte[]>();
                Func<byte[], WebSocketMessageType, CancellationToken, Task> send =
                    (data, type, ct) => { sent.Add(data); return Task.CompletedTask; };

                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                strategy.EncodeAudioChunk(new byte[12000]);
                now += 2000;
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 1, $"expected the periodic commit to fire, got {sent.Count} frames");

                // The tail captured after that commit, and nothing more.
                strategy.EncodeAudioChunk(new byte[4080]);
                var steps = strategy.GetStopSequence();

                Assert(steps.Count == 2, $"expected the stop sequence to drop the 4080-byte tail, got {steps.Count} steps");
                Assert(steps[0].Action == StreamingStopAction.Wait, $"expected the sequence to still wait, got {steps[0].Action}");
                Assert(steps[1].Action == StreamingStopAction.Close, $"expected the sequence to still close, got {steps[1].Action}");
            });

            Run("OpenAI: a stop after a periodic commit still commits a tail over the minimum", () =>
            {
                // The other half of the same composition: consuming the bytes at the
                // periodic commit must not make the stop sequence permanently silent. A
                // tail that clears the floor on its own still has to be committed.
                long now = 0;
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.OpenAI, nowMs: () => now);
                var sent = new List<byte[]>();
                Func<byte[], WebSocketMessageType, CancellationToken, Task> send =
                    (data, type, ct) => { sent.Add(data); return Task.CompletedTask; };

                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                strategy.EncodeAudioChunk(new byte[12000]);
                now += 2000;
                strategy.OnAudioSendOpportunityAsync(send, CancellationToken.None).GetAwaiter().GetResult();

                Assert(sent.Count == 1, $"expected the periodic commit to fire, got {sent.Count} frames");

                strategy.EncodeAudioChunk(new byte[12000]);
                var steps = strategy.GetStopSequence();

                Assert(steps.Count == 3, $"expected the stop sequence to commit a 12000-byte tail, got {steps.Count} steps");
                Assert(steps[0].Action == StreamingStopAction.SendMessage, $"expected a SendMessage step first, got {steps[0].Action}");
                Assert(steps[0].Payload != null, "expected the commit step to carry a payload");
                var tailFrame = System.Text.Encoding.UTF8.GetString(steps[0].Payload!);
                Assert(tailFrame.Contains("input_audio_buffer.commit"), $"expected the commit frame, got '{tailFrame}'");
            });

            Run("TranscriptViewModel.ApplyUpdate never reverts the entity it wraps", () =>
            {
                // Reproduces the History clobber: MainViewModel creates a Processing
                // transcript, HistoryViewModel wraps that exact instance, then
                // MainViewModel completes the SAME instance and hands it back through
                // HistoryService.TranscriptUpdated. Absorbing the update must not
                // revert the entity to the snapshot taken at construction — the
                // transcription flow's finally-block safety net reads that status and
                // would overwrite a completed transcript with a failure.
                var transcript = new Transcript
                {
                    Id = Guid.NewGuid(),
                    Status = TranscriptStatus.Processing,
                    Text = "Processing audio..."
                };

                var vm = new TranscriptViewModel(transcript);

                transcript.Text = "the real transcription";
                transcript.TranscribedText = "the real transcription";
                transcript.Status = TranscriptStatus.Completed;
                transcript.TranscriptionProvider = "Whisper large-v3-turbo";

                vm.ApplyUpdate(transcript);

                Assert(transcript.Status == TranscriptStatus.Completed,
                    $"entity status was reverted to {transcript.Status}");
                Assert(transcript.Text == "the real transcription",
                    $"entity text was reverted to '{transcript.Text}'");
                Assert(transcript.TranscribedText == "the real transcription",
                    "entity raw text was discarded");
                Assert(transcript.TranscriptionProvider == "Whisper large-v3-turbo",
                    "entity provider was discarded");

                Assert(vm.Status == TranscriptStatus.Completed,
                    $"view model still shows {vm.Status}");
                Assert(vm.Text == "the real transcription",
                    $"view model still shows '{vm.Text}'");

                // A distinct instance (e.g. re-read from the DB) must still land.
                var reread = new Transcript
                {
                    Id = transcript.Id,
                    Status = TranscriptStatus.Failed,
                    Text = "No speech detected",
                    FailedReason = "No speech detected",
                    RetryCount = 2
                };

                vm.ApplyUpdate(reread);

                Assert(vm.Status == TranscriptStatus.Failed, $"view model shows {vm.Status}");
                Assert(vm.Text == "No speech detected", $"view model shows '{vm.Text}'");
                Assert(vm.RetryCount == 2, $"view model shows retry count {vm.RetryCount}");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript applies filler-word removal to confirmed deltas, gated by _config.RemoveFillerWords (mirrors TranscriptionOrchestrator's batch order)", () =>
            {
                // Issue #94: "Remove filler words" stripped fillers from batch
                // transcription but was silently ignored in streaming. This pins the
                // fix's wiring in AppendFinalTranscript — the only place confirmed/
                // final streaming deltas are processed (never PartialTranscript/interim).
                //
                // RemoveFillerWords now flows through the immutable StreamingSessionConfig
                // (built once by StreamingTranscriptionSessionFactory.Create), like every
                // other per-session setting on this client — so exercising "on" vs "off"
                // is just two configs, no global-singleton save/mutate/restore needed.
                const string raw = "I uh think this is, um, correct";
                var baseConfig = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "en",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var enabledClient = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), baseConfig);
                // Literal, not recomputed from the same helper: the point is to pin
                // what the shared core produces, not that it equals itself. Verified
                // identical to the retired C# regex (issue #278).
                const string expectedEnabled = "I think this is, correct";
                var actualEnabled = enabledClient.AppendFinalTranscript(raw);
                Assert(actualEnabled == expectedEnabled,
                    $"expected filler words stripped ('{expectedEnabled}'), got '{actualEnabled}'");
                Assert(actualEnabled != null && !actualEnabled.Contains("uh") && !actualEnabled.Contains("um"),
                    $"filler words were not removed from '{actualEnabled}'");

                var disabledConfig = baseConfig with { RemoveFillerWords = false };
                var disabledClient = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), disabledConfig);
                var expectedDisabled = TranscriptionTextProcessing.ProcessVoiceCommands(raw).Trim();
                var actualDisabled = disabledClient.AppendFinalTranscript(raw);
                Assert(actualDisabled == expectedDisabled,
                    $"expected filler words preserved when the setting is off ('{expectedDisabled}'), got '{actualDisabled}'");
                Assert(actualDisabled != null && actualDisabled.Contains("uh") && actualDisabled.Contains("um"),
                    $"filler words should be preserved when RemoveFillerWords is disabled, got '{actualDisabled}'");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript passes the session language, so a German stream keeps its real words", () =>
            {
                // Filler removal is English-only. AppendFinalTranscript hands the
                // session's language to the shared core, which no-ops outside en/en-*,
                // so a German stream keeps its real words ("er" = he, "um" = at) even
                // with the setting on. This used to need a bespoke IsEnglishLanguage
                // gate on the Windows side, because the local regex took no language
                // at all (issue #278).
                const string german = "ich denke er ist groß";
                var germanConfig = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "de",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), germanConfig);
                var actual = client.AppendFinalTranscript(german);
                Assert(actual == german,
                    $"expected German real words preserved ('{german}'), got '{actual}'");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript strips a confirmed delta that is entirely a filler word", () =>
            {
                // A confirmed segment that is JUST "uh"/"um"/"er" (already trimmed, no
                // surrounding whitespace) must be stripped down to empty, same as a filler
                // appearing mid-sentence — regression coverage for the boundary case where
                // the old regex required whitespace on at least one side to match.
                var config = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "en",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                var actual = client.AppendFinalTranscript("uh");
                Assert(actual == null,
                    $"expected a filler-only delta to be stripped down to nothing, got '{actual}'");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript only recapitalizes the leading word for the session's first confirmed delta", () =>
            {
                // A leading filler on the FIRST confirmed delta of a session is a real
                // sentence opener, so the shared core's remove_filler_words correctly
                // recapitalizes the word after it. But that same recapitalization is wrong for a LATER
                // delta - "um, this works" following an earlier confirmed "I think" is
                // mid-transcript, not a new sentence, and must not become "I think This works".
                var config = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "en",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);

                var firstDelta = client.AppendFinalTranscript("um, the cat sat down");
                Assert(firstDelta == "The cat sat down",
                    $"expected the session's first delta to recapitalize after its leading filler, got '{firstDelta}'");

                var secondDelta = client.AppendFinalTranscript("um, this works");
                Assert(secondDelta == "this works",
                    $"expected a later delta's leading word to stay lowercase (mid-transcript), got '{secondDelta}'");
            });

            Run("TranscriptionTextProcessing routes spacing, filler words and autocapitalize through the shared core", () =>
            {
                // Issue #278: Windows kept private copies of these four transforms in
                // Utilities/SmartSpacing.cs and Utilities/AutocapitalizeInsert.cs, and
                // they had drifted from macOS. Each assertion below is one of the
                // observed drifts, so this fails if Windows ever reimplements them.

                // Drift 1: fillers were stripped in EVERY language. English only now.
                Assert(TranscriptionTextProcessing.RemoveFillerWords("I uh think this is, um, correct", "en")
                        == "I think this is, correct",
                    "English fillers must still be stripped");
                Assert(TranscriptionTextProcessing.RemoveFillerWords("ich denke er ist groß", "de")
                        == "ich denke er ist groß",
                    "German real words must survive filler removal");
                Assert(TranscriptionTextProcessing.RemoveFillerWords("I uh think", "auto")
                        == "I uh think",
                    "an auto-detect language must leave fillers alone");
                Assert(TranscriptionTextProcessing.RemoveFillerWords("I uh think", null)
                        == "I uh think",
                    "an unset language must leave fillers alone");
                // Adjacent fillers: the core replaces to a fixpoint, the retired C#
                // regex used lookaround. Same result.
                Assert(TranscriptionTextProcessing.RemoveFillerWords("uh um I think", "en") == "I think",
                    "adjacent fillers must both be removed");

                // Drift 2: the pronoun "I" was lowercased mid-sentence.
                Assert(TranscriptionTextProcessing.ApplyAutocapitalize("I think", TextFieldContext.MidSentence)
                        == "I think",
                    "the pronoun 'I' must not be lowercased mid-sentence");
                Assert(TranscriptionTextProcessing.ApplyAutocapitalize("Hello", TextFieldContext.MidSentence)
                        == "hello",
                    "an ordinary word must still be lowercased mid-sentence");
                Assert(TranscriptionTextProcessing.ApplyAutocapitalize("API documentation", TextFieldContext.MidSentence)
                        == "API documentation",
                    "an acronym must be left alone");
                Assert(TranscriptionTextProcessing.ApplyAutocapitalize("Hello", TextFieldContext.StartOfSentence)
                        == "Hello",
                    "start-of-sentence must be a pass-through");

                // Drift 3: the language table is case-insensitive, and a missing
                // language means auto-detect. Both are hw-text fixes this change
                // depends on; assert Windows really gets them.
                Assert(TranscriptionTextProcessing.AppendTrailingSpace("今日はいい天気ですね。", "JA")
                        == "今日はいい天気ですね。",
                    "an upper-case CJK language code must not gain a trailing space");
                Assert(TranscriptionTextProcessing.AppendTrailingSpace("今日はいい天気ですね。", null)
                        == "今日はいい天気ですね。",
                    "a null language must fall back to CJK detection");
                Assert(TranscriptionTextProcessing.AppendTrailingSpace("Hello world.", "en")
                        == "Hello world. ",
                    "a space-delimited language must still gain a trailing space");

                // Drift 4: the CJK range table was half the size and iterated UTF-16
                // code units, so a supplementary-plane ideograph counted as two
                // non-CJK chars. U+20000 is CJK Extension B.
                Assert(TranscriptionTextProcessing.AppendTrailingSpace("\U00020000\U00020001", "auto")
                        == "\U00020000\U00020001",
                    "supplementary-plane ideographs must count as CJK");

                Assert(TranscriptionTextProcessing.RemoveTrailingPeriod("Hello world.") == "Hello world",
                    "a single trailing period must be removed");
                Assert(TranscriptionTextProcessing.RemoveTrailingPeriod("Wait...") == "Wait...",
                    "an ellipsis must be preserved");
            });

            Run("Deepgram parses Results (object channel) into a FinalTranscript", () =>
            {
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.Deepgram);
                var evt = strategy.ParseMessage(
                    "{\"type\":\"Results\",\"channel\":{\"alternatives\":[{\"transcript\":\"hello\"}]},\"is_final\":true}");

                var final = evt as StreamingProviderEvent.FinalTranscript;
                Assert(final != null, $"expected FinalTranscript, got {evt?.GetType().Name ?? "null"}");
                Assert(final!.Text == "hello", $"expected text 'hello', got '{final.Text}'");
            });

            Run("Deepgram parses SpeechStarted (array channel) without throwing — issue #106", () =>
            {
                // Deepgram overloads "channel": an object on Results frames, an array of channel
                // indices on SpeechStarted/UtteranceEnd frames. Before the fix, deserializing the
                // array shape into a typed channel object threw and was swallowed by the outer
                // try/catch, so this event never reached the caller. The shared core reads the
                // frame through an untyped JSON value, which makes the polymorphism a non-event —
                // but this is a shipped regression and keeps a Windows-side assertion.
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.Deepgram);
                var evt = strategy.ParseMessage("{\"type\":\"SpeechStarted\",\"channel\":[0,1],\"timestamp\":1.2}");

                Assert(evt is StreamingProviderEvent.Metadata,
                    $"expected Metadata, got {evt?.GetType().Name ?? "null"}");
            });

            Run("Deepgram parses UtteranceEnd (array channel) without throwing — issue #106", () =>
            {
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.Deepgram);
                var evt = strategy.ParseMessage("{\"type\":\"UtteranceEnd\",\"channel\":[0,1],\"last_word_end\":2.5}");

                Assert(evt is StreamingProviderEvent.Metadata,
                    $"expected Metadata, got {evt?.GetType().Name ?? "null"}");
            });

            Run("InlineHtml turns release-note <b> into a bold run instead of literal markup", () =>
            {
                var runs = InlineHtml.Parse("<b>New models</b> &mdash; OpenAI gpt-transcribe and more.");

                Assert(runs.Count == 2, $"expected 2 runs, got {runs.Count}");
                Assert(runs[0] == new HtmlRun("New models", Bold: true, Italic: false),
                    $"expected a bold lead-in, got '{runs[0]}'");
                Assert(runs[1] == new HtmlRun(" — OpenAI gpt-transcribe and more.", Bold: false, Italic: false),
                    $"expected the decoded remainder, got '{runs[1]}'");
                Assert(!InlineHtml.PlainText("<b>x</b>").Contains('<'), "markup leaked into plain text");
            });

            Run("InlineHtml keeps text from tags it does not support and leaves unknown entities literal", () =>
            {
                Assert(InlineHtml.PlainText("<span class=\"x\">kept</span>") == "kept",
                    "unsupported tag should be dropped but its text kept");
                Assert(InlineHtml.PlainText("2 < 3") == "2 < 3",
                    "an unterminated tag should be treated as text");
                Assert(InlineHtml.PlainText("&bogus; stays") == "&bogus; stays",
                    "an unknown entity should stay literal");
                Assert(InlineHtml.PlainText("&lt;b&gt;escaped&lt;/b&gt;") == "<b>escaped</b>",
                    "escaped markup must not be re-parsed as a tag");
            });

            Run("InlineHtml turns <a href> into a linked run", () =>
            {
                var runs = InlineHtml.Parse("See the <a href=\"https://example.com/latency\">latency page</a> now.");

                Assert(runs.Count == 3, $"expected 3 runs, got {runs.Count}");
                Assert(runs[1].Text == "latency page" && !runs[1].Bold && !runs[1].Italic
                        && runs[1].Link?.AbsoluteUri == "https://example.com/latency",
                    $"expected a linked label, got '{runs[1]}'");
                Assert(runs[0].Link is null && runs[2].Link is null,
                    "text outside the anchor should not be linked");

                // Uri normalization: a bare authority gains the empty path's slash.
                var bold = InlineHtml.Parse("<a href=\"https://example.com\"><b>bold link</b></a>");
                Assert(bold.Count == 1 && bold[0].Bold && bold[0].Link?.AbsoluteUri == "https://example.com/",
                    "emphasis inside a link should keep both the style and the destination");

                var quoting = new[]
                {
                    "<A HREF='https://example.com/a' class=\"x\">x</A>",
                    "<a class=\"x\" href=https://example.com/a>x</a>",
                    "<a href = \"https://example.com/a\">x</a>"
                };
                foreach (var html in quoting)
                {
                    Assert(InlineHtml.Parse(html)[0].Link?.AbsoluteUri == "https://example.com/a",
                        $"href not read from '{html}'");
                }

                Assert(InlineHtml.Parse("<a href=\"https://example.com/p?a=1&amp;b=2\">x</a>")[0].Link?.AbsoluteUri
                        == "https://example.com/p?a=1&b=2",
                    "escaped query separators should survive in the destination");
            });

            Run("InlineHtml links only web and mail schemes", () =>
            {
                var hostile = new[]
                {
                    "<a href=\"javascript:alert(1)\">x</a>",
                    "<a href=\"data:text/html,<b>x</b>\">x</a>",
                    "<a href=\"file:///etc/passwd\">x</a>",
                    "<a href=\"/relative/path\">x</a>",
                    "<a data-href=\"https://example.com\">x</a>",
                    "<a>x</a>"
                };

                foreach (var html in hostile)
                {
                    Assert(InlineHtml.Parse(html).TrueForAll(run => run.Link is null),
                        $"'{html}' should not produce a link");
                    // Exactly the label, nothing else: the data: case used to leak
                    // '">x' into the visible text because the tag scan cut at the
                    // '>' inside the quoted href.
                    Assert(InlineHtml.PlainText(html) == "x",
                        $"'{html}' should render as its label alone, got '{InlineHtml.PlainText(html)}'");
                }

                Assert(InlineHtml.Parse("<a href=\"mailto:hi@example.com\">mail</a>")[0].Link?.AbsoluteUri
                        == "mailto:hi@example.com",
                    "mailto should stay clickable");

                // An unusable href must not leak onto the text that follows it.
                var after = InlineHtml.Parse("<a href=\"javascript:x\">label</a> after");
                Assert(after.TrueForAll(run => run.Link is null), "link leaked past the anchor");
            });

            Run("InlineHtml reads href only in attribute-name position", () =>
            {
                // "preceded by whitespace" is also true inside a quoted value, so a
                // title carrying "href=..." used to win over the real attribute.
                Assert(InlineHtml.Parse(
                            "<a title=\"see href=http://evil.example more\" href=\"https://real.example\">Label</a>")[0]
                        .Link?.AbsoluteUri == "https://real.example/",
                    "an href inside another attribute's value must not win");
                Assert(InlineHtml.Parse("<a title=\"use href=1\" href=\"https://real\">x</a>")[0]
                        .Link?.AbsoluteUri == "https://real/",
                    "the real href must still be found after a shadowing value");
                Assert(InlineHtml.Parse("<a data-href=\"https://evil.example\" href=\"https://real.example\">x</a>")[0]
                        .Link?.AbsoluteUri == "https://real.example/",
                    "a longer attribute name ending in 'href' must not match");
            });

            Run("InlineHtml keeps a quoted '>' inside the tag, and invents nothing on an unterminated quote", () =>
            {
                // A '>' in a query string used to truncate the tag, linking half the
                // URL and spilling the rest of the markup into the visible text.
                const string quotedAngle = "<li>Read <a href=\"https://ex.com/?q=a>b\" title=\"t\">here</a></li>";
                var runs = InlineHtml.Parse(quotedAngle);
                Assert(InlineHtml.PlainText(quotedAngle) == "Read here",
                    $"markup leaked into the text: '{InlineHtml.PlainText(quotedAngle)}'");
                Assert(runs.Count == 2 && runs[1].Text == "here",
                    $"expected the label alone in the linked run, got {runs.Count} runs");

                // An href whose quote is never closed is not a destination.
                const string unterminated = "<a href=\"https://example.com>label</a> after";
                Assert(InlineHtml.Parse(unterminated).TrueForAll(run => run.Link is null),
                    "an unterminated quote should yield no link at all");
                Assert(InlineHtml.PlainText(unterminated) == "label after",
                    $"got '{InlineHtml.PlainText(unterminated)}'");
            });

            Run("InlineHtml does not leave a self-closing or unclosed <a> on the link stack", () =>
            {
                // <a …/> pushed an entry nothing ever popped, so every remaining
                // word in the note rendered as part of the link.
                Assert(InlineHtml.Parse("Before <a href=\"https://x.example\"/> after and more")
                        .TrueForAll(run => run.Link is null),
                    "a self-closing anchor must not link the text that follows it");

                var selfClosed = InlineHtml.Parse("<a href=\"https://x.example\"/>after");
                Assert(selfClosed.Count == 1 && selfClosed[0] == new HtmlRun("after", Bold: false, Italic: false),
                    $"expected one unlinked run, got '{string.Join(", ", selfClosed)}'");

                // An <a> nobody closes is bounded by the end of the fragment.
                var unclosed = InlineHtml.Parse("<a href=\"https://x.example\">unclosed");
                Assert(unclosed.Count == 1 && unclosed[0].Text == "unclosed"
                        && unclosed[0].Link?.AbsoluteUri == "https://x.example/",
                    "an unclosed anchor should still link its own label");
            });

            Run("InlineHtml treats a '/' at the end of a bare href as part of the URL", () =>
            {
                // Deciding "self-closing" from the raw tag's last character read
                // every bare href ending in '/' — most URLs — as "<a …/>", and
                // silently dropped the link.
                var bare = InlineHtml.Parse("<a href=https://example.com/>Home</a>");
                Assert(bare.Count == 1 && bare[0].Text == "Home"
                        && bare[0].Link?.AbsoluteUri == "https://example.com/",
                    $"a bare href ending in '/' lost its link: '{string.Join(", ", bare)}'");

                var quoted = InlineHtml.Parse("<a href=\"https://example.com/\">Home</a>");
                Assert(quoted.Count == 1 && quoted[0].Text == "Home"
                        && quoted[0].Link?.AbsoluteUri == "https://example.com/",
                    $"a quoted href ending in '/' lost its link: '{string.Join(", ", quoted)}'");

                // A '/' of the tag's own still closes it, even after a bare href
                // that ends in one.
                const string closed = "<a href=https://example.com/ />Home and the rest";
                Assert(InlineHtml.Parse(closed).TrueForAll(run => run.Link is null),
                    "a genuinely self-closing anchor must link nothing");
                Assert(InlineHtml.PlainText(closed) == "Home and the rest",
                    $"got '{InlineHtml.PlainText(closed)}'");

                // A '/' that is not the last thing in the tag is not the tag's.
                Assert(InlineHtml.Parse("<a / href=https://example.com/>L</a>")[0].Link?.AbsoluteUri
                        == "https://example.com/",
                    "a '/' before a real attribute must not close the tag");

                foreach (var lineBreak in new[] { "a<br>b", "a<br/>b", "a<br />b" })
                {
                    Assert(InlineHtml.PlainText(lineBreak) == "a\nb",
                        $"'{lineBreak}' should be a line break, got '{InlineHtml.PlainText(lineBreak)}'");
                }
            });

            Run("InlineHtml gives a nested <a> the innermost destination, not the outer one", () =>
            {
                // The inner href is rejected, so its label must lose the link
                // rather than inherit the outer anchor's destination.
                var runs = InlineHtml.Parse(
                    "<a href=\"https://ok.example\">read <a href=\"javascript:x\">this</a></a>");

                Assert(runs.Count == 2, $"expected 2 runs, got {runs.Count}");
                Assert(runs[0].Text == "read " && runs[0].Link?.AbsoluteUri == "https://ok.example/",
                    $"outer label lost its link: '{runs[0]}'");
                Assert(runs[1].Text == "this" && runs[1].Link is null,
                    $"inner label inherited the outer destination: '{runs[1]}'");
            });

            Run("InlineHtml leaves the space on either side of a link outside the link", () =>
            {
                // A space inside the linked run is underlined, tinted and
                // clickable — it belongs outside the anchor, on whichever side
                // of it the space was written.
                const string opening = "<b>See</b> <a href=\"https://example.com\">here</a>";
                var runs = InlineHtml.Parse(opening);

                Assert(InlineHtml.PlainText(opening) == "See here",
                    "the space between the two runs was lost");
                Assert(runs.TrueForAll(run => run.Link is null || !run.Text.StartsWith(' ')),
                    $"the space was swallowed into the link run: '{string.Join(", ", runs)}'");

                // The closing side: the space in front of </a> used to be emitted
                // as its own run still carrying the link — an extra Hyperlink with
                // a hand cursor and a tooltip, for one blank character.
                const string closing = "See <a href=\"https://x.example\"><b>the page</b> </a>now";
                var closingRuns = InlineHtml.Parse(closing);

                Assert(InlineHtml.PlainText(closing) == "See the page now",
                    $"got '{InlineHtml.PlainText(closing)}'");
                Assert(closingRuns.TrueForAll(run => run.Link is null || run.Text.Trim().Length > 0),
                    $"a blank run still carries the link: '{string.Join(", ", closingRuns)}'");
            });

            Run("InlineHtml does not style the rest of the note after a self-closing <b/> or <i/>", () =>
            {
                // Self-closing was consulted for <a> only, so "<b/>" pushed a depth
                // nothing ever popped and emboldened everything after it.
                Assert(InlineHtml.Parse("before <b/> after").TrueForAll(run => !run.Bold),
                    $"'<b/>' bolded the rest: '{string.Join(", ", InlineHtml.Parse("before <b/> after"))}'");
                // Each TrueForAll is paired with the text it should have
                // produced. TrueForAll is vacuously true on an empty or
                // truncated list, so on its own it also passes for a parser
                // that aborted on the self-closing tag and lost the rest.
                Assert(InlineHtml.PlainText("before <b/> after") == "before after",
                    $"'<b/>' lost text: '{InlineHtml.PlainText("before <b/> after")}'");
                Assert(InlineHtml.Parse("before <i/> after").TrueForAll(run => !run.Italic),
                    $"'<i/>' italicised the rest: '{string.Join(", ", InlineHtml.Parse("before <i/> after"))}'");
                Assert(InlineHtml.PlainText("before <i/> after") == "before after",
                    $"'<i/>' lost text: '{InlineHtml.PlainText("before <i/> after")}'");
                Assert(InlineHtml.Parse("x<strong />y").TrueForAll(run => !run.Bold),
                    "'<strong />' bolded the rest");
                Assert(InlineHtml.PlainText("x<strong />y") == "xy",
                    $"'<strong />' lost text: '{InlineHtml.PlainText("x<strong />y")}'");
                Assert(InlineHtml.Parse("x<em />y").TrueForAll(run => !run.Italic),
                    "'<em />' italicised the rest");
                Assert(InlineHtml.PlainText("x<em />y") == "xy",
                    $"'<em />' lost text: '{InlineHtml.PlainText("x<em />y")}'");

                // The paired forms still style what they wrap.
                Assert(InlineHtml.Parse("<b>still bold</b>")[0].Bold, "a real <b> stopped working");
            });

            Run("InlineHtml reads an apostrophe in a bare value as an ordinary character", () =>
            {
                // Skipping quoted values in the tag-end scan entered quote mode on
                // any apostrophe, so the one in a bare "href=it's" paired up with
                // the next one in the text and swallowed everything between them.
                const string html = "<a href=it's>label</a> and <b>Ray's</b> note";
                Assert(InlineHtml.PlainText(html) == "label and Ray's note",
                    $"text between two apostrophes was eaten: '{InlineHtml.PlainText(html)}'");
                Assert(InlineHtml.PlainText("<b>Ray's</b> and <i>don't</i>") == "Ray's and don't",
                    $"got '{InlineHtml.PlainText("<b>Ray's</b> and <i>don't</i>")}'");

                // A quote in value position — with or without whitespace after the
                // '=' — still shields a '>' sitting inside the value.
                Assert(InlineHtml.PlainText("<a href=\"https://ex.com/?q=a>b\" title=\"t\">here</a>") == "here",
                    "a '>' inside a quoted value truncated the tag");
                Assert(InlineHtml.PlainText("<a href = 'https://ex.com/?q=a>b'>x</a>") == "x",
                    "a '>' inside a spaced-out quoted value truncated the tag");
            });

            Run("InlineHtml keeps the feed's href verbatim, decoding entities only", () =>
            {
                // The href used to be run through the whole parser to decode
                // "&amp;", which also stripped tags and comments out of it — a
                // valid, allow-listed, but different destination.
                var markup = InlineHtml.Parse("<a href=\"https://ex.com/?q=<b>x</b>\">Docs</a>");
                Assert(markup[0].Link?.AbsoluteUri != "https://ex.com/?q=x",
                    "markup was stripped out of the destination");
                Assert(Uri.UnescapeDataString(markup[0].Link?.AbsoluteUri ?? "") == "https://ex.com/?q=<b>x</b>",
                    $"destination not preserved verbatim: '{markup[0].Link?.AbsoluteUri}'");
                Assert(InlineHtml.PlainText("<a href=\"https://ex.com/?q=<b>x</b>\">Docs</a>") == "Docs",
                    "the label changed");

                var commented = InlineHtml.Parse("<a href=\"https://ex.com/<!-- c -->path\">Docs</a>");
                Assert(commented[0].Link?.AbsoluteUri != "https://ex.com/path",
                    "a comment was stripped out of the destination");

                // Entities in the href must still decode: feeds escape query
                // separators, and "?a=1&amp;b=2" is not a URL until they do.
                Assert(InlineHtml.Parse("<a href=\"https://ex.com/p?a=1&amp;b=2\">x</a>")[0].Link?.AbsoluteUri
                        == "https://ex.com/p?a=1&b=2", "&amp; stopped decoding in the href");
                Assert(InlineHtml.Parse("<a href=\"https://ex.com/p?a=1&#38;b=2\">x</a>")[0].Link?.AbsoluteUri
                        == "https://ex.com/p?a=1&b=2", "&#38; stopped decoding in the href");
            });

            Run("InlineHtml does not pair an unclosed value with a later tag's quote", () =>
            {
                // Skipping to the matching quote searched the whole fragment, so
                // a value never closed inside its own tag paired up with the
                // quote of a later one and everything between them — the label,
                // its </a>, the next tag — was swallowed as one tag body.
                const string html = "Read <a href=\"https://ex.com/latency>the page</a> for <b class=\"hl\">details</b>.";
                var runs = InlineHtml.Parse(html);

                Assert(InlineHtml.PlainText(html) == "Read the page for details.",
                    $"text was swallowed: '{InlineHtml.PlainText(html)}'");
                Assert(runs.TrueForAll(run => run.Link is null),
                    $"an unterminated quote invented a destination: '{string.Join(", ", runs)}'");
                Assert(runs.Exists(run => run.Text == "details" && run.Bold),
                    $"the <b> after it never opened: '{string.Join(", ", runs)}'");
                Assert(runs.TrueForAll(run => !run.Text.Contains('<') && !run.Text.Contains("href")),
                    $"markup leaked into the text: '{string.Join(", ", runs)}'");

                // A '>' inside a value that *is* closed still does not end the
                // tag, and an apostrophe in a bare value is still ordinary text.
                Assert(InlineHtml.PlainText("<a href=\"https://ex.com/?q=a>b\" title=\"t\">here</a>") == "here",
                    "a '>' inside a quoted value truncated the tag");
                Assert(InlineHtml.PlainText("<a href=it's>label</a> and <b>Ray's</b> note")
                        == "label and Ray's note",
                    "an apostrophe in a bare value ate the text after it");
            });

            Run("InlineHtml pops on a closing tag that also closes itself", () =>
            {
                // parseTag sets both flags for "</a/>", and the self-closing
                // guard ran first, so nothing was ever popped and the rest of
                // the note stayed linked, bold or italic.
                const string anchor =
                    "<a href=\"https://hyperwhisper.app/changelog\">changelog</a/>. Also faster startup.";
                var runs = InlineHtml.Parse(anchor);

                Assert(InlineHtml.PlainText(anchor) == "changelog. Also faster startup.",
                    $"got '{InlineHtml.PlainText(anchor)}'");
                Assert(runs.Exists(run => run.Text == "changelog" && run.Link is not null),
                    $"the anchor's own label lost its link: '{string.Join(", ", runs)}'");
                Assert(runs.TrueForAll(run => run.Link is null || run.Text == "changelog"),
                    $"'</a/>' left the rest of the note linked: '{string.Join(", ", runs)}'");

                const string bold = "<b>New:</b/> dictation is faster";
                Assert(InlineHtml.Parse(bold).TrueForAll(run => !run.Bold || run.Text == "New:"),
                    $"'</b/>' left the rest of the note bold: '{string.Join(", ", InlineHtml.Parse(bold))}'");
                Assert(InlineHtml.Parse("<i>x</i/> y").TrueForAll(run => !run.Italic || run.Text == "x"),
                    "'</i/>' left the rest of the note italic");
                Assert(InlineHtml.Parse("<a href=\"https://x.example\">x</a />after")
                        .TrueForAll(run => run.Link is null || run.Text == "x"),
                    "'</a />' left the rest of the note linked");

                // The opening self-closing forms still change no state.
                Assert(InlineHtml.Parse("before <b/> after").TrueForAll(run => !run.Bold),
                    "'<b/>' bolded the rest again");
                Assert(InlineHtml.Parse("Before <a href=\"https://x.example\"/> after")
                        .TrueForAll(run => run.Link is null),
                    "'<a …/>' linked the rest again");
                Assert(InlineHtml.PlainText("a<br/>b") == "a\nb", "'<br/>' stopped breaking the line");
            });

            Run("InlineHtml writes one space around an element that produces no text", () =>
            {
                // The pending space was committed on the opening tag and then
                // re-armed from producedText, so an empty element was spelt with
                // a space on each side of nothing.
                foreach (var html in new[]
                {
                    "Read <a href=\"https://x.example\"><img src=\"badge.png\"></a> the docs",
                    "Read <a href=\"https://x.example\"></a> the docs",
                    "Read <a href=\"https://x.example\">   </a> the docs"
                })
                {
                    Assert(InlineHtml.PlainText(html) == "Read the docs",
                        $"'{html}' rendered as '{InlineHtml.PlainText(html)}'");
                }

                // Both space cases above still hold: the space in front of <a>
                // stays outside the link, and so does the one in front of </a>.
                const string opening = "<b>See</b> <a href=\"https://example.com\">here</a>";
                Assert(InlineHtml.PlainText(opening) == "See here", "the opening-side space was lost");
                Assert(InlineHtml.Parse(opening).TrueForAll(run => run.Link is null || !run.Text.StartsWith(' ')),
                    "the opening-side space was swallowed into the link");

                const string closing = "See <a href=\"https://x.example\"><b>the page</b> </a>now";
                Assert(InlineHtml.PlainText(closing) == "See the page now", "the closing-side space was lost");
                Assert(InlineHtml.Parse(closing).TrueForAll(run => run.Link is null || run.Text.Trim().Length > 0),
                    "the closing-side space is inside the link again");
            });

            Run("InlineHtml keeps the element name when its own value is malformed", () =>
            {
                // The name was recorded after the value parse, so giving up on a
                // malformed value on the first token discarded the element too.
                Assert(InlineHtml.PlainText("line one<br = >line two") == "line one\nline two",
                    $"the line break was dropped: '{InlineHtml.PlainText("line one<br = >line two")}'");
                Assert(InlineHtml.PlainText("a<br = \"unterminated>b") == "a\nb",
                    $"got '{InlineHtml.PlainText("a<br = \"unterminated>b")}'");
            });

            Run("InlineHtml collapses a CRLF and leaves a non-breaking space alone", () =>
            {
                // Whitespace is collapsed one Unicode SCALAR VALUE at a time:
                // the unit hw-releasenotes pins for every index, scan limit and
                // whitespace predicate on all three heads (#284, decision (b)).
                // A CRLF is two scalars — "\r" and "\n", each of them
                // collapsible — so it collapses like any other run of
                // whitespace and the expectations below are the same
                // everywhere.
                //
                // This file used to walk UTF-16 code units, and macOS walked
                // graphemes, where "\r\n" is one Character equal to neither
                // "\r" nor "\n" and had to be named outright to collapse at
                // all. Both are gone. A non-breaking space is not collapsible
                // whitespace on any of them.
                Assert(InlineHtml.PlainText("line one\r\nline two") == "line one line two",
                    $"got '{InlineHtml.PlainText("line one\r\nline two")}'");
                Assert(InlineHtml.PlainText("a\r\n\r\n  b") == "a b",
                    "a run of CRLFs should collapse to one space");
                Assert(InlineHtml.PlainText("a&nbsp;b") == "a\u00A0b",
                    $"a non-breaking space is not collapsible: '{InlineHtml.PlainText("a&nbsp;b")}'");
            });

            Run("InlineHtml leaves a numeric entity with a signed body literal", () =>
            {
                // The other half of decision (a) (#284), pinned in the
                // direction this head already took: "&#+65;" and "&#x+41;"
                // never decoded here — NumberStyles.None rejects a sign — and
                // decoded to "A" on macOS, where UInt32(_:radix:) accepts one.
                // The shared decoder keeps all three literal, so this cannot
                // regress into a decode.
                foreach (var literal in new[] { "&#+65;", "&#-65;", "&#x+41;" })
                {
                    Assert(InlineHtml.PlainText(literal) == literal,
                        $"'{literal}' should stay literal, got '{InlineHtml.PlainText(literal)}'");
                }

                // The well-formed spellings still decode.
                Assert(InlineHtml.PlainText("&#65;") == "A", "'&#65;' stopped decoding");
                Assert(InlineHtml.PlainText("&#x41;") == "A", "'&#x41;' stopped decoding");
            });

            Run("InlineHtml leaves a numeric entity with whitespace in its body literal", () =>
            {
                // A DELIBERATE strictness change (#284, decision (a)).
                // "&#x 41;" used to decode to "A" HERE, because
                // NumberStyles.HexNumber allows leading white, and stayed
                // literal on macOS. Nothing pinned it, no feed carries it, and
                // this is remote input — so the shared decoder now requires the
                // body after '#' (and after an 'x'/'X') to be nothing but
                // digits of the radix, on both heads. ("&# 65;" and "&#65 ;"
                // never decoded on either; they are pinned so they cannot
                // start to.)
                foreach (var literal in new[] { "&#x 41;", "&# 65;", "&#65 ;" })
                {
                    Assert(InlineHtml.PlainText(literal) == literal,
                        $"'{literal}' should stay literal, got '{InlineHtml.PlainText(literal)}'");
                }
            });

            Run("InlineHtmlText renders an anchor containing emphasis as one link", () =>
            {
                // Three runs, one destination: one Hyperlink, not three siblings
                // with three tab stops, three tooltips and three hyperlink nodes
                // announced to a screen reader. macOS renders this anchor as one
                // contiguous link too.
                const string html = "before <a href=\"https://x.com\">see <b>this</b> page</a> after";
                var runs = InlineHtml.Parse(html);
                Assert(runs.FindAll(run => run.Link is not null).Count == 3,
                    $"expected 3 linked runs, got '{string.Join(", ", runs)}'");

                var textBlock = new System.Windows.Controls.TextBlock();
                InlineHtmlText.Apply(textBlock, html);

                var inlines = textBlock.Inlines.ToList();
                Assert(inlines.Count == 3, $"expected 3 inlines, got {inlines.Count}");
                Assert(inlines[1] is System.Windows.Documents.Hyperlink,
                    $"expected the anchor to be one Hyperlink, got {inlines[1].GetType().Name}");

                var hyperlink = (System.Windows.Documents.Hyperlink)inlines[1];
                Assert(hyperlink.NavigateUri?.AbsoluteUri == "https://x.com/",
                    $"wrong destination: '{hyperlink.NavigateUri}'");
                Assert(hyperlink.Inlines.Count == 3,
                    $"expected the three runs inside one link, got {hyperlink.Inlines.Count}");
                Assert(hyperlink.Inlines.ToList()[1] is System.Windows.Documents.Run bolded
                        && bolded.FontWeight == FontWeights.Bold,
                    "emphasis inside the link was lost");
            });

            Run("InlineHtmlText.Runs renders already-parsed runs and rebuilds on rebind", () =>
            {
                // The counterpart to Source, for callers that hold runs rather
                // than a fragment. HomePage binds this so the Recent Updates
                // list renders what AppcastItem parsed once, instead of
                // re-parsing every bullet on every layout pass (#284).
                const string html = "before <a href=\"https://x.com\">see <b>this</b> page</a> after";
                var textBlock = new System.Windows.Controls.TextBlock();
                InlineHtmlText.SetRuns(textBlock, InlineHtml.Parse(html));

                // Identical rendering to the Source case above: one Hyperlink
                // across the whole anchor, not three siblings.
                var inlines = textBlock.Inlines.ToList();
                Assert(inlines.Count == 3, $"expected 3 inlines, got {inlines.Count}");
                Assert(inlines[1] is System.Windows.Documents.Hyperlink hyperlink
                        && hyperlink.NavigateUri?.AbsoluteUri == "https://x.com/"
                        && hyperlink.Inlines.Count == 3,
                    "the anchor should render as one Hyperlink holding its three runs");

                // An ItemsControl recycles containers, so a rebind must REPLACE
                // the previous item's runs rather than append to them.
                InlineHtmlText.SetRuns(textBlock, InlineHtml.Parse("just text"));
                Assert(textBlock.Inlines.Count == 1,
                    $"rebinding should rebuild the TextBlock, got {textBlock.Inlines.Count} inlines");

                InlineHtmlText.SetRuns(textBlock, null);
                Assert(textBlock.Inlines.Count == 0, "binding null should clear the TextBlock");
            });

            Run("InlineHtml.SplitBlocks replaces the update dialog's <li> walker", () =>
            {
                // UpdateAvailableWindow held the THIRD copy of the <li>
                // extractor: a "<(h[23]|li|p)[^>]*>(.*?)</\1>" walker. #284
                // collapsed all three into hw-releasenotes, keeping the most
                // forgiving reading of each disagreement.
                var blocks = InlineHtml.SplitBlocks(
                    "<h2>Title</h2><p>Intro</p><ul><li>a</li></ul><h3>More</h3><p>Outro</p>");

                Assert(blocks.Select(block => block.Kind).SequenceEqual(new[]
                    {
                        HtmlBlockKind.Heading, HtmlBlockKind.Paragraph, HtmlBlockKind.Bullet,
                        HtmlBlockKind.Heading, HtmlBlockKind.Paragraph
                    }),
                    $"wrong kinds: {string.Join(", ", blocks.Select(block => block.Kind))}");
                Assert(blocks.Select(block => RunText(block.Runs))
                        .SequenceEqual(["Title", "Intro", "a", "More", "Outro"]),
                    $"wrong text: {string.Join(" | ", blocks.Select(block => RunText(block.Runs)))}");

                // The backreference "</\1>" needed those exact characters, so a
                // feed writing "</li >" lost the bullet here while macOS kept
                // it; and "[^>]*" ended the open tag at a ">" inside a quoted
                // attribute value, leaking 'b">' into the bullet's text.
                Assert(InlineHtml.SplitBlocks("<ul><li>one</li ><li>two</li></ul>")
                        .Select(block => RunText(block.Runs)).SequenceEqual(["one", "two"]),
                    "a closing tag with whitespace before its > should still close the item");
                Assert(InlineHtml.SplitBlocks("<ul><li class=\"a>b\">kept</li></ul>")
                        .Select(block => RunText(block.Runs)).SequenceEqual(["kept"]),
                    "a > inside a quoted attribute should stay an attribute");

                // A note that is one textless block renders nothing, rather than
                // falling through and printing its own markup as text.
                Assert(InlineHtml.SplitBlocks("<p>   </p>").Count == 0,
                    "an empty block element should not trigger the line fallback");
                Assert(InlineHtml.SplitBlocks(null).Count == 0, "null should split into no blocks");
            });

            Run("InlineHtml.SplitBlocks keeps the fallback's parse-exactly-once guard", () =>
            {
                // A note with no block markup is still one block per line, split
                // on <br> and on newlines, with "-"/"*" opening a bullet.
                var blocks = InlineHtml.SplitBlocks("Heading line<br>- first\n* second\n   \n-  spaced");
                Assert(blocks.Select(block => RunText(block.Runs))
                        .SequenceEqual(["Heading line", "first", "second", "spaced"]),
                    $"wrong fallback lines: {string.Join(" | ", blocks.Select(block => RunText(block.Runs)))}");
                Assert(blocks.Select(block => block.Kind).SequenceEqual(new[]
                    {
                        HtmlBlockKind.Paragraph, HtmlBlockKind.Bullet,
                        HtmlBlockKind.Bullet, HtmlBlockKind.Bullet
                    }),
                    "a leading - or * should open a bullet and a plain line a paragraph");

                // THE GUARD. Each fallback line keeps its own markup and is
                // parsed exactly ONCE. Flattening the note to text and parsing
                // the result again decoded the entities on the first pass and
                // read the decoded result as a tag on the second — turning
                // markup the feed ESCAPED so it would show into a live link.
                var escaped = InlineHtml.SplitBlocks(
                    "Write &lt;a href=\"https://evil.example\"&gt;x&lt;/a&gt; to link.");
                Assert(escaped.Count == 1 && RunText(escaped[0].Runs)
                        == "Write <a href=\"https://evil.example\">x</a> to link.",
                    "escaped markup was re-read as markup instead of staying text: "
                        + string.Join(" | ", escaped.Select(block => RunText(block.Runs))));
                Assert(escaped.All(block => block.Runs.All(run => run.Link is null)),
                    "escaped markup was decoded and then re-read as a live link");

                // And the other half of the same guard: a REAL anchor on a
                // fallback line still reaches the renderer as a link, which the
                // flattening pass used to drop.
                var anchored = InlineHtml.SplitBlocks("see <a href=\"https://example.com/x\">the page</a>");
                Assert(anchored.SelectMany(block => block.Runs)
                        .Any(run => run.Link?.AbsoluteUri == "https://example.com/x"),
                    "a real anchor in the fallback lost its link");
            });

            Run("AppcastItem.BulletPoints are parsed runs, keeping emphasis and dropping empty items", () =>
            {
                var item = new AppcastItem
                {
                    ReleaseNotes = "<ul><li><b>Bold lead</b> — detail.</li><li class=\"x\">  </li>"
                                 + "<li>Plain bullet.</li></ul>"
                };

                // THIS ASSERTION CHANGED IN #284, and it is the only one in this
                // block that may. BulletPoints was a List<string> of raw <li>
                // inner HTML that the renderer re-parsed per bullet, per layout
                // pass; the <li> extraction and the inline parse now happen
                // once, together, in the shared core. The input and the answer
                // it stands for are unchanged.
                Assert(item.BulletPoints.Count == 2, $"expected 2 bullets, got {item.BulletPoints.Count}");
                Assert(item.BulletPoints[0].Count == 2,
                    $"expected the first bullet to split into 2 runs, got {item.BulletPoints[0].Count}");
                Assert(item.BulletPoints[0][0] == new HtmlRun("Bold lead", Bold: true, Italic: false),
                    $"expected a bold lead-in, got '{item.BulletPoints[0][0]}'");
                Assert(item.BulletPoints[0][1] == new HtmlRun(" — detail.", Bold: false, Italic: false),
                    $"expected the plain remainder, got '{item.BulletPoints[0][1]}'");
                Assert(RunText(item.BulletPoints[1]) == "Plain bullet.",
                    $"got '{RunText(item.BulletPoints[1])}'");

                // A note that opens with the list has no title: a <b> inside the
                // first bullet emphasises that bullet, it is not a heading.
                Assert(!item.HasReleaseTitle && item.ReleaseTitle.Count == 0,
                    $"a note opening with <ul> should have no title, got '{RunText(item.ReleaseTitle)}'");
            });

            Run("AppcastItem takes the first <h2> as the release title — #284 decision (c)", () =>
            {
                // The Windows feed's shape, unchanged: appcast-windows.xml opens
                // every entry with "<h2>What's New in X</h2>". All 30 live
                // entries were replayed through the shared core and produced the
                // same title and bullets this head renders today.
                var item = new AppcastItem
                {
                    ReleaseNotes = "<h2>What's New in 1.11.0</h2>\n<ul>\n"
                                 + "<li>Links are now clickable.</li>\n</ul>"
                };

                Assert(RunText(item.ReleaseTitle) == "What's New in 1.11.0",
                    $"got '{RunText(item.ReleaseTitle)}'");
                Assert(item.HasReleaseTitle, "HasReleaseTitle should be true when there is a title");
                Assert(item.BulletPoints.Select(RunText).SequenceEqual(["Links are now clickable."]),
                    $"wrong bullets: {string.Join(" | ", item.BulletPoints.Select(RunText))}");
            });

            Run("AppcastItem matches the title heading case-insensitively and with attributes", () =>
            {
                // The half of decision (c) this head did not have: the old regex
                // was "<h2>(.*?)</h2>" — case-SENSITIVE, and no attributes
                // allowed — so "<H2>" or '<h2 id="x">' showed no title at all.
                var item = new AppcastItem
                {
                    ReleaseNotes = "<H2 id=\"whats-new\">Title</H2><ul><li>x</li></ul>"
                };

                Assert(RunText(item.ReleaseTitle) == "Title", $"got '{RunText(item.ReleaseTitle)}'");
            });

            Run("AppcastItem takes only an <h2> by name, never an <h3>", () =>
            {
                // An <h2> anywhere in the note wins, which is what the old regex
                // did. An <h3> is a sub-heading in the body and never becomes a
                // title on its own — the update dialog still renders it as a
                // heading block.
                var withH3 = new AppcastItem { ReleaseNotes = "<ul><li>x</li></ul><h3>Details</h3>" };
                Assert(!withH3.HasReleaseTitle,
                    $"an <h3> should not become the title, got '{RunText(withH3.ReleaseTitle)}'");

                var withH2 = new AppcastItem { ReleaseNotes = "<ul><li>x</li></ul><h2>Late heading</h2>" };
                Assert(RunText(withH2.ReleaseTitle) == "Late heading",
                    $"got '{RunText(withH2.ReleaseTitle)}'");
            });

            Run("AppcastItem gains the pre-list title branch, with its emphasis", () =>
            {
                // The other half of decision (c), which this head gains: with no
                // <h2>, the title is the content before the first <ul> (or the
                // first <li> when there is no <ul>). That is the macOS feed's
                // shape, where the heading is a bare <b>…</b> — so the title
                // carries emphasis and ReleaseTitle is runs, not a string.
                var item = new AppcastItem
                {
                    ReleaseNotes = "<b>Enhanced Audio Recording</b>\n<ul>\n<li>Improved stability</li>\n</ul>"
                };

                Assert(RunText(item.ReleaseTitle) == "Enhanced Audio Recording",
                    $"got '{RunText(item.ReleaseTitle)}'");
                Assert(item.ReleaseTitle.All(run => run.Bold),
                    "the title's emphasis should survive — this is why it is runs and not a string");
                Assert(item.BulletPoints.Select(RunText).SequenceEqual(["Improved stability"]),
                    "the pre-list heading must not be read as a bullet");

                // A note with no notes at all has neither, and an empty <h2>
                // falls through to this branch instead of hiding a real title.
                var empty = new AppcastItem { ReleaseNotes = "" };
                Assert(!empty.HasReleaseTitle && empty.BulletPoints.Count == 0 && !empty.HasReleaseNotes,
                    "an empty note should have no title and no bullets");
                var emptyHeading = new AppcastItem
                {
                    ReleaseNotes = "<h2>  </h2>Real title<ul><li>x</li></ul>"
                };
                Assert(RunText(emptyHeading.ReleaseTitle) == "Real title",
                    $"a textless <h2> should not suppress the title, got '{RunText(emptyHeading.ReleaseTitle)}'");
            });

            Run("AppcastItem keeps a bullet whose closing tag carries whitespace", () =>
            {
                // The "</li >" disagreement, resolved macOS's way. This head's
                // "</li>" regex did not match at all, so the run of bullets
                // collapsed into one item carrying raw markup; macOS searched
                // for the prefix "</li" and closed the item. Dropping a bullet
                // the feed wrote is the worse failure, so the tokenizer wins.
                var item = new AppcastItem { ReleaseNotes = "<ul><li>one</li ><li>two</li></ul>" };

                Assert(item.BulletPoints.Select(RunText).SequenceEqual(["one", "two"]),
                    $"got: {string.Join(" | ", item.BulletPoints.Select(RunText))}");
            });

            Run("AppcastItem parses the release notes ONCE, at construction", () =>
            {
                // STRUCTURAL, and the point of the phase. ReleaseTitle and
                // BulletPoints were getters that re-ran a Regex — and then a
                // second parse per bullet in the renderer — on every read, and
                // WPF reads a bound property on every layout pass of every card
                // in the Recent Updates list. They are cached fields now, filled
                // by the ReleaseNotes init accessor.
                //
                // Reference equality is what proves it: a getter that re-parsed
                // would hand back a fresh list every time. macOS pins the same
                // property with Mirror, which reports stored properties only.
                var item = new AppcastItem { ReleaseNotes = "<b>Title</b><ul><li>one</li></ul>" };

                Assert(ReferenceEquals(item.ReleaseTitle, item.ReleaseTitle),
                    "ReleaseTitle re-parses on every read");
                Assert(ReferenceEquals(item.BulletPoints, item.BulletPoints),
                    "BulletPoints re-parses on every read");
                Assert(ReferenceEquals(item.BulletPoints[0], item.BulletPoints[0]),
                    "each bullet's runs re-parse on every read");

                // And the cached values are the parsed ones, not empty defaults.
                Assert(RunText(item.ReleaseTitle) == "Title" && item.ReleaseTitle[0].Bold,
                    $"got '{RunText(item.ReleaseTitle)}'");
                Assert(item.BulletPoints.Select(RunText).SequenceEqual(["one"]),
                    $"got: {string.Join(" | ", item.BulletPoints.Select(RunText))}");

                // An item built without ReleaseNotes never runs the accessor, so
                // the defaults must still be usable rather than null.
                var bare = new AppcastItem { Version = "1.0.0" };
                Assert(!bare.HasReleaseTitle && bare.BulletPoints.Count == 0,
                    "an item with no ReleaseNotes should have empty title and bullets");
            });

            Run("AppcastItem's cached lists are read-only for real, not just in the declared type", () =>
            {
                // Copy() shares these lists by reference with every copy, and
                // its remarks call that safe. It is only safe if the lists
                // cannot be written through: an IReadOnlyList<T> whose runtime
                // type is a List<T> downcasts, and one caller doing that would
                // mutate the note every other copy is rendering. The old
                // getters handed back a fresh list per read and could not be
                // corrupted that way, so this is a guarantee the parse-once
                // change has to replace rather than drop.
                //
                // A wrapper, not a copy — the point of the change is to parse
                // once, not to clone per read, so reference identity per read
                // (asserted above) still has to hold.
                var item = new AppcastItem { ReleaseNotes = "<b>Title</b><ul><li>one</li><li>two</li></ul>" };

                foreach (var (name, list) in new (string, object)[]
                {
                    ("ReleaseTitle", item.ReleaseTitle),
                    ("BulletPoints", item.BulletPoints),
                    ("BulletPoints[0]", item.BulletPoints[0]),
                    ("BulletPoints[1]", item.BulletPoints[1]),
                    ("a bare item's ReleaseTitle", new AppcastItem().ReleaseTitle),
                    ("a bare item's BulletPoints", new AppcastItem().BulletPoints),
                })
                {
                    Assert(list is not List<HtmlRun> && list is not List<IReadOnlyList<HtmlRun>>,
                        $"{name} downcasts to a writable List<T> ({list.GetType().Name})");
                    Assert(list is System.Collections.IList { IsReadOnly: true },
                        $"{name} is writable through IList ({list.GetType().Name})");
                }

                // The copy shares the very same lists, so the guarantee travels
                // with it rather than being re-established per copy.
                var copy = item.Copy();
                Assert(ReferenceEquals(copy.BulletPoints, item.BulletPoints),
                    "Copy() no longer shares the parsed bullets");
            });

            Run("Every cloudTierEligible catalog id has a CloudAccuracyTier case", () =>
            {
                // shared-app-classification/AGENTS.md documents catalog edits as
                // data-only, but the Provider dropdown is built straight from the
                // catalog while persistence funnels through the CloudAccuracyTier
                // enum, whose FromString fallback is DeepgramNova3. A new
                // cloudTierEligible entry with no enum case would therefore be a
                // selectable row that transcribes and bills as Deepgram. Fail here
                // instead, on the PR that adds the entry.
                var entries = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
                    .CloudTierEligibleProviders();
                Assert(entries.Count > 0, "cloud-stt-catalog.json exposed no cloudTierEligible providers");

                foreach (var entry in entries)
                {
                    var roundTripped = CloudAccuracyTierExtensions.FromString(entry.Id).ToStorageValue();
                    Assert(
                        string.Equals(roundTripped, entry.Id, StringComparison.OrdinalIgnoreCase),
                        $"catalog id '{entry.Id}' has no CloudAccuracyTier case — FromString falls back to "
                            + $"'{roundTripped}', so that Provider row would silently route and bill as Deepgram. "
                            + "Add the case to Models/CloudAccuracyTier.cs (and the macOS CloudAccuracyTier enum) "
                            + "in the same change as the catalog entry.");
                }
            });

            Run("A persisted googleChirp3 tier migrates onto geminiTranscribe", () =>
            {
                // Catalog v8 retired googleChirp3 in favour of geminiTranscribe.
                // FromString's fallback is DeepgramNova3, so every legacy spelling
                // that misses would silently move a Google user to Deepgram —
                // wrong X-STT-Provider, wrong credits, wrong vendor row.
                // The stored data is converged by the EF migration
                // 20260827090000_MigrateGoogleChirp3TierToGeminiTranscribe; this
                // guards the read path that has to hold until it runs (and for
                // any value arriving over the Local API or a backup restore).
                foreach (var legacy in new[]
                {
                    "googleChirp3", "googlechirp3", "GOOGLECHIRP3",
                    "googlespeech", "googleSpeech", "google-chirp", "googlechirp",
                    "chirp", "chirp_3",
                })
                {
                    var resolved = CloudAccuracyTierExtensions.FromString(legacy).ToStorageValue();
                    Assert(
                        resolved == "geminiTranscribe",
                        $"legacy tier '{legacy}' resolved to '{resolved}', not 'geminiTranscribe' — "
                            + "a Chirp 3 user would be silently moved to another vendor.");
                }

                // And the retired id must not come back as an enum member: the
                // canonical loop in FromString runs before the catalog aliases,
                // so a GoogleChirp3 member would win and strand the user on a
                // tier with no catalog entry.
                Assert(
                    !Enum.GetNames<CloudAccuracyTier>().Contains("GoogleChirp3"),
                    "CloudAccuracyTier.GoogleChirp3 is back; it would shadow the catalog migrateFrom alias.");
            });

            Run("Meta Muse direct provider stays separate from the cloud tier", () =>
            {
                var tier = CloudAccuracyTierExtensions.FromString("  METAMUSE  ");
                Assert(tier == CloudAccuracyTier.MetaMuse, "Meta Muse persistence did not round-trip");
                Assert(tier.ToStorageValue() == "metaMuse" && tier.ToSttProvider() == "meta",
                    "Meta Muse tier did not resolve its canonical id and provider");

                Assert((int)CloudTranscriptionProvider.Meta == 15,
                    "Meta changed its append-only persisted enum value");
                Assert(CloudTranscriptionProviderExtensions.FromIdentifier("  META  ".Trim())
                        == CloudTranscriptionProvider.Meta,
                    "Meta direct provider did not parse case-insensitively");
                Assert(CloudTranscriptionProvider.Meta.GetIdentifier() == "meta"
                        && CloudTranscriptionProvider.Meta.RequiresApiKey()
                        && CloudTranscriptionProvider.Meta.GetMaxFileSizeBytes() == 32L * 1024 * 1024,
                    "Meta direct provider metadata drifted");
                var normalizedMeta = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
                    .NormalizeCloudProvider("meta");
                Assert(normalizedMeta.Provider == "meta" && normalizedMeta.AccuracyTier == null,
                    "unshipped Meta provider storage gained migration semantics");

                var entry = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared.GetById("metaMuse");
                Assert(entry is { SttProvider: "meta", MaxFileSizeMb: 32, MaxDurationMinutes: 10 },
                    "Meta Muse catalog limits or provider changed");
                Assert(entry!.Models.Count == 1
                    && entry.Models[0].Id == "muse-voice-transcribe-1.0"
                    && !entry.Models[0].Streaming,
                    "Meta Muse batch model registry changed or became live-selectable");

                Assert(entry!.Access?.ByokEligible == true,
                    "Meta BYOK catalog gate is not enabled");
                Assert(CloudTranscriptionModels.GetById(
                        "muse-voice-transcribe-1.0", CloudTranscriptionProvider.Meta)?.Provider
                        == CloudTranscriptionProvider.Meta,
                    "Meta Muse direct model is missing");
                Assert(HyperWhisper.Services.LocalApi.Endpoints.HealthEndpoints.TranscriptionProviders
                        .Any(provider => provider == CloudTranscriptionProvider.Meta),
                    "/health does not report direct Meta key status");
                Assert(HyperWhisper.Services.LocalApi.Endpoints.HealthEndpoints.StatusString(
                        CloudTranscriptionProvider.Meta, keyPresent: true, ProviderHealth.Unknown) == "configured",
                    "/health does not distinguish a configured Meta key from an unknown probe");
                Assert(!HyperWhisper.Services.LocalApi.Endpoints.HealthEndpoints.IsReachable(
                        CloudTranscriptionProvider.Meta, keyPresent: true, ProviderHealth.Healthy),
                    "/health claims the configured-only Meta key was probed");
                Assert(ModelLibraryManager.CloudProviderAssetName(CloudTranscriptionProvider.Meta) == "providerMeta",
                    "Meta still uses another provider's brand asset");
                Assert(!MainViewModel.ExceedsMuseSourceLimit(64L * 1024 * 1024)
                        && MainViewModel.ExceedsMuseSourceLimit(64L * 1024 * 1024 + 1),
                    "Windows Meta normalization source bound drifted");

                var transient = new Mode();
                HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApplyEngineModel(
                    transient, "meta", model: null);
                Assert(transient.ProviderType == "cloud"
                        && transient.Model == "cloud"
                        && transient.CloudProvider == "meta"
                        && transient.CloudTranscriptionModel == "muse-voice-transcribe-1.0",
                    "Local API engine=meta did not select direct Muse");

                var overridden = new Mode { CloudTranscriptionModel = "stale-other-provider-model" };
                HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApplyEngineModel(
                    overridden, "meta", model: null);
                Assert(overridden.CloudTranscriptionModel == "muse-voice-transcribe-1.0",
                    "Local API engine=meta retained a baseline provider model");

                var savedOpenAi = new Mode { CloudTranscriptionModel = "gpt-4o-transcribe" };
                HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApplyEngineModel(
                    savedOpenAi, "openai", model: null);
                Assert(savedOpenAi.CloudTranscriptionModel == "gpt-4o-transcribe",
                    "Local API erased a saved provider model when model was omitted");

                var cloud = new Mode
                {
                    ProviderType = "cloud",
                    Model = "cloud",
                    CloudProvider = "hyperwhisper",
                    CloudAccuracyTier = "metaMuse",
                    CloudTranscriptionModel = "muse-voice-transcribe-1.0",
                };
                Assert(cloud.CloudProvider == "hyperwhisper" && cloud.CloudAccuracyTier == "metaMuse",
                    "the existing HyperWhisper Cloud Meta route changed");

                var caps = HyperWhisper.Services.SharedModelsCatalog.VoiceCapabilities(
                    "meta", "muse-voice-transcribe-1.0");
                Assert(caps is { CodeSwitching: true, Endpointing: true, ContextBias: true,
                    LanguageBias: true, TurnTimestamps: true, Diarization: true, WordTimestamps: false },
                    "Meta Muse shared-model capabilities drifted");
            });

            Run("Meta Muse maps malformed NAudio input to a typed format error", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"meta-malformed-{Guid.NewGuid():N}.wav");
                try
                {
                    File.WriteAllText(path, "not a wave file");
                    try
                    {
                        MetaMuseService.ValidateFinalWaveAsync(path).GetAwaiter().GetResult();
                        throw new InvalidOperationException("malformed Meta audio was accepted");
                    }
                    catch (TranscriptionException ex)
                    {
                        Assert(ex.Code == TranscriptionErrorCode.UnsupportedFormat,
                            "malformed Meta audio did not return the typed unsupported-format error");
                    }
                }
                finally
                {
                    File.Delete(path);
                }
            });

            Run("Saving a canonical imported WAV preserves the user source", () =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"hw-source-ownership-{Guid.NewGuid():N}");
                Directory.CreateDirectory(root);
                var source = Path.Combine(root, "source.wav");
                var saved = Path.Combine(root, "saved.wav");
                try
                {
                    File.WriteAllBytes(source, new byte[] { 0x52, 0x49, 0x46, 0x46 });
                    HistoryService.PersistAudioFile(source, saved, ownsSource: false);

                    Assert(File.Exists(source), "saving a canonical import moved the user's source file");
                    Assert(File.Exists(saved), "saving a canonical import did not create the history copy");
                    Assert(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(saved)),
                        "the history copy differs from the user's source file");
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });

            Run("Store-as-M4A includes canonical WAV imports only", () =>
            {
                Assert(MainViewModel.ShouldConvertImportedAudioToM4A(
                        true, "canonical.wav", "history-copy.wav"),
                    "Store-as-M4A skipped a canonical WAV import");
                Assert(!MainViewModel.ShouldConvertImportedAudioToM4A(
                        false, "canonical.wav", "history-copy.wav"),
                    "Store-as-M4A ignored the disabled setting");
                Assert(!MainViewModel.ShouldConvertImportedAudioToM4A(
                        true, "provider-native.mp3", "history-copy.wav"),
                    "Store-as-M4A treated a provider-native non-WAV as WAV");
                Assert(!MainViewModel.ShouldConvertImportedAudioToM4A(
                        true, "user-owned.wav", "user-owned.wav"),
                    "Store-as-M4A can delete a user-owned fallback source");
            });

            Run("Grok's empty model id resolves through a provider-scoped lookup", () =>
            {
                // Grok's API takes no `model` parameter, so its single registry
                // entry is stored under the empty id. The Model row is now a
                // one-item dropdown like every other provider's, so that id has
                // to resolve or the description, the price and the mode card's
                // model name all render blank.
                var grok = CloudTranscriptionModels.GetById("", CloudTranscriptionProvider.Grok);
                Assert(grok != null, "GetById(\"\", Grok) returned null — the Grok Model row would render blank");
                Assert(grok!.DisplayName == "Grok Speech-to-Text", $"got '{grok.DisplayName}'");
                Assert(!string.IsNullOrEmpty(grok.Description), "Grok entry has no description to show");

                // Unscoped, "" stays ambiguous: any provider left without a model
                // would otherwise resolve to Grok.
                Assert(CloudTranscriptionModels.GetById("") == null, "unscoped GetById(\"\") must stay null");
            });

            Run("BackupExportSettingsPage initializes under WPF", () =>
            {
                DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();

                var application = new System.Windows.Application();
                LoadApplicationResources(application);

                // Constructing the page exercises the exact construction-order NRE this
                // regression test covers. Export selection handlers must not run until
                // InitializeComponent has created the complete checkbox tree.
                var page = new BackupExportSettingsPage();
                if (!page.IsInitialized)
                    throw new InvalidOperationException("BackupExportSettingsPage did not finish WPF initialization.");

                // Post-construction changes prove handlers are attached and state is
                // recomputed; the button's default enabled value alone proves nothing.
                page.ExportSettingsCheckbox.IsChecked = false;
                page.ExportModesCheckbox.IsChecked = false;
                page.ExportVocabularyCheckbox.IsChecked = false;
                Assert(!page.ExportButton.IsEnabled,
                    "expected ExportButton to be disabled once all export sections are unchecked");

                page.ExportModesCheckbox.IsChecked = true;
                Assert(page.ExportButton.IsEnabled,
                    "expected ExportButton to be re-enabled after re-checking a section");

                application.Shutdown();
            });

            Run("VocabularyProcessor.ApplyReplacements trims even with no vocabulary configured — issue #92", () =>
            {
                DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();

                var processor = new VocabularyProcessor();
                var result = processor.ApplyReplacements("  hello world  ");

                Assert(result == "hello world", $"expected trimmed text with empty vocabulary, got '{result}'");
            });

            // Vocabulary egress normalization now lives in the shared Rust core
            // (hw-net, keyword_boost_terms). This runs through the real FFI and
            // is the Windows half of a cross-platform parity check: macOS
            // RustCoreMapping.boostVocabularyTerms and the portable
            // HyperWhisper.SharedCore.Tests assert the same rule.
            Run("StreamingTranscriptionSessionFactory.BuildVocabulary normalizes through the shared core", () =>
            {
                const StreamingTranscriptionProvider deepgram = StreamingTranscriptionProvider.Deepgram;
                Assert(StreamingTranscriptionSessionFactory.SupportsVocabulary(deepgram),
                    "Deepgram must still declare vocabulary support");

                var vocabulary = StreamingTranscriptionSessionFactory.BuildVocabulary(
                    deepgram,
                    ["  API  ", "api", "Rust<script>", "multi\n  word", "   ", ""]);
                // A TERM LIST, not a joined string: joining is a per-provider wire
                // decision and moved into the core with the rest of the protocol
                // (issue #281). First-seen casing/order, no cap here - the protocols cap.
                Assert(vocabulary is { Count: 3 },
                    $"expected 3 sanitized deduped terms, got {vocabulary?.Count.ToString() ?? "null"}");
                Assert(string.Join(", ", vocabulary!) == "API, Rustscript, multi word",
                    $"expected the sanitized deduped terms, got '{string.Join(", ", vocabulary!)}'");

                // The shared core owns "does this provider take vocabulary".
                Assert(!StreamingTranscriptionSessionFactory.SupportsVocabulary(StreamingTranscriptionProvider.OpenAI),
                    "OpenAI Realtime has no vocabulary parameter");
                Assert(StreamingTranscriptionSessionFactory.BuildVocabulary(
                    StreamingTranscriptionProvider.OpenAI, ["API"]) is null,
                    "a provider without vocabulary support must still get null");
                Assert(StreamingTranscriptionSessionFactory.BuildVocabulary(deepgram, []) is null,
                    "an empty vocabulary must still get null");
                Assert(StreamingTranscriptionSessionFactory.BuildVocabulary(deepgram, ["<>", "   "]) is null,
                    "a vocabulary that sanitizes away entirely must get null");

                // Sanitization truncates a term at the core's 80-character limit.
                var truncated = StreamingTranscriptionSessionFactory.BuildVocabulary(
                    deepgram, new[] { new string('x', 150) });
                Assert(truncated is { Count: 1 } && truncated[0].Length == 80,
                    $"expected one 80-character truncated term, got '{string.Join(", ", truncated ?? [])}'");
            });

            // The ISO 3166-1 region table now lives in the shared Rust core
            // (hw-text, EnglishSpelling::for_region). These run through the real
            // FFI, so they are the Windows half of a cross-platform parity check:
            // macOS EnglishSpellingRegionDefaultTests.swift and the portable
            // HyperWhisper.ModeDefaults.Tests assert the same codes against the
            // same table.
            Run("EnglishSpellingRegionDefault maps a region to its spelling variant", () =>
            {
                Assert(EnglishSpellingRegionDefault.ForRegion("GB") == "british",
                    "expected GB to map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("IE") == "british",
                    "expected IE to map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("NZ") == "british",
                    "expected NZ to map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("AU") == "australian",
                    "expected AU to map to australian");
                Assert(EnglishSpellingRegionDefault.ForRegion("CA") == "canadian",
                    "expected CA to map to canadian");
                Assert(EnglishSpellingRegionDefault.ForRegion("US") == "american",
                    "expected US to map to american");

                // Case and padding come from whatever the OS reports; trimming
                // and case folding are the core's, not this class's.
                Assert(EnglishSpellingRegionDefault.ForRegion(" gb ") == "british",
                    "expected a lowercase, padded code to still map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("\nca\n") == "canadian",
                    "expected a newline-padded code to still map to canadian");

                // Anything unknown or missing keeps the historical american value.
                Assert(EnglishSpellingRegionDefault.ForRegion("JP") == "american",
                    "expected an unlisted region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion(null) == "american",
                    "expected a null region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion("") == "american",
                    "expected an empty region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion("   ") == "american",
                    "expected a whitespace-only region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion("ZZ") == "american",
                    "expected an invalid region to fall back to american");
            });

            Run("The core's region table never seeds the no-spelling state", () =>
            {
                // HwEnglishSpelling.None is "emit no spelling instruction at
                // all" — the live meaning of a mode whose EnglishSpelling was
                // never chosen. It is NOT american. ForRegion is a SEEDING call
                // and must never produce it, or a brand-new mode would silently
                // ship without a <SPELLING> block. This is what makes the
                // ForRegion shim safe without an "american" fallback of its own.
                Assert(HyperwhisperCoreMethods.EnglishSpellingRawValue(HwEnglishSpelling.None).Length == 0,
                    "expected HwEnglishSpelling.None to render as the empty token, not 'american'");

                foreach (var code in new string?[] { "GB", "AU", "CA", "US", "JP", "ZZ", "", "   ", " gb ", null })
                {
                    Assert(HyperwhisperCoreMethods.EnglishSpellingForRegion(code) != HwEnglishSpelling.None,
                        $"expected region '{code ?? "<null>"}' to seed a real variant, got None");
                    Assert(EnglishSpellingRegionDefault.ForRegion(code).Length > 0,
                        $"expected region '{code ?? "<null>"}' to seed a non-empty spelling token");
                }

                // The four tokens this app stores are the four the core emits;
                // nothing but this check stops the two enums drifting apart.
                Assert(HyperwhisperCoreMethods.EnglishSpellingRawValue(HwEnglishSpelling.American) == EnglishSpellingRegionDefault.American,
                    "core token for American does not match the stored value");
                Assert(HyperwhisperCoreMethods.EnglishSpellingRawValue(HwEnglishSpelling.British) == EnglishSpellingRegionDefault.British,
                    "core token for British does not match the stored value");
                Assert(HyperwhisperCoreMethods.EnglishSpellingRawValue(HwEnglishSpelling.Australian) == EnglishSpellingRegionDefault.Australian,
                    "core token for Australian does not match the stored value");
                Assert(HyperwhisperCoreMethods.EnglishSpellingRawValue(HwEnglishSpelling.Canadian) == EnglishSpellingRegionDefault.Canadian,
                    "core token for Canadian does not match the stored value");
            });

            Run("ForCurrentRegion returns a variant the mode editor can select", () =>
            {
                var current = EnglishSpellingRegionDefault.ForCurrentRegion();
                Assert(
                    current is "american" or "british" or "australian" or "canadian",
                    $"expected a known spelling variant, got '{current}'");
            });

            Run("Seeded default modes carry the region's spelling variant", () =>
            {
                var expected = EnglishSpellingRegionDefault.ForCurrentRegion();
                var modes = ModeDefaults.GetDefaultModes();

                Assert(modes.Count > 0, "expected at least one default mode");
                foreach (var mode in modes)
                {
                    Assert(mode.EnglishSpelling == expected,
                        $"expected default mode '{mode.Name}' to seed '{expected}', got '{mode.EnglishSpelling ?? "<null>"}'");
                }
            });

            // HYPERWHISPER-SP / HYPERWHISPER-FM parity: LicenseNetworkService.LicenseVerdictReason
            // decides whether a non-200 license-validate reply is an ordinary verdict (log only) or
            // a genuine backend incident (capture to Sentry). The backend answers 400 with the SAME
            // {"valid":false,"error":"..."} shape for a lapsed license, a nonexistent key, AND a real
            // infrastructure fault, so the shape alone cannot decide this - only the server's own
            // `reason` field can. Mirrors macOS LicenseNetworkVerdictReportingTests.swift exactly.
            Run("LicenseVerdictReason recognizes reason=not_entitled at 400 as an ordinary verdict", () =>
            {
                var body = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License is revoked","reason":"not_entitled"}""");
                var reason = LicenseNetworkService.LicenseVerdictReason(400, body);
                Assert(reason == LicenseNetworkService.NotEntitledReason,
                    $"expected '{LicenseNetworkService.NotEntitledReason}', got '{reason ?? "<null>"}'");
            });

            Run("LicenseVerdictReason: reason alone (no other fields) is enough at 400", () =>
            {
                var body = System.Text.Encoding.UTF8.GetBytes("""{"reason":"not_entitled"}""");
                var reason = LicenseNetworkService.LicenseVerdictReason(400, body);
                Assert(reason == LicenseNetworkService.NotEntitledReason, $"got '{reason ?? "<null>"}'");
            });

            Run("LicenseVerdictReason: lookup_failed and bad_request are NOT an ordinary verdict", () =>
            {
                var lookupFailed = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"Failed to validate with Polar","reason":"lookup_failed"}""");
                var badRequest = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License key is required","reason":"bad_request"}""");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, lookupFailed) == "lookup_failed",
                    "expected 'lookup_failed' returned verbatim");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, badRequest) == "bad_request",
                    "expected 'bad_request' returned verbatim");
            });

            Run("LicenseVerdictReason: an unrecognised reason is returned verbatim but is not a verdict", () =>
            {
                var body = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"...","reason":"quota_exceeded"}""");
                var reason = LicenseNetworkService.LicenseVerdictReason(400, body);
                Assert(reason == "quota_exceeded", $"got '{reason ?? "<null>"}'");
                Assert(reason != LicenseNetworkService.NotEntitledReason,
                    "an unrecognised reason must never equal the not-entitled constant");
            });

            Run("LicenseVerdictReason: a body with no reason field is not a verdict (must still be captured)", () =>
            {
                var noReason = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License is revoked"}""");
                var validTrue = System.Text.Encoding.UTF8.GetBytes("""{"valid":true}""");
                var emptyObject = System.Text.Encoding.UTF8.GetBytes("{}");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, noReason) == null,
                    "expected null for a 400 body with no reason field");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, validTrue) == null,
                    "expected null for valid:true with no reason field");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, emptyObject) == null,
                    "expected null for an empty JSON object");
            });

            Run("LicenseVerdictReason: an empty body or an HTML captive-portal page is not a verdict", () =>
            {
                var html = System.Text.Encoding.UTF8.GetBytes(
                    "<!DOCTYPE html><html><body><h1>Sign in to continue</h1></body></html>");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, System.Array.Empty<byte>()) == null,
                    "expected null for an empty body");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, html) == null,
                    "expected null for an undecodable HTML body");
            });

            Run("LicenseVerdictReason: non-object JSON and a non-string reason are not a verdict", () =>
            {
                var array = System.Text.Encoding.UTF8.GetBytes("[]");
                var nonStringReason = System.Text.Encoding.UTF8.GetBytes("""{"valid":false,"reason":7}""");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, array) == null, "expected null for a JSON array");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, nonStringReason) == null,
                    "expected null for a non-string reason value");
            });

            Run("LicenseVerdictReason: only status 400 is eligible - same body at 500 is not a verdict", () =>
            {
                var verdictBody = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License is revoked","reason":"not_entitled"}""");
                Assert(LicenseNetworkService.LicenseVerdictReason(500, verdictBody) == null,
                    "expected null - a 5xx never reaches this predicate as a terminal result, and even if it did, only 400 is the documented verdict status");
                Assert(LicenseNetworkService.LicenseVerdictReason(401, verdictBody) == null, "expected null at 401");
                Assert(LicenseNetworkService.LicenseVerdictReason(200, verdictBody) == null, "expected null at 200");
            });

            RunAsync("a mid-session generationComplete keeps BOTH utterances and still sends audio_stream_end", async () =>
            {
                // Google emits serverContent.generationComplete at EVERY turn
                // boundary. Mapping it to SessionComplete completed
                // _sessionCompletedTcs at the first pause, so the stop sequence's
                // WaitForSessionComplete returned immediately, the socket closed
                // right after audio_stream_end, and the LAST utterance's final never
                // landed. Backend semantics: terminal only once the client asked to
                // stop (ws-streaming-shared.ts, the 'complete' arm).
                var config = new StreamingSessionConfig(null, null, "en", null, "AIza-test", null, false, false);
                using var strategy = LiveStrategy(StreamingTranscriptionProvider.GeminiTranscribe, config);

                Assert(!strategy.CompleteEndsSessionBeforeStop,
                    "Gemini must declare its completion frame as a turn boundary before stop");
                // Every other provider keeps the unconditional reading - a vendor
                // whose 'complete' really is once-per-session must not be changed.
                // The answer comes from the shared core (issue #281), so this walks
                // the real provider enum rather than a hand-kept strategy list.
                foreach (var provider in Enum.GetValues<StreamingTranscriptionProvider>())
                {
                    if (provider == StreamingTranscriptionProvider.GeminiTranscribe)
                    {
                        continue;
                    }

                    using var other = LiveStrategy(provider, config);
                    Assert(other.CompleteEndsSessionBeforeStop,
                        $"{other.TranscriptionProviderLabel} must keep the pre-existing terminal reading of SessionComplete");
                }

                var client = new StreamingTranscriptionClient(strategy, config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);

                var sessionCompletedCount = 0;
                client.SessionCompleted += (_, _) => sessionCompletedCount++;

                // Utterance 1, turn boundary, utterance 2 - the exact live shape
                // pinned by the backend's two-utterance relay test.
                client.HandleProviderEvent(strategy.ParseMessage(
                    "{\"serverContent\":{\"inputTranscription\":{\"text\":\"Hello, this is a test.\"}}}"));
                client.HandleProviderEvent(strategy.ParseMessage(
                    "{\"serverContent\":{\"generationComplete\":true}}"));
                client.HandleProviderEvent(strategy.ParseMessage(
                    "{\"serverContent\":{\"inputTranscription\":{\"text\":\"Let us meet on Wednesday.\"}}}"));

                Assert(client.State == StreamingConnectionState.Streaming,
                    $"the session must stay open across a turn boundary, State was {client.State}");
                Assert(sessionCompletedCount == 0,
                    "a mid-session turn boundary must not raise SessionCompleted - subscribers treat that as end-of-session");

                // The stop sequence still runs in full. No socket is connected, so
                // the sends are no-ops, but the steps - and the text they are there
                // to protect - are what this pins.
                var steps = strategy.GetStopSequence();
                Assert(steps.Count > 0 && steps[0].Action == StreamingStopAction.SendMessage &&
                       System.Text.Encoding.UTF8.GetString(steps[0].Payload!) ==
                           "{\"realtime_input\":{\"audio_stream_end\":true}}",
                    "the stop sequence must still open with audio_stream_end");
                Assert(steps.Any(step => step.Action == StreamingStopAction.WaitForSessionComplete),
                    "the stop sequence must still wait for the trailing final");

                var finalText = await client.StopAsync();
                Assert(finalText.Contains("Hello, this is a test.") && finalText.Contains("Let us meet on Wednesday."),
                    $"both utterances must survive the turn boundary, got '{finalText}'");

                // ...and the SAME frame after the client asked to stop IS the
                // end-of-session signal the stop sequence is waiting for.
                var stopping = new StreamingTranscriptionClient(strategy, config);
                stopping.SetStateForTesting(StreamingConnectionState.Disconnecting);
                var completedAfterStop = 0;
                stopping.SessionCompleted += (_, _) => completedAfterStop++;
                stopping.HandleProviderEvent(strategy.ParseMessage(
                    "{\"serverContent\":{\"generationComplete\":true}}"));
                Assert(completedAfterStop == 1,
                    "a post-stop generationComplete must complete the session exactly once");
            });

            Run("the HyperWhisper Cloud dictation model picker never offers a streaming-only model", () =>
            {
                // gemini-3.5-transcribe-live is WebSocket-only - it has no
                // pre-recorded endpoint, so every dictation on it 400s. The catalog
                // already carries `"streaming": true` on that model; the Mode
                // editor's Model dropdown flat-mapped every model and ignored it.
                var catalog = CloudSttCatalog.Shared;

                var live = catalog.GetModel("geminiTranscribe", "gemini-3.5-transcribe-live");
                Assert(live != null, "the catalog no longer carries gemini-3.5-transcribe-live");

                var offered = catalog.ModelsForVendorKey("google").Select(entry => entry.Model.Id).ToArray();
                Assert(!offered.Contains("gemini-3.5-transcribe-live"),
                    $"a live-only model reached the dictation picker: {string.Join(", ", offered)}");
                Assert(offered.Contains("gemini-3.5-transcribe"),
                    $"the pre-recorded Google model must still be offered, got: {string.Join(", ", offered)}");

                // REGRESSION GUARD (mirrors macOS's deepgramNova3StaysSelectableForDictation).
                // The per-model `streaming` flag is NOT the filter, however much it
                // reads like one: it means "HW Cloud routes this model live", and
                // Deepgram carries it on nova-3-general and nova-3-medical, which
                // are the DEFAULT pre-recorded dictation models. Filtering on it
                // deletes the default dictation model from the picker - a worse bug
                // than the one this test exists for. Both facts are pinned here so
                // the flag cannot quietly become the filter again.
                Assert(catalog.GetModel("deepgramNova3", "nova-3-general")!.Streaming,
                    "nova-3-general lost its `streaming` flag - this guard no longer guards anything");
                var deepgram = catalog.ModelsForVendorKey("deepgram").Select(entry => entry.Model.Id).ToArray();
                Assert(deepgram.Contains("nova-3-general") && deepgram.Contains("nova-3-medical"),
                    $"Deepgram's pre-recorded models must stay selectable for dictation, got: {string.Join(", ", deepgram)}");
                Assert(deepgram.Length == catalog.ModelsForId("deepgramNova3").Count,
                    "the dictation picker dropped a Deepgram model - only live-only ids may be filtered");

                Assert(CloudSttCatalog.IsLiveOnlyModel("  GEMINI-3.5-TRANSCRIBE-LIVE  "),
                    "live-only matching must be trimmed and case-insensitive, like every other catalog model lookup");
                foreach (var notLiveOnly in new string?[] { null, "", "   ", "gemini-3.5-transcribe", "nova-3-general" })
                {
                    Assert(!CloudSttCatalog.IsLiveOnlyModel(notLiveOnly),
                        $"'{notLiveOnly}' must not be treated as live-only");
                }

                // The SEND path has to reject it too: the picker no longer offers
                // one, but a backup restore or a Local API write can still store it,
                // and it IS a member of the tier so plain membership accepts it.
                //
                // Assert on HyperWhisperCloudService.ResolveDictationModelId - the
                // method that actually produces the X-STT-Model header. The earlier
                // version of this check called CloudSttCatalog.DictationModelsForId,
                // which has NO production caller, so it passed whether or not the
                // send path guarded anything.
                var geminiDefault = catalog.DefaultModelIdForId("geminiTranscribe");
                Assert(geminiDefault == "gemini-3.5-transcribe",
                    $"the geminiTranscribe tier default moved, this check needs updating - got '{geminiDefault}'");
                Assert(HyperWhisperCloudService.ResolveDictationModelId("geminiTranscribe", "gemini-3.5-transcribe-live") == geminiDefault,
                    "the send path still sends the live-only id as X-STT-Model - every dictation would 400");
                Assert(HyperWhisperCloudService.ResolveDictationModelId("geminiTranscribe", "  GEMINI-3.5-TRANSCRIBE-LIVE  ") == geminiDefault,
                    "the send path let a padded/upper-cased live-only id through");

                // ...and it must NOT punt a legitimate model back to the default.
                Assert(HyperWhisperCloudService.ResolveDictationModelId("geminiTranscribe", "gemini-3.5-transcribe") == "gemini-3.5-transcribe",
                    "the send path replaced a valid Gemini dictation model with the tier default");
                foreach (var deepgramModel in catalog.ModelsForId("deepgramNova3"))
                {
                    Assert(HyperWhisperCloudService.ResolveDictationModelId("deepgramNova3", deepgramModel.Id) == deepgramModel.Id,
                        $"the send path dropped Deepgram's '{deepgramModel.Id}' - only live-only ids may be filtered");
                }
            });

            Run("the Gemini 3.5 Transcribe API key survives a backup export/restore round trip", () =>
            {
                // Configure ONLY the new key. The LEGACY `gemini` post-processing
                // key restores fine, which is what masked this: a user with both
                // sees Google "configured" and Gemini 3.5 Transcribe silently unset.
                var stored = new Dictionary<TranscriptionApiKeyType, string?>
                {
                    [TranscriptionApiKeyType.GeminiTranscribe] = "AIza-gemini-transcribe-key",
                };

                var exported = UniversalBackupMapper.MapApiKeys(
                    _ => null,
                    type => stored.TryGetValue(type, out var value) ? value : null);
                Assert(exported.GeminiTranscribe == "AIza-gemini-transcribe-key",
                    "the export dropped the Gemini 3.5 Transcribe key");
                Assert(exported.Gemini == null,
                    "the export must not fold the new key into the legacy `gemini` slot");

                // Through the wire format, under the name the schema declares.
                var json = JsonSerializer.Serialize(exported);
                Assert(json.Contains("\"geminitranscribe\""),
                    $"expected the schema's lowercase apiKeys name in the export, got: {json}");

                var restored = new Dictionary<TranscriptionApiKeyType, string?>();
                UniversalBackupMapper.ApplyApiKeys(
                    JsonSerializer.Deserialize<UniversalApiKeys>(json)!,
                    (_, _) => { },
                    (type, value) => restored[type] = value);
                Assert(restored.TryGetValue(TranscriptionApiKeyType.GeminiTranscribe, out var roundTripped) &&
                       roundTripped == "AIza-gemini-transcribe-key",
                    "the restore left Gemini 3.5 Transcribe unconfigured - the reported repro");

                // A backup written with the camelCase catalog spelling still
                // restores; it lands in the JsonExtensionData bag, not the field.
                var camelCase = JsonSerializer.Deserialize<UniversalApiKeys>(
                    "{\"geminiTranscribe\":\"AIza-from-another-platform\"}")!;
                Assert(camelCase.GeminiTranscribe == null,
                    "the camelCase spelling is deliberately NOT the declared field name");
                var tolerated = new Dictionary<TranscriptionApiKeyType, string?>();
                UniversalBackupMapper.ApplyApiKeys(camelCase, (_, _) => { }, (type, value) => tolerated[type] = value);
                Assert(tolerated.TryGetValue(TranscriptionApiKeyType.GeminiTranscribe, out var fromCamel) &&
                       fromCamel == "AIza-from-another-platform",
                    "a camelCase `geminiTranscribe` key must still restore");
            });

            RunAsync("the Chirp 3 tier migration leaves a BYOK mode alone", async () =>
            {
                // The migration keys on CloudAccuracyTier. That column is left
                // behind verbatim when a mode is switched to BYOK, so an unscoped
                // UPDATE rewrites a BYOK Grok mode's `''` sentinel model id to
                // gemini-3.5-transcribe and the next dictation posts a Google model
                // to xAI. Precedent for the scoping:
                // 20260508120000_MigrateRemovedDeepgramModels.
                const string PreviousMigration = "20260823180000_AddWordTimestamps";
                const string ThisMigration = "20260827090000_MigrateGoogleChirp3TierToGeminiTranscribe";

                var databasePath = Path.Combine(
                    Path.GetTempPath(), "HyperWhisper.SmokeTests", Guid.NewGuid().ToString("N"), "chirp3.db");

                try
                {
                    using (var context = new HyperWhisperDbContext(databasePath))
                    {
                        await context.Database
                            .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                            .MigrateAsync(PreviousMigration);
                        var applied = await context.Database.GetAppliedMigrationsAsync();
                        Assert(!applied.Contains(ThisMigration),
                            "the migration under test already ran - this test would prove nothing");

                        // The row the migration DOES own.
                        context.Modes.Add(new Mode
                        {
                            Name = "cloud-chirp",
                            ProviderType = "cloud",
                            CloudProvider = "hyperwhisper",
                            CloudAccuracyTier = "googleChirp3",
                            CloudTranscriptionModel = "chirp_3",
                        });
                        // Switched to BYOK Grok and never re-saved: the stale tier
                        // is still there, and Grok's model id is the empty sentinel
                        // the CASE arm would rewrite.
                        context.Modes.Add(new Mode
                        {
                            Name = "byok-grok",
                            ProviderType = "cloud",
                            CloudProvider = "grok",
                            CloudAccuracyTier = "googleChirp3",
                            CloudTranscriptionModel = "",
                        });
                        // Same shape, a BYOK vendor with a real model id.
                        context.Modes.Add(new Mode
                        {
                            Name = "byok-deepgram",
                            ProviderType = "cloud",
                            CloudProvider = "deepgram",
                            CloudAccuracyTier = "chirp_3",
                            CloudTranscriptionModel = "nova-3-general",
                        });
                        await context.SaveChangesAsync();
                    }

                    using (var context = new HyperWhisperDbContext(databasePath))
                    {
                        await context.Database.MigrateAsync();
                    }

                    using (var verify = new HyperWhisperDbContext(databasePath))
                    {
                        var cloud = verify.Modes.Single(mode => mode.Name == "cloud-chirp");
                        Assert(cloud.CloudAccuracyTier == "geminiTranscribe" &&
                               cloud.CloudTranscriptionModel == "gemini-3.5-transcribe",
                            $"the HW-Cloud Chirp row did not migrate: '{cloud.CloudAccuracyTier}' / '{cloud.CloudTranscriptionModel}'");

                        var grok = verify.Modes.Single(mode => mode.Name == "byok-grok");
                        Assert(grok.CloudProvider == "grok" && grok.CloudTranscriptionModel == "",
                            $"the BYOK Grok mode was rewritten to '{grok.CloudTranscriptionModel}' - it would post a Google model to xAI");

                        var deepgram = verify.Modes.Single(mode => mode.Name == "byok-deepgram");
                        Assert(deepgram.CloudTranscriptionModel == "nova-3-general" &&
                               deepgram.CloudAccuracyTier == "chirp_3",
                            $"the BYOK Deepgram mode was rewritten: '{deepgram.CloudAccuracyTier}' / '{deepgram.CloudTranscriptionModel}'");
                    }
                }
                finally
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(databasePath);
                        if (directory != null && Directory.Exists(directory))
                            Directory.Delete(directory, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            });

            RunAsync("the inline-base64 body streams bytes identical to the buffered build it replaced", async () =>
            {
                // RustHttpExecutor used to hold the file, its base64 AND the joined
                // array live at once - 88.7 MB of LOH for a 14 MB recording, on
                // every one of up to 8 retry attempts. It now encodes onto the
                // socket. The bytes must not have moved an inch: same padded,
                // non-URL-safe alphabet, no line breaks.
                var path = Path.Combine(
                    Path.GetTempPath(), "HyperWhisper.SmokeTests", Guid.NewGuid().ToString("N") + ".wav");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                try
                {
                    // Deliberately NOT a multiple of 3, so the trailing partial
                    // group exercises the encoder's final-block padding.
                    var audio = new byte[(5 * 1024 * 1024) + 2];
                    new Random(20260827).NextBytes(audio);
                    await File.WriteAllBytesAsync(path, audio);

                    var prefix = System.Text.Encoding.UTF8.GetBytes("{\"audio\":{\"data\":\"");
                    var suffix = System.Text.Encoding.UTF8.GetBytes("\"}}");

                    using var message = RustHttpExecutor.BuildRequestMessage(new HttpRequest(
                        @method: uniffi.hyperwhisper_core.HttpMethod.Post,
                        @url: "https://generativelanguage.googleapis.com/v1beta/interactions",
                        @headers: new List<Header>(),
                        @body: new Body.JsonWithBase64File(prefix, path, suffix)));

                    Assert(message.Content != null, "the inline-base64 body produced no content at all");
                    var produced = await message.Content!.ReadAsByteArrayAsync();

                    // The exact previous implementation, verbatim.
                    var base64 = Convert.ToBase64String(File.ReadAllBytes(path));
                    var expected = new byte[prefix.Length + base64.Length + suffix.Length];
                    Buffer.BlockCopy(prefix, 0, expected, 0, prefix.Length);
                    Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes(base64), 0, expected, prefix.Length, base64.Length);
                    Buffer.BlockCopy(suffix, 0, expected, prefix.Length + base64.Length, suffix.Length);

                    Assert(produced.Length == expected.Length,
                        $"streamed body is {produced.Length} bytes, buffered build was {expected.Length}");
                    Assert(produced.AsSpan().SequenceEqual(expected),
                        "the streamed inline-base64 body is not byte-identical to the buffered build it replaced");

                    // Content-Length must be declared, not chunked: the vendor
                    // rejects a chunked upload on this endpoint.
                    Assert(message.Content.Headers.ContentLength == expected.Length,
                        $"declared Content-Length {message.Content.Headers.ContentLength}, body is {expected.Length}");
                    Assert(message.Content.Headers.ContentType?.MediaType == "application/json",
                        "the inline-base64 body must be sent as application/json");
                }
                finally
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(path);
                        if (directory != null && Directory.Exists(directory))
                            Directory.Delete(directory, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            });

            Run("the Meta API key uses its isolated backup slot", () =>
            {
                const string Placeholder = "not-a-real-secret-value";
                var stored = new Dictionary<TranscriptionApiKeyType, string?>
                {
                    [TranscriptionApiKeyType.Meta] = Placeholder,
                };
                var exported = UniversalBackupMapper.MapApiKeys(
                    _ => null,
                    type => stored.TryGetValue(type, out var value) ? value : null);
                Assert(exported.Meta == Placeholder, "the export dropped the Meta key");
                Assert(exported.Gemini == null && exported.GeminiTranscribe == null,
                    "the Meta key leaked into a Google key slot");

                var json = JsonSerializer.Serialize(exported);
                Assert(json.Contains("\"meta\""), "the Meta backup field is not lowercase");
                var restored = new Dictionary<TranscriptionApiKeyType, string?>();
                UniversalBackupMapper.ApplyApiKeys(
                    JsonSerializer.Deserialize<UniversalApiKeys>(json)!,
                    (_, _) => { },
                    (type, value) => restored[type] = value);
                Assert(restored.TryGetValue(TranscriptionApiKeyType.Meta, out var value)
                       && value == Placeholder,
                    "the restore left Meta unconfigured");
            });

            RunAsync("typed multipart files survive the C# binding beside streamed audio", async () =>
            {
                var path = Path.Combine(
                    Path.GetTempPath(), "HyperWhisper.SmokeTests", Guid.NewGuid().ToString("N") + ".wav");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try
                {
                    await File.WriteAllTextAsync(path, "streamed-audio-marker");
                    var metadata = System.Text.Encoding.UTF8.GetBytes("{\"mode\":\"PUSH_TO_TALK\"}");
                    using var message = RustHttpExecutor.BuildRequestMessage(new HttpRequest(
                        @method: uniffi.hyperwhisper_core.HttpMethod.Post,
                        @url: "https://example.test/transcribe",
                        @headers: new List<Header>(),
                        @body: new Body.Multipart("test-boundary", new List<HwPart>
                        {
                            new HwPart.InlineFile("request", "request.json", "application/json", metadata),
                            new HwPart.FileRef("audio", path, "audio/wav", "audio.wav"),
                        })));

                    Assert(message.Content is MultipartFormDataContent,
                        "typed multipart request produced no multipart content");
                    var parts = ((MultipartFormDataContent)message.Content!).ToList();
                    var requestPart = parts.SingleOrDefault(part =>
                        part.Headers.ContentDisposition?.Name?.Trim('"') == "request");
                    var audioPart = parts.SingleOrDefault(part =>
                        part.Headers.ContentDisposition?.Name?.Trim('"') == "audio");
                    Assert(requestPart?.Headers.ContentDisposition?.FileName?.Trim('"') == "request.json",
                        "typed request.json disposition is missing");
                    Assert(requestPart?.Headers.ContentType?.MediaType == "application/json",
                        "typed request.json MIME is missing");
                    Assert(System.Text.Encoding.UTF8.GetString(
                            await requestPart!.ReadAsByteArrayAsync()) == "{\"mode\":\"PUSH_TO_TALK\"}",
                        "typed request.json bytes are missing");
                    Assert(audioPart?.Headers.ContentDisposition?.FileName?.Trim('"') == "audio.wav",
                        "streamed audio disposition is missing");
                    Assert(System.Text.Encoding.UTF8.GetString(
                            await audioPart!.ReadAsByteArrayAsync()) == "streamed-audio-marker",
                        "streamed audio bytes are missing");
                }
                finally
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(path);
                        if (directory != null && Directory.Exists(directory))
                            Directory.Delete(directory, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            });

            // =================================================================
            // Local API wire contract (issue #289)
            //
            // #289 observed that `find app/macos app/windows -ipath '*test*'
            // -iname '*localapi*'` was empty: of the three implementations of
            // one documented contract, only the most-drifted had any tests.
            // These are the Windows half.
            //
            // They test the SEAM, not the logic. The decision table, the
            // encoder and the constant-time compare are pinned by the Rust
            // crate's own suite, where they can be fuzzed. What can still go
            // wrong here is the bridge — a header that never reaches the guard,
            // a status the middleware overrides, a token the DPAPI store would
            // reject.
            // =================================================================

            Run("the origin guard allows a loopback client and denies DNS rebinding", () =>
            {
                static bool Allowed(string? host, string? origin, string? fetchSite, int port)
                {
                    // Fully qualified: `Microsoft.AspNetCore.Http` cannot be
                    // imported here, because its `HttpRequest` collides with
                    // the UniFFI binding's own `HttpRequest` record.
                    var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                    if (host != null) context.Request.Headers["Host"] = host;
                    if (origin != null) context.Request.Headers["Origin"] = origin;
                    if (fetchSite != null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
                    return LocalApiOriginGuard.IsAllowed(context, port);
                }

                Assert(Allowed("127.0.0.1:51671", null, null, 51671), "a loopback curl client must be served");
                Assert(Allowed("localhost:51671", null, null, 51671), "the localhost spelling must be served");
                Assert(Allowed("LocalHost:51671", null, null, 51671), "Host is case-insensitive");
                Assert(Allowed("127.0.0.1:51671", "http://127.0.0.1:51671", "same-origin", 51671),
                    "a same-origin browser fetch must be served");

                // The attack. A rebound page still sends the attacker's Host.
                Assert(!Allowed("attacker.com:51671", "http://attacker.com:51671", "cross-site", 51671),
                    "a DNS-rebound request must be rejected");
                Assert(!Allowed("127.0.0.1:51671", "http://attacker.com", null, 51671),
                    "a cross-origin Origin must be rejected");
                Assert(!Allowed("127.0.0.1:51671", null, "cross-site", 51671),
                    "cross-site fetch metadata must be rejected");
                Assert(!Allowed(null, null, null, 51671), "a request with no Host header must be rejected");
                Assert(!Allowed("127.0.0.1:51672", null, null, 51671), "the wrong port must be rejected");
                Assert(!Allowed("127.0.0.1:51671", null, null, 0), "an unbound server must serve nothing");
            });

            Run("the bearer check accepts only the real token", () =>
            {
                var token = HyperwhisperCoreMethods.LocalApiGenerateToken(new byte[32]);
                Assert(token.Length == 43, $"expected a 43-character token, got {token.Length}");
                Assert(HyperwhisperCoreMethods.LocalApiIsWellFormedToken(token),
                    "the generated token is not in the base64url alphabet the DPAPI store expects");

                Assert(LocalApiAuth.Authorize($"Bearer {token}", token), "the real token must authorize");
                Assert(LocalApiAuth.Authorize($"bearer {token}", token), "the scheme is case-insensitive");
                Assert(LocalApiAuth.Authorize($"  Bearer {token}  ", token), "the header is trimmed");

                Assert(!LocalApiAuth.Authorize("", token), "an absent header must not authorize");
                Assert(!LocalApiAuth.Authorize(token, token), "a bare token with no scheme must not authorize");
                Assert(!LocalApiAuth.Authorize($"Basic {token}", token), "another scheme must not authorize");
                Assert(!LocalApiAuth.Authorize($"Bearer {token}x", token), "a longer token must not authorize");
                for (var length = 0; length < token.Length; length++)
                {
                    Assert(!LocalApiAuth.Authorize($"Bearer {token[..length]}", token),
                        $"a {length}-character prefix of the token must not authorize");
                }
                // The gap #289 closed on every platform at once.
                Assert(!LocalApiAuth.Authorize($"Bearer {token}", ""),
                    "an empty stored credential must authorize nothing");
            });

            Run("the error-code constants are the shared closed set of 14", () =>
            {
                // A Swift `Codable` enum is closed, so a client sharing the
                // macOS decoder fails to decode the ENTIRE envelope on a
                // fifteenth code. These constants must not grow one.
                var shared = HyperwhisperCoreMethods.LocalApiAllErrorCodes()
                    .Select(HyperwhisperCoreMethods.LocalApiErrorCodeWireValue)
                    .ToHashSet(StringComparer.Ordinal);
                Assert(shared.Count == 14, $"expected 14 shared codes, got {shared.Count}");

                var windows = typeof(LocalApiErrorCode)
                    .GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                    .Select(field => (string)field.GetRawConstantValue()!)
                    .ToHashSet(StringComparer.Ordinal);
                Assert(windows.SetEquals(shared),
                    $"the Windows constants and the shared set disagree: [{string.Join(", ", windows.Except(shared))}] / [{string.Join(", ", shared.Except(windows))}]");

                // The four Linux emitted outside the set, plus the declared and
                // never-used one.
                foreach (var outside in new[] { "PAYLOAD_TOO_LARGE", "CANCELLED", "UNAUTHORIZED", "RECORDING_NOT_FOUND", "INTERNAL_ERROR" })
                {
                    Assert(HyperwhisperCoreMethods.LocalApiErrorCodeFromWireValue(outside) is null,
                        $"{outside} must not be in the closed set");
                }
            });

            Run("a business failure is HTTP 200 and the guard's 403 is macOS's response", () =>
            {
                foreach (var code in HyperwhisperCoreMethods.LocalApiAllErrorCodes())
                {
                    var business = HyperwhisperCoreMethods.LocalApiBusinessFailure(code, "x", null);
                    Assert(business.httpStatus == 200,
                        $"{code} came back as {business.httpStatus}, not the documented 200");
                }
                Assert(HyperwhisperCoreMethods.LocalApiBadRequestFailure("x", null).httpStatus == 400,
                    "a malformed request is still 400");

                var forbidden = HyperwhisperCoreMethods.LocalApiForbiddenOriginFailure();
                Assert(forbidden.httpStatus == 403, "the origin guard responds 403");
                Assert(HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(forbidden.code) == "INVALID_REQUEST",
                    "the guard must NOT invent a FORBIDDEN code - that would be a contract change on macOS");
                Assert(forbidden.message == "Request rejected: Host/Origin not permitted.",
                    "the guard's message is macOS's, verbatim");

                // The 401 body must stay byte-identical to what Windows sent
                // before #289 — no hint, because the hint names the platform's
                // own discovery file and Windows never sent one.
                var unauthorized = HyperwhisperCoreMethods.LocalApiUnauthorizedFailure(null);
                Assert(unauthorized.httpStatus == 401, "a credential failure is still 401");
                Assert(unauthorized.json == "{\"ok\":false,\"error\":{\"code\":\"INVALID_REQUEST\",\"message\":\"Missing or invalid bearer token\"}}",
                    $"the 401 envelope changed shape: {unauthorized.json}");
            });

            // =================================================================
            // Local API request-size limits (issue #375, the Windows half)
            //
            // #405 bounded macOS and moved the two caps into hw-localapi. It
            // deliberately skipped Windows, which is a SEPARATE implementation:
            // app/windows/.../LocalApi references neither PortableLocalApi nor
            // LocalApiHost, so nothing the portable head enforces ever reached
            // it. What it actually had was Kestrel's OWN default of 30,000,000
            // bytes — an accidental cap, at a number no other head uses — and a
            // bare `catch` around every JSON body read that turned Kestrel's
            // rejection into HTTP 400 "Invalid JSON body".
            //
            // These pin the four things that were wrong, in the order they were
            // wrong: the configured cap, the failure shape, the two base64
            // guards, and the `file` cap.
            //
            // As with #289 above, they test the SEAM. The numbers and the
            // envelope text are the Rust crate's, and its own suite fuzzes
            // them; what can go wrong here is a limit that never reaches
            // Kestrel, an exception the head misreads, or a guard on a path
            // nothing calls.
            // =================================================================

            Run("the Kestrel host bounds the request body at the shared cap", () =>
            {
                var maxRequest = (long)HyperwhisperCoreMethods.LocalApiMaxRequestBytes();
                var maxUpload = (long)HyperwhisperCoreMethods.LocalApiMaxUploadBytes();
                var maxBase64 = (long)HyperwhisperCoreMethods.LocalApiMaxBase64LengthForUpload();

                Assert(maxUpload <= maxRequest,
                    $"the shared upload cap {maxUpload} is above the request cap {maxRequest}");
                Assert(LocalApiLimits.MaxRequestBytes == maxRequest,
                    $"LocalApiLimits.MaxRequestBytes is {LocalApiLimits.MaxRequestBytes}, the shared core says {maxRequest}");
                Assert(LocalApiLimits.MaxUploadBytes == maxUpload,
                    $"LocalApiLimits.MaxUploadBytes is {LocalApiLimits.MaxUploadBytes}, the shared core says {maxUpload}");
                Assert(LocalApiLimits.MaxBase64LengthForUpload == maxBase64,
                    $"LocalApiLimits.MaxBase64LengthForUpload is {LocalApiLimits.MaxBase64LengthForUpload}, the shared core says {maxBase64}");

                // The REAL host, not a look-alike built here: BuildApp is what
                // Start() calls, and reading the limit back off its options is
                // the only way to prove the configure delegate ran. Nothing
                // binds a socket until StartAsync, so this costs no port.
                var app = LocalApiServer.Instance.BuildApp(0);
                try
                {
                    var kestrel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                            Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>(app.Services)
                        .Value;
                    Assert(kestrel.Limits.MaxRequestBodySize == maxRequest,
                        $"the Local API host caps the body at {kestrel.Limits.MaxRequestBodySize?.ToString() ?? "null"}, "
                            + $"the shared core says {maxRequest}. Kestrel's own default is 30000000 — if that is the "
                            + "number above, ConfigureKestrel lost its LocalApiLimits.ApplyRequestBodyLimit call.");
                }
                finally
                {
                    app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            });

            // RunAsync blocks on GetAwaiter().GetResult(), and by this point the
            // "BackupExportSettingsPage initializes under WPF" case above has
            // left a DispatcherSynchronizationContext on this thread. This
            // console harness never runs a Dispatcher loop, so an awaited
            // continuation posted to that context would never run and the whole
            // suite would hang. Detach for the duration, exactly as the
            // onboarding block below does.
            var limitsPreviousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            RunAsync("an over-limit body answers 200 + INVALID_REQUEST, never 400 and never 413", async () =>
            {
                var tooLarge = HyperwhisperCoreMethods.LocalApiRequestTooLargeFailure();
                var cap = LocalApiLimits.MaxRequestBytes;

                // A real Kestrel listener on an ephemeral loopback port,
                // configured through the same ApplyRequestBodyLimit the server
                // uses and answering through the same ReadJsonBodyAsync every
                // route on this head now calls. A DefaultHttpContext could not
                // prove any of this: the rejection is Kestrel's, raised from
                // inside the body read.
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    LocalApiLimits.ApplyRequestBodyLimit(options);
                    options.Listen(IPAddress.Loopback, 0);
                });
                await using var app = builder.Build();
                app.MapPost("/probe", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
                {
                    var (dto, failure) = await LocalApiLimits.ReadJsonBodyAsync<ModeDto>(
                        ctx, "Required: name. See /modes GET for the full shape.");
                    return failure ?? LocalApiResponder.Ok(new { ok = true, name = dto?.Name ?? "" });
                });
                await app.StartAsync();
                try
                {
                    var port = app.Services
                        .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                        .Addresses.Select(address => new Uri(address).Port).First(p => p > 0);
                    // The two refusals go over a RAW SOCKET rather than
                    // HttpClient. A refused request is one the server answers
                    // and then resets, because the rest of the body is still
                    // coming; HttpClient reports that as
                    // "Error while copying content to a stream" and loses the
                    // response it already had. That is a fault in the test, not
                    // in the head — a real client sees the envelope, because
                    // this reads the socket WHILE it writes.
                    //
                    // 1. One byte over, declared in Content-Length. The head
                    //    answers from the header without consuming the body, so
                    //    only a slice of it is sent.
                    {
                        var (status, code, message) = await RawProbeAsync(
                            port, $"Content-Length: {cap + 1}\r\n", PaddedJsonOfExactly(cap + 1), chunked: false);
                        Assert(status == 200,
                            $"an over-limit body answered HTTP {status}. It must be 200: 400 is what the old bare "
                                + "catch sent, and 413 wants a PAYLOAD_TOO_LARGE code that is outside the closed 14.");
                        Assert(code == "INVALID_REQUEST", $"an over-limit body answered code {code}");
                        Assert(message == tooLarge.message,
                            $"the request-limit message is \"{message}\", the shared core says \"{tooLarge.message}\"");
                    }

                    // 2. The same overflow with NO Content-Length, so the
                    //    pre-check cannot see it and Kestrel's own counter is
                    //    what refuses: BadHttpRequestException with StatusCode
                    //    413, raised from inside ReadFromJsonAsync. This is the
                    //    exception the bare catch used to swallow, and this
                    //    probe is the only proof that the head reads it right.
                    {
                        var (status, code, message) = await RawProbeAsync(
                            port, "Transfer-Encoding: chunked\r\n", PaddedJsonOfExactly(cap + 1), chunked: true);
                        Assert(status == 200,
                            $"a chunked over-limit body answered HTTP {status}; Kestrel's 413 must not reach the wire");
                        Assert(code == "INVALID_REQUEST", $"a chunked over-limit body answered code {code}");
                        Assert(message == tooLarge.message,
                            $"the chunked request-limit message is \"{message}\", the shared core says \"{tooLarge.message}\"");
                    }

                    // 3. The accepting side of the boundary. A body of EXACTLY
                    //    the cap is read whole, so there is nothing left to
                    //    reset and HttpClient is safe here.
                    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                    using (var response = await client.PostAsync("/probe", JsonBytes(PaddedJsonOfExactly(cap))))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        Assert((int)response.StatusCode == 200,
                            $"a body of exactly {cap} bytes answered HTTP {(int)response.StatusCode}");
                        Assert(!body.Contains(tooLarge.message, StringComparison.Ordinal),
                            $"a body of exactly {cap} bytes was refused for its size; the cap is inclusive on every head");
                    }

                    // 4. A genuinely malformed body keeps the answer it has
                    //    always had. The point of the fix is that "too big" and
                    //    "malformed" stopped being the same reply.
                    using (var response = await client.PostAsync(
                        "/probe", new ByteArrayContent("{\"name\": "u8.ToArray())
                        {
                            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                        }))
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        var error = document.RootElement.GetProperty("error");
                        Assert((int)response.StatusCode == 400,
                            $"malformed JSON answered HTTP {(int)response.StatusCode}, it must stay 400");
                        Assert(error.GetProperty("code").GetString() == "INVALID_REQUEST",
                            $"malformed JSON answered code {error.GetProperty("code").GetString()}");
                        Assert(error.GetProperty("message").GetString() == "Invalid JSON body",
                            "malformed JSON no longer says \"Invalid JSON body\"; a caller can no longer tell it "
                                + "from an over-limit body");
                    }
                }
                finally
                {
                    await app.StopAsync();
                }

                // `{"name":"x"` followed by padding spaces and `}`. JSON allows
                // whitespace between tokens, so a 50 MiB body of this shape
                // costs the server one two-character string rather than a 50 MiB
                // one — the size guards are what this test is about, not the
                // deserializer's appetite.
                static byte[] PaddedJsonOfExactly(long totalBytes)
                {
                    const string prefix = "{\"name\":\"x\"";
                    const string suffix = "}";
                    var bytes = new byte[totalBytes];
                    System.Text.Encoding.ASCII.GetBytes(prefix).CopyTo(bytes, 0);
                    bytes.AsSpan(prefix.Length, bytes.Length - prefix.Length - suffix.Length).Fill((byte)' ');
                    System.Text.Encoding.ASCII.GetBytes(suffix).CopyTo(bytes, bytes.Length - suffix.Length);
                    return bytes;
                }

                static ByteArrayContent JsonBytes(byte[] payload) => new(payload)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                };
            });

            RunAsync("the mode wire contract comes from the shared core", async () =>
            {
                // WHY A KEY READER EXISTS HERE AT ALL (issue #356). This head
                // cannot infer which keys a caller sent from `ModeDto`:
                // `Punctuation`, `Capitalization` and `ProfanityFilter` are
                // non-nullable `bool`, so an absent key and an explicit `false`
                // deserialise to the same value. That is the whole reason
                // `ReadJsonBodyWithKeysAsync` had to be added beside the
                // existing reader instead of reusing it.
                static Microsoft.AspNetCore.Http.HttpContext BodyContext(string json)
                {
                    var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    ctx.Request.Body = new MemoryStream(bytes);
                    ctx.Request.ContentLength = bytes.Length;
                    ctx.Request.ContentType = "application/json";
                    return ctx;
                }

                var (full, fullKeys, fullFailure) = await LocalApiLimits.ReadJsonBodyWithKeysAsync<ModeDto>(
                    BodyContext("""{"name":"Seven","preset":"hyper","language":"en","model":"base","punctuation":false,"capitalization":false,"profanityFilter":false}"""));
                Assert(fullFailure == null && full != null, "a well-formed create body was refused");
                Assert(fullKeys.Count == 7 && fullKeys.Contains("profanityFilter"),
                    $"the key reader lost keys: [{string.Join(", ", fullKeys)}]");
                Assert(!full!.Punctuation,
                    "an explicit false did not survive the second parse; the reader must deserialise from the same document");

                var (partial, partialKeys, partialFailure) = await LocalApiLimits.ReadJsonBodyWithKeysAsync<ModeDto>(
                    BodyContext("""{"name":"Only"}"""));
                Assert(partialFailure == null && partial != null && partialKeys.Count == 1,
                    "a one-key body did not read back as one key");
                // An explicit `false` and an absent key are the SAME `ModeDto`
                // here — which is exactly why the key list, not the DTO, is what
                // the required check reads.
                Assert(!partial!.Punctuation && !full.Punctuation,
                    "the DTO stopped conflating an absent boolean with an explicit false; the key reader may no longer be needed");

                var (_, brokenKeys, brokenFailure) = await LocalApiLimits.ReadJsonBodyWithKeysAsync<ModeDto>(
                    BodyContext("""{"name": """));
                Assert(brokenFailure != null && brokenKeys.Count == 0,
                    "malformed JSON did not answer the same 400 the existing reader answers");

                // DECISION B — the required seven, create only. `{"name":"Only"}`
                // created a mode on this head before #356; `openapi.yaml` has
                // required all seven since it was written, and macOS has
                // enforced them by construction since it shipped.
                var required = HyperwhisperCoreMethods.LocalApiRequiredModeKeys();
                Assert(required.Count == 7 && required[0] == "name",
                    "the shared required-key list changed shape");
                var missing = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
                    HwLocalApiModeOperation.Create, partialKeys.ToList(), "Only", null, null, null, null, null, null, null));
                Assert(missing != null && missing.httpStatus == 400,
                    "a create body missing six required keys was accepted");
                var patchOk = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
                    HwLocalApiModeOperation.Patch, partialKeys.ToList(), "Only", null, null, null, null, null, null, null));
                Assert(patchOk == null,
                    "the required-key rule leaked onto PATCH, where openapi.yaml has no required list");

                // DECISION C — `sortOrder` is bounded to the Int16 range its
                // storage column has always had. This head had no bound at all.
                var overflow = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
                    HwLocalApiModeOperation.Patch, [], null, null, null, null, 99999L, null, null, null));
                Assert(overflow != null
                        && overflow.httpStatus == 200
                        && HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(overflow.code) == LocalApiErrorCode.InvalidRequest,
                    "an out-of-Int16 sortOrder was accepted, or refused outside the closed fourteen");
                Assert(HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
                        HwLocalApiModeOperation.Patch, [], null, null, null, null, 32767L, null, null, null)) == null,
                    "sortOrder 32767 was refused; the bound is inclusive on every head");

                // DECISION D — one comparison key. `OrdinalIgnoreCase` was one
                // of three different answers to "the same name".
                Assert(HyperwhisperCoreMethods.LocalApiModeNameConflict("  WORK  ", ["Personal", "work"]),
                    "the shared collision rule stopped matching the name this head would have matched");
                var taken = HyperwhisperCoreMethods.LocalApiModeNameTakenFailure("Work", HwLocalApiModeOperation.Create);
                Assert(HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(taken.code) == LocalApiErrorCode.ModeNameTaken
                        && taken.message == "A mode named 'Work' already exists"
                        && taken.hint != null,
                    "the shared collision failure drifted from this head's wording");
                Assert(HyperwhisperCoreMethods.LocalApiModeNameTakenFailure("Work", HwLocalApiModeOperation.Patch).hint == null,
                    "the patch collision grew a hint this head has never sent");

                // ITEM 3 — one alias table. `qwen3_asr` is this head's own
                // response label; the trim is new here and could only ever
                // accept a spelling this head refused.
                foreach (var spelling in new[] { "qwen3_asr", " QWEN3-ASR ", "qwen", "Qwen3" })
                {
                    var transient = new Mode();
                    HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApplyEngineModel(
                        transient, spelling, model: null);
                    Assert(transient is { ProviderType: "local", LocalEngine: "parakeet", LocalParakeetModel: "qwen3-asr-0.6b" },
                        $"engine spelling '{spelling}' did not resolve through the shared alias table");
                }

                // A REAL ENGINE ID WINDOWS DOES NOT SHIP is ENGINE_UNAVAILABLE,
                // not `Unknown engine` — the resolver answers identity, and the
                // capability verdict is this head's.
                foreach (var absent in new[] { "nemotron", "nemotron-local", "applespeech", "speech-analyzer" })
                {
                    var caught = false;
                    try
                    {
                        HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApplyEngineModel(
                            new Mode(), absent, model: null);
                    }
                    catch (HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApiInputException ex)
                    {
                        caught = ex.Code == LocalApiErrorCode.EngineUnavailable
                            && !ex.Message.StartsWith("Unknown engine", StringComparison.Ordinal);
                    }
                    Assert(caught, $"engine '{absent}' was not refused as a known-but-unavailable engine");
                }
            });

            SynchronizationContext.SetSynchronizationContext(limitsPreviousContext);

            Run("the audio_base64 guards refuse an oversized clip before decoding it", () =>
            {
                var tooLarge = HyperwhisperCoreMethods.LocalApiUploadTooLargeFailure();

                // Both comparisons are `>`, so exactly the cap is accepted.
                Assert(!LocalApiLimits.ExceedsBase64UploadLimit(LocalApiLimits.MaxBase64LengthForUpload),
                    "a base64 string of exactly the encoded cap must be accepted");
                Assert(LocalApiLimits.ExceedsBase64UploadLimit(LocalApiLimits.MaxBase64LengthForUpload + 1),
                    "one character over the encoded cap must be refused");
                Assert(!LocalApiLimits.ExceedsUploadLimit(LocalApiLimits.MaxUploadBytes),
                    "audio of exactly the upload cap must be accepted");
                Assert(LocalApiLimits.ExceedsUploadLimit(LocalApiLimits.MaxUploadBytes + 1),
                    "one byte over the upload cap must be refused");

                // The encoded cap is derived from the decoded cap, so no string
                // that clears the pre-check can decode past MaxUploadBytes —
                // the post-decode guard is unreachable from here, exactly as
                // PortableLocalApi.cs:251 is unreachable without a shrunken
                // fixture cap. The boundary assertions above are its coverage.
                Assert(LocalApiLimits.MaxBase64LengthForUpload / 4 * 3 <= LocalApiLimits.MaxUploadBytes,
                    "the encoded cap now admits a string that decodes past the upload cap; the post-decode guard "
                        + "in ResolveAudioSource is no longer unreachable and needs a round-trip test of its own");

                // The production resolver, driven through the pre-check. If the
                // guard were missing this would allocate the decoded buffer
                // first — which is the amplification #375 is about.
                var request = new TranscribeRequest
                {
                    AudioBase64 = new string('A', checked((int)LocalApiLimits.MaxBase64LengthForUpload) + 1),
                    MimeType = "audio/wav"
                };
                var refusal = CaptureApiInputException(() =>
                    HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ResolveAudioSource(request));
                Assert(refusal.Code == LocalApiErrorCode.InvalidRequest,
                    $"an oversized audio_base64 answered code {refusal.Code}; every size refusal is INVALID_REQUEST");
                Assert(refusal.Message == tooLarge.message,
                    $"the base64 upload-limit message is \"{refusal.Message}\", the shared core says \"{tooLarge.message}\"");
            });

            Run("the file path is capped at the shared upload limit", () =>
            {
                var tooLarge = HyperwhisperCoreMethods.LocalApiUploadTooLargeFailure();
                var root = AppPaths.ProfileTempRecordingsDirectory;
                Directory.CreateDirectory(root);
                var oversizedPath = Path.Combine(root, $"oversized-{Guid.NewGuid():N}.wav");
                var atCapPath = Path.Combine(root, $"at-cap-{Guid.NewGuid():N}.wav");
                try
                {
                    // SetLength moves the end-of-file marker; NTFS does not
                    // write the bytes, so a 48 MiB fixture costs no 48 MiB of
                    // I/O. The cap reads FileStream.Length, which asks about the
                    // open handle, so the marker is what it sees.
                    using (var file = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write))
                        file.SetLength(LocalApiLimits.MaxUploadBytes + 1);

                    Assert(HistoryService.IsTrustedAudioPath(oversizedPath),
                        $"the fixture at {oversizedPath} is not inside a trusted recordings root, so this test would "
                            + "pass on the containment refusal rather than on the size cap");

                    var refusal = CaptureApiInputException(() =>
                        HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ResolveAudioSource(
                            new TranscribeRequest { File = oversizedPath }));
                    // FILE_NOT_ALLOWED here means the containment guard answered
                    // ahead of the size cap, which makes the test prove nothing.
                    // Report the whole path chain so that reads as a fixture
                    // fault rather than as a missing cap.
                    Assert(refusal.Code == LocalApiErrorCode.InvalidRequest,
                        $"an oversized file answered code {refusal.Code}; every size refusal is INVALID_REQUEST. "
                            + TrustedPathDiagnostics(oversizedPath));
                    Assert(refusal.Message == tooLarge.message,
                        $"the file upload-limit message is \"{refusal.Message}\", the shared core says \"{tooLarge.message}\"");

                    // The accepting side: exactly the cap still resolves.
                    using (var file = new FileStream(atCapPath, FileMode.CreateNew, FileAccess.Write))
                        file.SetLength(LocalApiLimits.MaxUploadBytes);

                    var (snapshotPath, isTemp, readLock) =
                        HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ResolveAudioSource(
                            new TranscribeRequest { File = atCapPath });
                    readLock?.Dispose();
                    Assert(isTemp && File.Exists(snapshotPath),
                        $"a file of exactly {LocalApiLimits.MaxUploadBytes} bytes was refused for its size; "
                            + "the cap is inclusive on every head");
                    try { File.Delete(snapshotPath); } catch (IOException) { }
                }
                finally
                {
                    foreach (var path in new[] { oversizedPath, atCapPath })
                    {
                        try { File.Delete(path); } catch (IOException) { }
                    }
                }
            });

            // =================================================================
            // Issue #379 — real transcription outcomes feed the health cache.
            //
            // The reported defect: with a valid Cloud key, POST /transcribe
            // {"engine":"googlespeech"} failed, and /health kept reporting that
            // provider "status":"healthy","reachable":true throughout. The probe
            // is not lying by accident — it hits the vendor's model-list endpoint
            // (and for the HW-Cloud-routed providers it short-circuits with no
            // network call at all), so it is genuinely green while transcription
            // is failing. Only the real outcome can correct it.
            //
            // These mirror the macOS cases (a)-(e) in
            // app/macos/hyperwhisperTests/CloudProviderHealthCacheTTLTests.swift.
            // The clock is injected exactly as macOS injects `now: () -> Date`,
            // so the 60 s window is crossed without a wall-clock wait.
            // =================================================================

            const CloudTranscriptionProvider healthProvider = CloudTranscriptionProvider.GoogleSpeech;

            static TranscriptionException ProviderDown() => new(
                TranscriptionErrorCode.ProviderUnavailable, "Google Chirp 3 unavailable", "Google Chirp 3", 503);

            Run("issue #379 (a): a recorded provider-down failure outranks a healthy probe", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                // The state the issue reported: the probe says Healthy.
                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                Assert(health.GetStatus(healthProvider) == ProviderHealth.Healthy, "probe should start Healthy");

                health.RecordTranscriptionOutcome(
                    healthProvider, health.CaptureTranscriptionCredentialGeneration(healthProvider), ProviderDown());

                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    $"health status {health.GetHealthStatus(healthProvider)}");
                Assert(health.GetStatus(healthProvider) == ProviderHealth.Healthy,
                    "the /health override leaked into the generic model status seam");

                // Even if the probe republishes Healthy underneath, the override wins.
                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    "a fresh healthy probe must not beat a real failure inside the window");

                // /health derives `reachable` from exactly this value
                // (HealthEndpoints.BuildTranscriptionProviders), so this is the
                // reported symptom, asserted at its source.
                Assert(health.GetHealthStatus(healthProvider) != ProviderHealth.Healthy,
                    "/health would still report reachable:true");
            });

            Run("issue #379 (b): the failure override expires after its 60s TTL", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                health.RecordTranscriptionOutcome(
                    healthProvider, health.CaptureTranscriptionCredentialGeneration(healthProvider), ProviderDown());

                // AddMilliseconds, not AddSeconds(59.9): DateTime.AddSeconds rounds
                // a fractional double to ticks, so 59.9 + 0.1 lands at 59.9999999s
                // and never reaches the boundary. Verified, not assumed.
                now = now.AddMilliseconds(59_900);

                // Re-stamp the probe at 59.9s. This is what separates the two 60s
                // windows: unlike macOS, whose `statuses` dictionary never expires,
                // GetStatus reads the TTL'd cache, so a probe left at t=0 would go
                // stale in the same instant the override does and the assertion
                // below would be reading cache expiry rather than override expiry.
                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    "still inside the window at 59.9s");

                // The gate is `>= TTL` -> expired, so exactly 60 s is already out.
                now = now.AddMilliseconds(100);
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Healthy,
                    $"status after expiry {health.GetHealthStatus(healthProvider)}");
            });

            Run("issue #379 (c): a recorded success clears the override immediately", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                var credentialGeneration = health.CaptureTranscriptionCredentialGeneration(healthProvider);
                health.RecordTranscriptionOutcome(healthProvider, credentialGeneration, ProviderDown());
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable, "precondition");

                // No clock movement at all - the success alone must clear it. A real
                // transcription is stronger evidence than any probe, so a provider
                // that recovered must not stay Unreachable for the remaining ~59s.
                health.RecordTranscriptionOutcome(healthProvider, credentialGeneration, null);

                Assert(health.GetStatus(healthProvider) == ProviderHealth.Healthy,
                    $"status {health.GetStatus(healthProvider)}");
            });

            Run("RustRetry caps the actual jittered sleep at the remaining budget", () =>
            {
                var admitted = RustRetry.AdmittedSleepMs(
                    coreDelayMs: 10_000,
                    sleptMs: 20_000,
                    budgetMs: 30_000,
                    jitterUnit: 1);
                Assert(admitted == 10_000, $"admitted {admitted}ms");
                Assert(20_000UL + admitted == 30_000, "actual sleep exceeded the budget");

                var unbounded = RustRetry.AdmittedSleepMs(
                    coreDelayMs: 10_000,
                    sleptMs: 20_000,
                    budgetMs: 0,
                    jitterUnit: 1);
                Assert(unbounded == 13_000, $"unbounded jitter {unbounded}ms");
            });

            Run("issue #379 (c1): a success near cache expiry starts a fresh TTL", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                now = now.AddSeconds(59);

                // The successful request is new evidence. It must stamp t=59,
                // even when the existing raw status is already Healthy.
                health.RecordTranscriptionOutcome(
                    healthProvider, health.CaptureTranscriptionCredentialGeneration(healthProvider), null);
                now = now.AddSeconds(2);

                Assert(health.GetStatus(healthProvider) == ProviderHealth.Healthy,
                    "a success at t=59 expired with the probe that ran at t=0");
            });

            Run("issue #379 (c2): an old-key success cannot overwrite a new-key Unauthorized verdict", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                var oldGeneration = health.CaptureTranscriptionCredentialGeneration(healthProvider);
                health.RegisterApiKeyChange(healthProvider, "replacement-key-0123456789");
                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Unauthorized);

                health.RecordTranscriptionOutcome(healthProvider, oldGeneration, null);

                Assert(health.GetStatus(healthProvider) == ProviderHealth.Unauthorized,
                    "an old-key success stamped the replacement key Healthy");
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unauthorized,
                    "/health lost the replacement key's Unauthorized verdict");
            });

            Run("issue #379 (c3): an unrelated provider key edit keeps an in-flight outcome valid", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                var googleGeneration = health.CaptureTranscriptionCredentialGeneration(healthProvider);

                health.RegisterApiKeyChange(CloudTranscriptionProvider.Deepgram, "replacement-deepgram-key");
                health.RecordTranscriptionOutcome(healthProvider, googleGeneration, ProviderDown());

                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    "an unrelated Deepgram key edit discarded a valid Google outcome");
            });

            Run("issue #379 (c4): production API-key write mappings advance only the affected provider", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                var geminiBefore = health.CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider.Gemini);
                var deepgramBefore = health.CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider.Deepgram);

                // These are the same helpers called by the two real
                // ApiKeyService.SetApiKey overloads after the vault write.
                ApiKeyService.RegisterTranscriptionApiKeyChange(
                    health, PostProcessingProvider.Gemini, "replacement-gemini-key");

                Assert(health.CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider.Gemini) == geminiBefore + 1,
                    "the shared Gemini key write did not advance Gemini transcription health");
                Assert(health.CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider.Deepgram) == deepgramBefore,
                    "the Gemini key write changed Deepgram's generation");

                ApiKeyService.RegisterTranscriptionApiKeyChange(
                    health, TranscriptionApiKeyType.Deepgram, "replacement-deepgram-key");

                Assert(health.CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider.Deepgram) == deepgramBefore + 1,
                    "the Deepgram transcription-key write did not advance Deepgram health");
                Assert(health.CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider.Gemini) == geminiBefore + 1,
                    "the Deepgram key write changed Gemini's generation");
            });

            Run("issue #379 (d): only a definitive provider-down verdict sets the override", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                // Marking a provider unreachable because the user's Wi-Fi dropped,
                // their card expired, or they recorded silence would be a worse bug
                // than the stale verdict the override exists to fix.
                var harmless = new Exception[]
                {
                    new TranscriptionException(TranscriptionErrorCode.Unauthorized, "bad key", "Google Chirp 3", 401),
                    new TranscriptionException(TranscriptionErrorCode.QuotaExceeded, "quota", "Google Chirp 3", 429),
                    new TranscriptionException(TranscriptionErrorCode.RateLimited, "slow down", "Google Chirp 3", 429),
                    new TranscriptionException(TranscriptionErrorCode.NetworkError, "offline", "Google Chirp 3"),
                    new TranscriptionException(TranscriptionErrorCode.NoSpeechDetected, "silence", "Google Chirp 3"),
                    new TranscriptionException(TranscriptionErrorCode.InvalidRequest, "bad body", "Google Chirp 3", 400),
                    new TranscriptionException(TranscriptionErrorCode.FileTooLarge, "too big", "Google Chirp 3", 413),
                    new TranscriptionException(TranscriptionErrorCode.Cancelled, "cancelled", "Google Chirp 3"),
                    new TranscriptionException(TranscriptionErrorCode.ProviderUnavailable, "request timeout", "Google Chirp 3", 408),
                    new TranscriptionException(TranscriptionErrorCode.ProviderUnavailable, "polling exhausted", "Google Chirp 3", 200),
                    new TranscriptionException(TranscriptionErrorCode.ProviderUnavailable, "status unavailable", "Google Chirp 3"),
                    new OperationCanceledException("cancelled"),
                    new HttpRequestException("connection refused")
                };

                foreach (var error in harmless)
                {
                    health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                    health.RecordTranscriptionOutcome(
                        healthProvider, health.CaptureTranscriptionCredentialGeneration(healthProvider), error);
                    Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Healthy,
                        $"{error.GetType().Name} must not mark the provider unreachable");
                    Assert(!CloudProviderHealthService.IsDefinitiveProviderDownVerdict(error),
                        $"{error.GetType().Name} classified as provider-down");
                }

                var issue379Failure = new TranscriptionException(
                    TranscriptionErrorCode.ProviderUnavailable,
                    "Google Chirp 3 unavailable",
                    "Google Chirp 3",
                    500);
                Assert(CloudProviderHealthService.IsDefinitiveProviderDownVerdict(issue379Failure),
                    "the issue #379 HTTP 500 must remain a provider-down verdict");
            });

            Run("issue #379 (e): the override never leaks into the forced-refresh verdict", () =>
            {
                // THE NO-WEDGE GUARANTEE. On macOS this is load-bearing:
                // ProviderHealth.unreachable.shouldBlockTranscription is true and
                // TranscriptionProviderRouter throws on a non-healthy ensureHealthy
                // verdict, so an override that leaked into the return value would
                // lock the user out of the provider for 60s after one blip.
                //
                // Windows has no equivalent pre-flight gate (nothing calls GetStatus
                // before transcribing), so there is nothing to wedge here. What is
                // mirrored is the invariant that produces the guarantee: the
                // override lives at the /health read seam (GetHealthStatus) only, and the
                // "give me a fresh verdict" path still returns the RAW probe result.
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                health.RecordTranscriptionOutcome(
                    healthProvider, health.CaptureTranscriptionCredentialGeneration(healthProvider), ProviderDown());
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable, "precondition");

                // GoogleSpeech is keyless, so RefreshAsync short-circuits to Unknown
                // without any network I/O. The value that matters is that it is the
                // probe's own verdict and NOT the override's Unreachable.
                var refreshed = health.RefreshAsync(healthProvider, force: true).GetAwaiter().GetResult();
                Assert(refreshed != ProviderHealth.Unreachable,
                    $"forced refresh returned the override, not the probe: {refreshed}");

                // …and reporting is unchanged by that refresh.
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    "the override must survive a probe inside its window");
            });

            Run("issue #379 (c2 guard): a failure does not refresh the raw probe cache", () =>
            {
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var health = new CloudProviderHealthService(() => now);

                health.SetCachedTranscriptionStatusForTests(healthProvider, ProviderHealth.Healthy);
                now = now.AddSeconds(59);
                health.RecordTranscriptionOutcome(
                    healthProvider, health.CaptureTranscriptionCredentialGeneration(healthProvider), ProviderDown());
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    "/health must apply the failure override");

                now = now.AddSeconds(2);
                // The t=0 raw probe is expired. GoogleSpeech has no BYOK key, so
                // reaching the normal refresh path returns its raw Unknown. If
                // the failure had re-stamped the cache at t=59, this call would
                // return cached Unreachable and suppress the refresh instead.
                var refreshed = health.RefreshAsync(healthProvider).GetAwaiter().GetResult();
                Assert(refreshed == ProviderHealth.Unknown,
                    $"failure refreshed the raw cache and suppressed the probe: {refreshed}");
                Assert(health.GetHealthStatus(healthProvider) == ProviderHealth.Unreachable,
                    "/health must stay honest while the raw probe refreshes");
            });

            // =================================================================
            // First-run onboarding flow model
            //
            // The Windows port of app/macos/hyperwhisper/Views/Onboarding/
            // OnboardingFlowModel.swift. These mirror all twelve suites of
            // hyperwhisperTests/OnboardingFlowModelTests.swift case for case, then
            // add four Windows-only suites for the state macOS has no counterpart
            // for: the shortcut row, the credits figure, the four-case device
            // availability, and the sample-clip Try It.
            //
            // Everything is driven through the seven seams in
            // ViewModels/Onboarding/OnboardingSeams.cs. Nothing here touches a
            // service, a window, or the disk.
            //
            // The fakes and the harness live in OnboardingTestSupport.cs.
            // =================================================================

            // The flow's asynchronous actions resume on whatever
            // SynchronizationContext is current. This console harness never runs a
            // Dispatcher loop, so a WPF context left behind by the window case would
            // queue a continuation that never runs. Detach for the duration.
            var onboardingPreviousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            // ----- Step gating -----------------------------------------------

            Run("onboarding: welcome always continues, permissions blocks without a microphone", () =>
            {
                var h = new OnboardingHarness();
                Assert(h.Flow.Step == OnboardingStep.Welcome, "the flow must start at welcome");
                Assert(h.Flow.CanContinue, "welcome is always passable");
                Assert(h.Flow.Advance(), "welcome must advance");

                Assert(h.Flow.Step == OnboardingStep.Permissions, "expected the permissions step");
                Assert(!h.Flow.CanContinue, "no microphone access must close the gate");
                Assert(!h.Flow.Advance(), "a closed gate must not advance");
                Assert(h.Flow.Step == OnboardingStep.Permissions, "a refused advance must not move the step");
            });

            Run("onboarding: the source step requires a selection", () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);

                Assert(!h.Flow.CanContinue, "no source picked yet");
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                Assert(h.Flow.CanContinue, "a picked source opens the gate");
            });

            Run("onboarding: back steps through the flow and stops at welcome", () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);

                Assert(h.Flow.Back(), "back from source must move");
                Assert(h.Flow.Step == OnboardingStep.Permissions, "expected permissions");
                Assert(h.Flow.Back(), "back from permissions must move");
                Assert(h.Flow.Step == OnboardingStep.Welcome, "expected welcome");
                Assert(!h.Flow.Back(), "welcome is the floor");
                Assert(h.Flow.Step == OnboardingStep.Welcome, "a refused back must not move the step");
            });

            Run("onboarding: advance stops at the final step", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Done);

                Assert(h.Flow.Step == OnboardingStep.Done, "expected done");
                Assert(!h.Flow.Advance(), "done is the ceiling");
            });

            // ----- Permissions -----------------------------------------------

            RunAsync("onboarding: granting the microphone after a denial reopens the gate", async () =>
            {
                var h = new OnboardingHarness();
                h.AdvanceTo(OnboardingStep.Permissions);
                Assert(!h.Flow.CanContinue, "precondition: the gate starts shut");

                h.Permissions.RequestResult = false;
                h.Flow.RequestMicrophoneAccess();
                await h.LastTask;

                Assert(!h.Flow.HasMicrophoneAccess, "a refused request must not grant access");
                Assert(h.Flow.PermissionErrorMessage is not null, "a refusal must raise the alert");
                Assert(!h.Flow.CanContinue, "a refusal keeps the gate shut");

                // The user grants it in Windows Settings; the flow re-reads on activation.
                h.Permissions.MicrophoneAuthorization = OnboardingMicrophoneAuthorization.Authorized;
                h.Flow.RefreshPermissions();

                Assert(h.Flow.HasMicrophoneAccess, "the re-read must pick the grant up");
                Assert(h.Flow.CanContinue, "the gate must reopen");
            });

            Run("onboarding: a denied microphone deep-links Settings instead of re-prompting", () =>
            {
                var h = new OnboardingHarness();
                h.Permissions.MicrophoneAuthorization = OnboardingMicrophoneAuthorization.Denied;

                h.Flow.HandleMicrophoneAction();

                Assert(h.Permissions.OpenedMicrophoneSettings == 1, "a denial must open Settings");
                Assert(h.Permissions.RequestCount == 0, "a denial must never re-prompt");
            });

            Run("onboarding: the shortcut row never gates the permissions step", () =>
            {
                var h = new OnboardingHarness();
                h.Permissions.Shortcut = new OnboardingShortcutState(
                    "Ctrl+Shift+Space", OnboardingShortcutStatus.Failed, "already in use by another app");
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Permissions);

                Assert(h.Flow.ShortcutStatus == OnboardingShortcutStatus.Failed, "precondition: registration failed");
                Assert(h.Flow.CanContinue, "the shortcut row is informational and must never gate");
            });

            // ----- Source branches --------------------------------------------

            Run("onboarding: the on-device branch gates on an installed model", () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);

                // Selecting the source pre-picks the recommended model.
                Assert(h.Flow.SelectedModel?.Id == FakeOnboardingCatalog.Parakeet.Id,
                    "the recommended model must be pre-picked");

                Assert(h.Flow.Advance(), "configure must be reachable");
                Assert(h.Flow.Step == OnboardingStep.Configure, "expected configure");
                Assert(h.Flow.CanContinue, "a picked model opens the configure gate");

                Assert(h.Flow.Advance(), "setup must be reachable");
                Assert(h.Flow.Step == OnboardingStep.Setup, "expected setup");
                Assert(!h.Flow.CanContinue, "nothing downloaded yet, so the setup gate is shut");

                h.Flow.StartSelectedModelDownload();
                Assert(h.Catalog.StartedDownloads.Count == 1
                    && h.Catalog.StartedDownloads[0] == FakeOnboardingCatalog.Parakeet.Id,
                    "the selected model must be the one downloaded");

                h.Catalog.Installed.Add(FakeOnboardingCatalog.Parakeet.Id);
                Assert(h.Flow.CanContinue, "an installed model opens the setup gate");
                Assert(h.Flow.StagedSource?.Model == FakeOnboardingCatalog.Parakeet.Id,
                    "the staged model must be the model id, verbatim");
                Assert(h.Flow.StagedSource?.CloudProvider is null, "on-device stages no cloud provider");
            });

            RunAsync("onboarding: the cloud branch needs a working key, not just typed text", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.LicenseKeyInput = "some-key";
                Assert(!h.Flow.CanContinue, "typed text alone must not open the gate");

                h.Flow.TestAccessKey();
                await h.LastTask;

                Assert(h.License.ProbedKeys.Count == 1 && h.License.ProbedKeys[0] == "some-key",
                    "the trimmed key must be the one probed");
                Assert(h.Flow.LicenseTestPassed == true, "the probe passed");
                Assert(h.Flow.CanContinue, "a passing probe opens the gate");

                // Editing the key invalidates the pass.
                h.Flow.LicenseKeyInput = "another-key";
                Assert(!h.Flow.KeyValidated, "an edit must clear the pass");
                Assert(!h.Flow.CanContinue, "an edit must shut the gate");
            });

            RunAsync("onboarding: the cloud setup gate opens only after activation", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.Flow.LicenseKeyInput = "key";
                h.Flow.TestAccessKey();
                await h.LastTask;

                h.AdvanceTo(OnboardingStep.Setup);
                Assert(!h.Flow.CanContinue, "a passing probe is not an activation");

                h.Flow.ActivateCloudLicense();
                await h.LastTask;

                Assert(h.License.ActivatedKeys.Count == 1 && h.License.ActivatedKeys[0] == "key",
                    "activation must use the typed key");
                Assert(h.Flow.CanContinue, "activation opens the setup gate");
                Assert(h.Flow.StagedSource?.CloudProvider == "hyperwhisper", "cloud stages the hyperwhisper provider");
                Assert(h.Flow.StagedSource?.PostProcessingMode == 1, "cloud stages post-processing on");
            });

            RunAsync("onboarding: a failed activation surfaces its error and keeps the gate closed", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.Flow.LicenseKeyInput = "key";
                h.License.ActivateOutcome = OnboardingLicenseOutcome.Failure("license expired");

                h.Flow.ActivateCloudLicense();
                await h.LastTask;

                Assert(h.Flow.SetupErrorMessage == "license expired", "the activation error must reach the one surface");
                Assert(!h.Flow.IsSelectedSourceUsable, "a failed activation leaves the source unusable");
            });

            RunAsync("onboarding: the provider branch stages the chosen provider", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.SelectProvider(CloudTranscriptionProvider.Groq);
                h.Flow.ApiKeyInput = "gsk-test";
                h.Flow.TestProviderKey();
                await h.LastTask;

                Assert(h.Flow.KeyValidated, "a healthy probe plus a stored key is a pass");
                Assert(h.Flow.StagedSource?.CloudProvider == CloudTranscriptionProvider.Groq.GetIdentifier(),
                    "the staged provider must be the chosen one");
                Assert(h.Flow.StagedSource?.Model == "cloud", "a cloud source stages the 'cloud' model");
                Assert(h.Flow.StagedSource?.PostProcessingMode == 0, "BYOK stages post-processing off");
            });

            // ----- Provider validation ----------------------------------------

            RunAsync("onboarding: an unauthorized probe never validates or persists", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "bad-key";
                h.ProviderKeys.Health = ProviderHealth.Unauthorized;

                h.Flow.TestProviderKey();
                await h.LastTask;

                Assert(!h.Flow.KeyValidated, "an unauthorized probe is not a pass");
                Assert(h.Flow.ProviderTestHealth == ProviderHealth.Unauthorized, "the health must be reported");
                Assert(h.ProviderKeys.Stored.Count == 0, "an unauthorized key must never be written");
            });

            RunAsync("onboarding: a healthy probe with a failed credential write is not a pass", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "good-key";
                h.ProviderKeys.Health = ProviderHealth.Healthy;
                h.ProviderKeys.PersistSucceeds = false;

                h.Flow.TestProviderKey();
                await h.LastTask;

                Assert(!h.Flow.KeyValidated, "a failed write is not a pass");
                Assert(h.Flow.ProviderTestHealth is null, "a failed write must not report a healthy provider");
                Assert(h.Flow.SetupErrorMessage == "credential store denied",
                    $"expected the store's own error, got {h.Flow.SetupErrorMessage ?? "null"}");
            });

            RunAsync("onboarding: returning to configure keeps the gate open for a stored provider key", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.ApiKeyInput = "sk-test";
                h.Flow.TestProviderKey();
                await h.LastTask;
                Assert(h.Flow.CanContinue, "precondition: a validated key opens the gate");

                Assert(h.Flow.Advance(), "setup must be reachable");
                Assert(h.Flow.Back(), "back to configure must work");
                // What the step does on every appearance, in either direction.
                h.Flow.ResetConfigureTestResults();

                Assert(!h.Flow.KeyValidated, "the inline result is cleared on every appearance");
                Assert(h.Flow.CanContinue, "an already validated key must keep the gate open");
            });

            Run("onboarding: an error from outside the flow is never shown", () =>
            {
                var h = new OnboardingHarness();
                // App-global, long-lived state produced by another screen entirely.
                h.ProviderKeys.ValidationError = "a credential failure from another screen";

                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                Assert(h.Flow.SetupErrorMessage is null, "the cloud branch must start clean");

                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                Assert(h.Flow.SetupErrorMessage is null, "the provider branch must start clean");
            });

            RunAsync("onboarding: changing provider clears the key and the validation", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "sk-openai";
                h.Flow.TestProviderKey();
                await h.LastTask;
                Assert(h.Flow.KeyValidated, "precondition: the key validated");

                h.Flow.SelectProvider(CloudTranscriptionProvider.Deepgram);

                Assert(h.Flow.ApiKeyInput.Length == 0, "a key typed for one provider must never carry over");
                Assert(!h.Flow.KeyValidated, "the pass belongs to the old provider");
                Assert(h.Flow.ProviderTestHealth is null, "the health belongs to the old provider");
            });

            // ----- Download failure surfacing (defect 2) -----------------------

            Run("onboarding: a Parakeet download failure is surfaced", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                h.Flow.SelectModel(FakeOnboardingCatalog.Parakeet);

                h.Catalog.PublishErrors(new OnboardingDownloadErrors(null, "Parakeet download failed"));

                Assert(h.Flow.SetupErrorMessage == "Parakeet download failed",
                    "a Parakeet failure must reach the one error surface");
            });

            Run("onboarding: a Whisper download failure is surfaced", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                h.Flow.SelectModel(FakeOnboardingCatalog.Whisper);

                h.Catalog.PublishErrors(new OnboardingDownloadErrors("Whisper download failed", null));

                Assert(h.Flow.SetupErrorMessage == "Whisper download failed",
                    "a Whisper failure must reach the one error surface");
            });

            Run("onboarding: the other engine's error is not attributed to the selected model", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                h.Flow.SelectModel(FakeOnboardingCatalog.Parakeet);

                h.Catalog.PublishErrors(new OnboardingDownloadErrors("stale whisper failure", null));

                Assert(h.Flow.SetupErrorMessage is null,
                    "a Whisper failure must not be shown against a Parakeet selection");
            });

            Run("onboarding: switching model repoints the error at its own engine", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                h.Catalog.PublishErrors(new OnboardingDownloadErrors("whisper failed", "parakeet failed"));

                h.Flow.SelectModel(FakeOnboardingCatalog.Whisper);
                Assert(h.Flow.SetupErrorMessage == "whisper failed", "expected the Whisper error");

                h.Flow.SelectModel(FakeOnboardingCatalog.Parakeet);
                Assert(h.Flow.SetupErrorMessage == "parakeet failed", "expected the Parakeet error");
            });

            // ----- Download progress invalidation (defect 2) -------------------

            Run("onboarding: a download tick invalidates the progress the setup step reads", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                h.Flow.SelectModel(FakeOnboardingCatalog.Parakeet);

                var invalidations = 0;
                System.ComponentModel.PropertyChangedEventHandler counter = (_, e) =>
                {
                    if (e.PropertyName == nameof(OnboardingFlowViewModel.SelectedModelProgress))
                        invalidations++;
                };

                h.Flow.PropertyChanged += counter;
                try
                {
                    h.Catalog.Downloading.Add(FakeOnboardingCatalog.Parakeet.Id);
                    h.Catalog.Progresses[FakeOnboardingCatalog.Parakeet.Id] = 0.42;
                    h.Catalog.PublishActivity();
                }
                finally
                {
                    h.Flow.PropertyChanged -= counter;
                }

                Assert(invalidations == 1, $"expected exactly one progress invalidation, got {invalidations}");
                Assert(Math.Abs(h.Flow.SelectedModelProgress - 0.42) < 0.0001,
                    "progress must be re-read from the catalog, not cached");
                Assert(h.Flow.IsSelectedModelDownloading, "the download state must be re-read too");
            });

            // ----- Microphone lifecycle ---------------------------------------

            Run("onboarding: System Default is the first device option", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();

                Assert(h.Flow.DeviceOptions.Count == h.Audio.Devices.Count + 1,
                    "the synthetic System Default row is always offered");
                Assert(h.Flow.DeviceOptions[0].IsSystemDefault, "System Default must be first");
                Assert(h.Flow.DeviceOptions[0].Name == OnboardingHarness.SystemDefaultName,
                    "the injected System Default name must be used");
                Assert(h.Flow.SelectedDeviceId.Length == 0, "nothing selected means the system default");
                Assert(h.Flow.SelectedDeviceName == OnboardingHarness.SystemDefaultName,
                    "the summary must name the system default");
            });

            Run("onboarding: entering and leaving the microphone step pairs the preview lifecycle", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();
                Assert(h.Audio.RefreshDeviceCalls == 1, "entry must refresh the device list");
                Assert(h.Audio.PreviewStarts == 1, "entry must start the meter");
                Assert(h.Audio.PreviewStops == 0, "entry must not stop the meter");
                Assert(h.Flow.IsLevelMeterActive, "the meter must report itself active");

                h.Flow.EndMicrophoneStep();
                Assert(h.Audio.PreviewStops == 1, "exit must stop the meter");
                Assert(!h.Flow.IsLevelMeterActive, "the meter must report itself inactive");
            });

            Run("onboarding: choosing a device repoints the meter and persists through the gateway", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();
                h.Flow.SelectDevice("usb");

                Assert(h.Audio.SelectedDeviceId == "usb", "the pick must reach the gateway");
                Assert(h.Audio.PreviewStarts == 2, "the meter must be re-pointed at the new device");
                Assert(h.Flow.SelectedDeviceName == "External USB Microphone", "the summary must follow the pick");

                h.Flow.SelectDevice(string.Empty);
                Assert(h.Audio.SelectedDeviceId is null, "an empty id means 'follow the system default'");
            });

            Run("onboarding: a device change on the microphone step refreshes the options", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);
                Assert(h.Flow.DeviceOptions.Any(d => d.Id == "usb"), "precondition: the USB mic is listed");

                h.Audio.Publish(new[] { new OnboardingInputDevice("builtin", "Realtek Microphone Array") });

                Assert(h.Flow.DeviceOptions.Count == 2, "the unplugged device must leave the list");
                Assert(!h.Flow.DeviceOptions.Any(d => d.Id == "usb"), "the unplugged device must not be offered");
            });

            Run("onboarding: device changes off the microphone step are ignored", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();
                var before = h.Flow.DeviceOptions.Count;

                // The flow is still on welcome, so the step owns nothing to refresh.
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());

                Assert(h.Flow.DeviceOptions.Count == before, "an off-step change must not rewrite the list");
            });

            Run("onboarding: selecting a disconnected device is ignored", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();

                h.Flow.SelectDevice("dock");

                Assert(h.Flow.SelectedDeviceId.Length == 0, "a phantom row must not be selected");
                Assert(h.Audio.SelectedDeviceId is null, "a phantom pick must not reach the gateway");
                Assert(h.Audio.StoredDeviceId is null, "a phantom pick must not be persisted");
                Assert(!h.Flow.HasPendingProductionWrite, "a change that never happened is not a pending write");
            });

            Run("onboarding: every exit path releases the microphone", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();
                h.Flow.DeferSetup();

                Assert(h.Audio.PreviewStops >= 1, "the meter must be stopped on exit");
                Assert(h.Audio.StopForExitCalls >= 1, "recording must be stopped on exit");
            });

            // ----- Try it step -------------------------------------------------

            Run("onboarding: transcript errors are detected by their sentinel", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.PublishTranscript("Error: no speech detected");
                Assert(h.Flow.TranscriptIsError, "the sentinel must be recognised");
                Assert(h.Flow.TranscriptBody == "no speech detected", "the sentinel must be stripped");

                h.Audio.PublishTranscript("Hello there");
                Assert(!h.Flow.TranscriptIsError, "a transcript is not an error");
                Assert(h.Flow.TranscriptBody == "Hello there", "a transcript passes through unchanged");
            });

            Run("onboarding: leaving the try it step stops recording and clears the transcript", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginTryItStep();
                h.Audio.PublishTranscript("Hello there");

                h.Flow.EndTryItStep();

                Assert(h.Audio.StopForExitCalls == 1, "leaving must release the microphone");
                Assert(h.Flow.Transcript.Length == 0, "leaving must clear the transcript");
            });

            // ----- Set Up Later rollback (defect 1) ----------------------------

            Run("onboarding: Set Up Later after reaching Try It leaves production state untouched", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                // Reaching Try It is the one place the staged source is applied,
                // because the test recording has to run through it.
                Assert(h.Committer.Applied.Count == 1, "the staged source is applied exactly once");
                Assert(h.Committer.ProductionState != FakeOnboardingCommitter.Seed, "precondition: it was written");

                h.Flow.DeferSetup();

                Assert(h.Committer.RestoreCount == 1, "deferral must restore exactly once");
                Assert(h.Committer.ProductionState == FakeOnboardingCommitter.Seed, "the write must be undone");
                Assert(h.Committer.MarkCompletedCount == 1, "deferral still closes the flow");
                Assert(!h.Flow.HasPendingProductionWrite, "nothing is left to roll back");
            });

            Run("onboarding: Set Up Later before any write never touches production state", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);

                Assert(h.Committer.Applied.Count == 0, "nothing is applied before Try It");

                h.Flow.DeferSetup();

                Assert(h.Committer.Applied.Count == 0, "deferral must not apply anything");
                Assert(h.Committer.RestoreCount == 0, "there is nothing to restore");
                Assert(h.Committer.ProductionState == FakeOnboardingCommitter.Seed, "production state is untouched");
            });

            RunAsync("onboarding: staging a source never writes on its own", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                h.Flow.ApiKeyInput = "sk-test";
                h.Flow.TestProviderKey();
                await h.LastTask;

                Assert(h.Flow.StagedSource is not null, "the configuration is staged");
                Assert(h.Committer.Applied.Count == 0, "staging alone applies nothing");
                Assert(h.Committer.ProductionState == FakeOnboardingCommitter.Seed, "production state is untouched");
            });

            RunAsync("onboarding: testing a key then deferring restores the previous provider key", async () =>
            {
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "sk-original";
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "sk-temporary";
                h.Flow.TestProviderKey();
                await h.LastTask;

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "sk-temporary",
                    "the tested key has to be written before it can be trusted");
                Assert(h.Flow.HasPendingProductionWrite, "that write is a pending production write");

                h.Flow.DeferSetup();

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "sk-original",
                    "deferral must put the user's own key back");
                Assert(!h.Flow.HasPendingProductionWrite, "nothing is left to roll back");
            });

            RunAsync("onboarding: only the key present before onboarding is restored", async () =>
            {
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "sk-original";
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);

                h.Flow.ApiKeyInput = "sk-first";
                h.Flow.TestProviderKey();
                await h.LastTask;
                h.Flow.ApiKeyInput = "sk-second";
                h.Flow.TestProviderKey();
                await h.LastTask;
                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "sk-second",
                    "precondition: the second test overwrote the first");

                h.Flow.DeferSetup();

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "sk-original",
                    "repeated tests must still roll back to the pre-onboarding key");
            });

            RunAsync("onboarding: deferring removes a key that did not exist before the flow", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "sk-new";
                h.Flow.TestProviderKey();
                await h.LastTask;
                Assert(h.ProviderKeys.HasKey(CloudTranscriptionProvider.OpenAI), "precondition: a key was written");

                h.Flow.DeferSetup();

                Assert(!h.ProviderKeys.HasKey(CloudTranscriptionProvider.OpenAI),
                    "'no key' must round-trip as a delete");
            });

            RunAsync("onboarding: completing keeps the provider key it wrote", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "sk-new";
                h.Flow.TestProviderKey();
                await h.LastTask;

                h.Flow.Complete();

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "sk-new",
                    "completion keeps every write it made");
            });

            Run("onboarding: deferring restores the previous input device", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.SelectedDeviceId = "builtin";
                h.Audio.StoredDeviceId = "builtin";
                h.Flow.BeginMicrophoneStep();
                h.Flow.SelectDevice("usb");
                Assert(h.Audio.SelectedDeviceId == "usb", "precondition: the pick was applied");
                Assert(h.Flow.HasPendingProductionWrite, "the device write is a pending production write");

                h.Flow.DeferSetup();

                Assert(h.Audio.SelectedDeviceId == "builtin", "the open device must go back");
                Assert(h.Audio.StoredDeviceId == "builtin", "the stored preference must go back");
            });

            Run("onboarding: deferring restores a stored device that is not currently connected", () =>
            {
                var h = new OnboardingHarness();
                // Remembered: a dock mic that is not connected, so nothing is open.
                h.Audio.StoredDeviceId = "dock";
                h.Audio.SelectedDeviceId = null;
                h.Flow.BeginMicrophoneStep();
                h.Flow.SelectDevice("usb");
                Assert(h.Audio.StoredDeviceId == "usb" && h.Audio.SelectedDeviceId == "usb",
                    "precondition: selecting writes both");

                h.Flow.DeferSetup();

                Assert(h.Audio.StoredDeviceId == "dock",
                    "the preference survives even though the device it names is absent");
                Assert(h.Audio.SelectedDeviceId is null, "nothing is left open, which is where the flow found it");
            });

            Run("onboarding: deferring restores the system default input device", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();
                h.Flow.SelectDevice("usb");

                h.Flow.DeferSetup();

                Assert(h.Audio.SelectedDeviceId is null, "null is a real value here, not 'nothing captured'");
            });

            Run("onboarding: completing keeps the chosen input device", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.BeginMicrophoneStep();
                h.Flow.SelectDevice("usb");

                h.Flow.Complete();

                Assert(h.Audio.SelectedDeviceId == "usb", "completion keeps the pick");
            });

            Run("onboarding: deferring is idempotent and cannot write afterwards", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                h.Flow.DeferSetup();
                h.Flow.DeferSetup();
                h.Flow.Complete();

                Assert(h.Committer.RestoreCount == 1, "the rollback must happen exactly once");
                Assert(h.Committer.MarkCompletedCount == 1, "the flow closes exactly once");
                Assert(h.Committer.ProductionState == FakeOnboardingCommitter.Seed,
                    "a Complete() after deferral must not resurrect the write");
            });

            // ----- Completion commit -------------------------------------------

            Run("onboarding: completing commits the staged source", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Done);

                h.Flow.Complete();

                var applied = h.Committer.Applied[^1];
                Assert(applied.Source == OnboardingSourceKind.OnDevice, "the on-device source must be committed");
                Assert(applied.Model == FakeOnboardingCatalog.Parakeet.Id, "the selected model must be committed");
                Assert(h.Committer.ProductionState.Contains(FakeOnboardingCatalog.Parakeet.Id),
                    "production state must carry the model");
                Assert(h.Committer.RestoreCount == 0, "completion never restores");
                Assert(h.Committer.MarkCompletedCount == 1, "onboarding is marked done exactly once");
                Assert(h.Committer.ReturnHomeCount == 1, "the shell is returned to home exactly once");
                Assert(!h.Flow.HasPendingProductionWrite, "nothing is left to roll back");
                Assert(!h.Flow.IsLiveForTesting, "the commit boundary is closed");
            });

            Run("onboarding: completing without a source still closes the flow cleanly", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.Complete();

                Assert(h.Committer.Applied.Count == 0, "there is nothing to apply");
                Assert(h.Committer.ProductionState == FakeOnboardingCommitter.Seed, "production state is untouched");
                Assert(h.Committer.MarkCompletedCount == 1, "the flow still closes");
            });

            // ----- Late async completion (defect 3) ----------------------------

            RunAsync("onboarding: an activation that lands after dismissal cannot write flow state", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.Flow.LicenseKeyInput = "key";
                h.License.GateActivation = true;

                h.Flow.ActivateCloudLicense();
                var task = h.LastTask;
                Assert(!task.IsCompleted, "precondition: the activation is parked on the gate");

                h.Flow.DeferSetup();
                Assert(!h.Flow.IsLiveForTesting, "deferral closes the commit boundary first");
                Assert(!h.Flow.HasInFlightWorkForTesting, "deferral empties the task box");

                // The network call now lands, long after the window closed.
                h.License.Release();
                await task;

                Assert(!h.Flow.KeyValidated, "a late activation must not validate the key");
                Assert(h.Flow.SetupErrorMessage is null, "a late activation must not write an error");
                Assert(h.Committer.ProductionState == FakeOnboardingCommitter.Seed,
                    "a late activation must not write production state");
            });

            RunAsync("onboarding: a stale licence probe result is discarded", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.License.GateProbe = true;
                h.Flow.LicenseKeyInput = "first-key";

                h.Flow.TestAccessKey();
                var task = h.LastTask;
                // The user edits the key before the probe result is consumed.
                h.Flow.LicenseKeyInput = "second-key";
                h.License.Release();
                await task;

                Assert(!h.Flow.KeyValidated, "a result for an abandoned key must not open the gate");
                Assert(h.Flow.LicenseTestPassed is null, "a result for an abandoned key must not be shown");
                Assert(!h.Flow.IsTestingKey, "the spinner must stop even for a discarded result");
            });

            RunAsync("onboarding: a stale provider key probe is never persisted", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.ProviderKeys.GateProbe = true;
                h.Flow.ApiKeyInput = "sk-abandoned";

                h.Flow.TestProviderKey();
                var task = h.LastTask;
                // The staleness check has to run BEFORE the persist, or an abandoned
                // probe writes the credential store and flags a pending production
                // write for a key nobody kept.
                h.Flow.ApiKeyInput = "sk-current";
                h.ProviderKeys.Release();
                await task;

                Assert(h.ProviderKeys.Stored.Count == 0, "an abandoned probe must never write");
                Assert(!h.Flow.KeyValidated, "an abandoned probe is not a pass");
                Assert(!h.Flow.HasPendingProductionWrite, "an abandoned probe sets no restore point");
                Assert(h.ProviderKeys.ProbeCount == 1, "exactly one probe ran");
            });

            RunAsync("onboarding: switching provider mid-probe discards the persist", async () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                h.ProviderKeys.GateProbe = true;
                h.Flow.ApiKeyInput = "sk-openai";

                h.Flow.TestProviderKey();
                var task = h.LastTask;
                h.Flow.SelectProvider(CloudTranscriptionProvider.Deepgram);
                h.ProviderKeys.Release();
                await task;

                Assert(h.ProviderKeys.Stored.Count == 0, "the probed key must never land under another provider");
                Assert(!h.Flow.KeyValidated, "the pass belonged to the abandoned provider");
                Assert(!h.Flow.HasPendingProductionWrite, "no restore point may be set");
            });

            // ----- Per-session validation records ------------------------------

            Run("onboarding: a stored but never probed key keeps both gates shut", () =>
            {
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "sk-preexisting";
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.AdvanceTo(OnboardingStep.Configure);

                Assert(!h.Flow.CanContinue, "an unprobed key must not open the configure gate");
                Assert(!h.Flow.IsSelectedSourceUsable, "an unprobed key must not read as usable");
            });

            RunAsync("onboarding: a validated key survives back navigation on the setup gate", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.ApiKeyInput = "sk-test";
                h.Flow.TestProviderKey();
                await h.LastTask;
                Assert(h.Flow.IsSelectedSourceUsable, "precondition: probed and stored");

                Assert(h.Flow.Advance(), "setup must be reachable");
                Assert(h.Flow.Back(), "back to configure must work");
                h.Flow.ResetConfigureTestResults();

                Assert(!h.Flow.KeyValidated, "the inline result is cleared on every appearance");
                Assert(h.Flow.CanContinue, "the per-session record keeps the configure gate open");
                Assert(h.Flow.IsSelectedSourceUsable, "the per-session record keeps the setup gate open");
            });

            RunAsync("onboarding: validation is remembered per provider, not globally", async () =>
            {
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.Deepgram] = "dg-preexisting";
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.SelectProvider(CloudTranscriptionProvider.Groq);
                h.Flow.ApiKeyInput = "gsk-test";
                h.Flow.TestProviderKey();
                await h.LastTask;
                Assert(h.Flow.CanContinue, "precondition: Groq validated");

                // Deepgram's stored key was never probed this session.
                h.Flow.SelectProvider(CloudTranscriptionProvider.Deepgram);
                Assert(!h.Flow.CanContinue, "another provider's pass must not carry over");
                Assert(!h.Flow.IsSelectedSourceUsable, "an unprobed stored key is not usable");

                // Groq's record survives the round trip.
                h.Flow.SelectProvider(CloudTranscriptionProvider.Groq);
                Assert(h.Flow.CanContinue, "the validated provider's record survives");
            });

            RunAsync("onboarding: returning to configure keeps the cloud gate open for the tested key", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.LicenseKeyInput = "hw-key";
                h.Flow.TestAccessKey();
                await h.LastTask;
                Assert(h.Flow.CanContinue, "precondition: the probe passed");

                Assert(h.Flow.Advance(), "setup must be reachable");
                Assert(h.Flow.Back(), "back to configure must work");
                h.Flow.ResetConfigureTestResults();

                Assert(!h.Flow.KeyValidated, "the inline result is cleared on every appearance");
                Assert(h.Flow.CanContinue, "the field still holds the exact key that passed");
            });

            RunAsync("onboarding: editing the remembered key closes the gate until it matches again", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.LicenseKeyInput = "hw-key";
                h.Flow.TestAccessKey();
                await h.LastTask;
                h.Flow.ResetConfigureTestResults();
                Assert(h.Flow.CanContinue, "precondition: the remembered key holds the gate open");

                h.Flow.LicenseKeyInput = "hw-key-edited";
                Assert(!h.Flow.CanContinue, "an edit must shut the gate");

                // Retyping the validated key reopens it without another probe.
                h.Flow.LicenseKeyInput = "hw-key";
                Assert(h.Flow.CanContinue, "the exact validated key reopens the gate");
                Assert(h.License.ProbedKeys.Count == 1, "no second probe may run");
            });

            RunAsync("onboarding: a failed re-probe of the remembered key closes the gate", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.AdvanceTo(OnboardingStep.Configure);

                h.Flow.LicenseKeyInput = "hw-key";
                h.Flow.TestAccessKey();
                await h.LastTask;
                Assert(h.Flow.CanContinue, "precondition: the probe passed");

                // The key is revoked server side; a re-probe of the SAME key fails.
                h.License.ProbeOutcome = OnboardingLicenseOutcome.Failure("revoked");
                h.Flow.TestAccessKey();
                await h.LastTask;

                Assert(!h.Flow.CanContinue, "a failing re-probe must shut the gate");
                h.Flow.ResetConfigureTestResults();
                Assert(!h.Flow.CanContinue, "a revoked key must not stay remembered");
            });

            // ----- The shortcut row (Windows-only) -----------------------------
            //
            // macOS's second permission row is Accessibility, a Bool. Windows needs
            // no such grant, but registering the global hotkey genuinely fails
            // (Win32 1409/1413), so the row has three renderings and a sentence.

            Run("onboarding: a registered shortcut renders as registered with no reason", () =>
            {
                var h = new OnboardingHarness();
                h.Permissions.Publish(new OnboardingShortcutState(
                    "Ctrl+Shift+Space", OnboardingShortcutStatus.Registered, null));

                Assert(h.Flow.ShortcutDisplay == "Ctrl+Shift+Space", "the formatted shortcut must reach the row");
                Assert(h.Flow.ShortcutStatus == OnboardingShortcutStatus.Registered, "expected Registered");
                Assert(h.Flow.ShortcutFailureReason is null, "a registered shortcut has nothing to explain");
            });

            Run("onboarding: a failed registration carries its reason verbatim", () =>
            {
                var h = new OnboardingHarness();
                h.Permissions.Publish(new OnboardingShortcutState(
                    "Ctrl+Shift+Space",
                    OnboardingShortcutStatus.Failed,
                    "This shortcut is already in use by another app."));

                Assert(h.Flow.ShortcutStatus == OnboardingShortcutStatus.Failed, "expected Failed");
                Assert(h.Flow.ShortcutFailureReason == "This shortcut is already in use by another app.",
                    "the adapter's sentence must be shown verbatim, not re-worded");

                // The way out of a conflict is recorded ON this step, not deep-linked
                // into a settings page behind an application-modal window.
                Assert(h.Flow.ApplyToggleShortcut("Ctrl+Alt+J"),
                    "the seam accepted the chord, so the flow must report it stored");
                Assert(h.Permissions.StoredToggleShortcuts.Count == 1
                       && h.Permissions.StoredToggleShortcuts[0] == "Ctrl+Alt+J",
                    "the recorded chord must reach the seam verbatim");
                Assert(h.Permissions.ShortcutRefreshes >= 1,
                    "storing it must re-check the registration, or the row still shows the failure");
                Assert(h.Flow.ShortcutStatus == OnboardingShortcutStatus.Registered,
                    "the row has to follow the new shortcut, not stay on the old failure");
                Assert(h.Flow.ShortcutDisplay == "Ctrl+Alt+J",
                    $"the row must show the new chord; showed '{h.Flow.ShortcutDisplay}'");
            });

            Run("onboarding: a refused shortcut write still leaves the row honest", () =>
            {
                var h = new OnboardingHarness();
                h.Permissions.Publish(new OnboardingShortcutState(
                    "Ctrl+Shift+Space", OnboardingShortcutStatus.Failed, "in use by another app"));
                h.Permissions.RefuseShortcutWrite = true;

                Assert(!h.Flow.ApplyToggleShortcut("Ctrl+Alt+J"),
                    "a refused write must be reported as refused, not swallowed");
                Assert(h.Permissions.StoredToggleShortcuts.Count == 0, "nothing was stored");
                Assert(h.Permissions.ShortcutRefreshes >= 1,
                    "refresh anyway: the row must show what IS configured, not what was typed");
                Assert(h.Flow.ShortcutDisplay == "Ctrl+Shift+Space",
                    "a refused write must leave the old shortcut on screen");

                // Empty never reaches the seam at all.
                Assert(!h.Flow.ApplyToggleShortcut("   "), "blank is not a shortcut");
                Assert(h.Permissions.StoredToggleShortcuts.Count == 0, "blank must not reach the seam");
            });

            Run("onboarding: an unknown shortcut registration is not a failure", () =>
            {
                var h = new OnboardingHarness();
                // What the adapter reports before the main window has an HWND.
                h.Permissions.Publish(new OnboardingShortcutState(
                    "Ctrl+Shift+Space", OnboardingShortcutStatus.Unknown, null));

                Assert(h.Flow.ShortcutStatus == OnboardingShortcutStatus.Unknown, "expected Unknown");
                Assert(h.Flow.ShortcutFailureReason is null, "Unknown must never render as an error");

                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Permissions);
                Assert(h.Flow.CanContinue, "an unknown registration must never gate");
            });

            // ----- Credits (Windows-only seam) ---------------------------------

            Run("onboarding: the credits figure reads as unknown until it arrives", () =>
            {
                var h = new OnboardingHarness();
                Assert(!h.Flow.HasCredits, "nothing has been fetched yet");
                Assert(h.Flow.CreditsFormatted == "…", "an unknown balance renders as an ellipsis, never a zero");

                h.Credits.Publish(new OnboardingCloudCredits(1240.5, 310, "1,240"));

                Assert(h.Flow.HasCredits, "the balance arrived");
                Assert(h.Flow.CreditsFormatted == "1,240", "the gateway's own formatting is shown");
            });

            Run("onboarding: the credits figure never gates the flow", () =>
            {
                var h = new OnboardingHarness();
                h.License.IsActive = true;
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.AdvanceTo(OnboardingStep.Setup);

                Assert(h.Credits.RefreshCount >= 1, "entering a cloud step must fetch the balance");
                Assert(!h.Flow.HasCredits, "precondition: the fake landed no balance");
                Assert(h.Flow.CanContinue, "an active licence opens the gate whatever the balance says");
            });

            RunAsync("onboarding: a failed credits fetch is not a setup error", async () =>
            {
                var h = new OnboardingHarness();
                h.License.IsActive = true;
                h.Credits.ThrowOnRefresh = true;
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.AdvanceTo(OnboardingStep.Configure);
                await h.LastTask;

                Assert(h.Flow.SetupErrorMessage is null, "a credits failure must never reach the setup error surface");
                Assert(!h.Flow.HasCredits, "the balance simply stays unknown");
                Assert(h.Flow.CreditsFormatted == "…", "an unknown balance renders as an ellipsis");
                Assert(h.Flow.CanContinue, "a credits failure must never close the gate");
            });

            RunAsync("onboarding: a successful activation refreshes the balance", async () =>
            {
                // StepDidChange fetches on entry to Configure and to Setup, and BOTH of
                // those run while a first-run machine is still unlicensed - so every
                // fetch came back unknown and nothing ever asked again. "Credits
                // confirmed" stayed unticked on a good key.
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.Flow.LicenseKeyInput = "HW-GOOD";

                h.Credits.NextCredits = new OnboardingCloudCredits(66950, 10627, "$66.95 remaining");
                var before = h.Credits.RefreshCount;

                h.Flow.ActivateCloudLicense();
                var task = h.LastTask;
                await task;

                Assert(h.Credits.RefreshCount > before, "activation must ask for the balance again");
                Assert(h.Flow.HasCredits, "and the balance must land on the flow");
                Assert(h.Flow.AreCreditsConfirmed, "so the Credits confirmed row ticks");
            });

            Run("onboarding: the Done summary shows the credit count, not the balance line", () =>
            {
                // The format is "{0} · {1} credits", so {1} is a COUNT. Passing
                // CreditsFormatted rendered "HyperWhisper Cloud · $66.95 remaining
                // (~10627 minutes) credits".
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.Credits.Publish(new OnboardingCloudCredits(66950, 10627, "$66.95 remaining (~10627 minutes)"));
                h.Flow.RefreshCredits(force: false);

                Assert(h.Flow.CreditsCountFormatted == 66950d.ToString("N0", CultureInfo.CurrentCulture),
                    $"unexpected count rendering '{h.Flow.CreditsCountFormatted}'");
                Assert(!h.Flow.SourceSummary.Contains("remaining"),
                    $"the balance line must not reach the summary: '{h.Flow.SourceSummary}'");
                Assert(h.Flow.SourceSummary.Contains(h.Flow.CreditsCountFormatted),
                    $"the count must reach the summary: '{h.Flow.SourceSummary}'");
            });

            // ----- Device availability (Windows-only) --------------------------

            Run("onboarding: a blocked microphone runs no preview and reports an inactive meter", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.Blocked;
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);

                Assert(h.Flow.DeviceAvailability == OnboardingDeviceAvailability.Blocked, "expected Blocked");
                Assert(h.Audio.PreviewStarts == 0, "a blocked microphone must not be opened");
                Assert(!h.Flow.IsLevelMeterActive, "the meter must render its explicit inactive state");
                Assert(h.Flow.CanContinue, "the microphone step never gates");
            });

            Run("onboarding: with no device there is no write, so Set Up Later has nothing to undo", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);

                Assert(h.Flow.DeviceAvailability == OnboardingDeviceAvailability.NoDevices, "expected NoDevices");

                h.Flow.SelectDevice("usb");

                Assert(h.Audio.SelectedDeviceId is null, "no device may be opened");
                Assert(h.Audio.StoredDeviceId is null, "no preference may be written");
                Assert(!h.Flow.HasPendingProductionWrite, "and therefore nothing is pending");
                Assert(h.Flow.CanContinue, "the microphone step never gates");
            });

            Run("onboarding: an enumeration failure stays distinct from having no devices", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.EnumerationFailed;
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);

                Assert(h.Flow.DeviceAvailability == OnboardingDeviceAvailability.EnumerationFailed,
                    "a broken audio stack must not be reported as 'buy a microphone'");
                Assert(!h.Flow.HasUsableMicrophone, "nothing is usable");
                Assert(h.Audio.PreviewStarts == 0, "no preview may run");
                Assert(h.Flow.CanContinue, "the microphone step never gates");
            });

            Run("onboarding: plugging a microphone in while the step is open recovers live", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);
                Assert(h.Flow.DeviceOptions.Count == 1, "precondition: only the synthetic row is offered");

                h.Audio.Availability = OnboardingDeviceAvailability.Available;
                h.Audio.Publish(FakeOnboardingAudio.ConnectedDevices);

                Assert(h.Flow.DeviceAvailability == OnboardingDeviceAvailability.Available, "expected Available");
                Assert(h.Flow.DeviceOptions.Count == 3, "the list must refill without leaving the step");
            });

            // ----- Sample clip (Windows-only) ----------------------------------

            Run("onboarding: Try It offers the sample clip only when there is nothing to record with", () =>
            {
                var withoutMic = new OnboardingHarness();
                withoutMic.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                withoutMic.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                withoutMic.StageInstalledOnDeviceModel();
                withoutMic.AdvanceTo(OnboardingStep.TryIt);

                Assert(withoutMic.Flow.TryItMode == OnboardingTryItMode.Sample,
                    "a Record button whose only outcome is an error must not be offered");

                var withMic = new OnboardingHarness();
                withMic.StageInstalledOnDeviceModel();
                withMic.AdvanceTo(OnboardingStep.TryIt);

                Assert(withMic.Flow.TryItMode == OnboardingTryItMode.Record,
                    "a working microphone still gets the recording path");
            });

            RunAsync("onboarding: the sample transcript arrives and is flagged as a sample", async () =>
            {
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                h.Flow.TranscribeSampleClip();
                await h.LastTask;

                Assert(h.Audio.SampleTranscriptions == 1, "the clip must go through the transcription path once");
                Assert(h.Flow.Transcript == h.Audio.SampleTranscript, "the result lands on the transcript channel");
                Assert(!h.Flow.TranscriptIsError, "a successful sample is not an error");
                Assert(h.Flow.TranscriptCameFromSample, "the copy must be able to say which of the two happened");
                Assert(!h.Flow.IsTranscribingSample, "the busy flag must clear");
            });

            Run("onboarding: the Done summary says 'none connected' rather than showing a tick", () =>
            {
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                h.Audio.Publish(Array.Empty<OnboardingInputDevice>());
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Done);

                Assert(h.Flow.MicrophoneSummary == HyperWhisper.Localization.Loc.S("onboarding.done.mic.noneConnected"),
                    "the summary must be honest about there being no device");
                Assert(h.Flow.MicrophoneSummary != OnboardingHarness.SystemDefaultName,
                    "it must not claim the system default is in use");
            });

            // ----- Fuzz round regressions (F1-F6) ------------------------------
            // Six defects found by 4,000 step-gated model walks and 12 GUI walks
            // over the flow. Each case is the fuzzer's own minimal reproduction,
            // made permanent: a fuzz finding with no committed test comes back.
            //
            // F1/F5 and half of F3 were ONE defect - an async validation result
            // applied without being scoped to the thing it tested - so they are
            // fixed by one mechanism (ValidationScope) and pinned by four cases
            // rather than by three staleness checks that disagree.

            RunAsync("onboarding F1: a licence probe landing after a source change cannot open the BYOK gate", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                h.Flow.Advance();                                   // -> Configure (cloud)

                h.Flow.LicenseKeyInput = "HW-CLOUD-KEY-0001";
                h.License.GateProbe = true;
                h.Flow.TestAccessKey();                             // parked: a slow network
                var task = h.LastTask;

                // The user backs out and picks the OTHER cloud branch while the
                // probe is still in the air.
                Assert(h.Flow.Back(), "back to source must work");
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                Assert(h.Flow.Advance(), "configure must be reachable for BYOK");

                h.License.Release();
                await task;

                Assert(!h.Flow.KeyValidated, "a licence pass is not a BYOK pass");
                Assert(!h.Flow.CanContinue,
                    "the BYOK gate must stay shut with an empty API key field, whatever the licence probe said");
                Assert(h.Flow.ApiKeyInput.Length == 0, "precondition: nothing was typed for the provider");
                Assert(h.Flow.ProviderTestHealth is null, "no provider was ever probed");
                Assert(!h.Flow.IsTestingKey, "the spinner must not be left running");
            });

            RunAsync("onboarding F1: a provider probe landing after a source change cannot open the Cloud gate", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.Advance();                                   // -> Configure (BYOK)

                h.Flow.ApiKeyInput = "sk-byok-0001";
                h.ProviderKeys.GateProbe = true;
                h.Flow.TestProviderKey();
                var task = h.LastTask;

                Assert(h.Flow.Back(), "back to source must work");
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                Assert(h.Flow.Advance(), "configure must be reachable for cloud");

                h.ProviderKeys.Release();
                await task;

                Assert(!h.Flow.KeyValidated, "a BYOK pass is not a licence pass");
                Assert(!h.Flow.CanContinue,
                    "the Cloud gate must stay shut with an empty licence field");
                Assert(h.ProviderKeys.Stored.Count == 0,
                    "a probe abandoned by a source change must never write the credential store");
                Assert(!h.Flow.HasPendingProductionWrite, "and must never set a restore point");
            });

            Run("onboarding F2: Advance and Back do nothing once the flow has finished", () =>
            {
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);

                h.Flow.DeferSetup();                                // rolls back, marks complete
                var stateAfterRollback = h.Committer.ProductionState;
                var appliesAfterRollback = h.Committer.Applied.Count;
                Assert(!h.Flow.IsLiveForTesting, "precondition: deferral closed the commit boundary");

                // Stepping INTO Try It writes the default Mode. On a finished flow
                // that silently undid the rollback MarkOnboardingCompleted() had
                // just been paired with.
                Assert(!h.Flow.Advance(), "a finished flow must refuse to advance");
                Assert(h.Flow.Step == OnboardingStep.Microphone, "a refused advance must not move the step");
                Assert(!h.Flow.Back(), "a finished flow must refuse to go back");
                Assert(h.Flow.Step == OnboardingStep.Microphone, "a refused back must not move the step");

                Assert(h.Committer.ProductionState == stateAfterRollback,
                    $"navigation on a dead flow rewrote production state: {h.Committer.ProductionState}");
                Assert(h.Committer.Applied.Count == appliesAfterRollback,
                    "navigation on a dead flow re-applied the staged source");
                Assert(h.Committer.MarkCompletedCount == 1, "and must not mark completion twice");
            });

            RunAsync("onboarding F3: Save API key on the Setup step probes before it writes", async () =>
            {
                // The dead end: it captured a restore point and persisted with NO
                // probe, recorded nothing in the per-session table, and so could
                // never make IsSelectedSourceUsable true - while having already
                // overwritten the user's real Credential Manager entry. The button
                // renders exactly when that property is false.
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "PRE-EXISTING-KEY";
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.ProviderKeys.Health = ProviderHealth.Unauthorized;

                h.Flow.ApiKeyInput = "sk-never-probed";
                h.Flow.SaveProviderKey();
                await h.LastTask;

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "PRE-EXISTING-KEY",
                    "a rejected key must never overwrite the stored credential");
                Assert(!h.Flow.IsSelectedSourceUsable, "a rejected key is not a usable source");
                Assert(h.Flow.HasSetupError, "and the step must say why, rather than failing in silence");

                // And the other half: when the probe passes, Save is no longer a
                // dead end - it opens the very gate its button is shown for.
                var h2 = new OnboardingHarness();
                h2.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "PRE-EXISTING-KEY";
                h2.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h2.ProviderKeys.Health = ProviderHealth.Healthy;

                h2.Flow.ApiKeyInput = "sk-probed";
                h2.Flow.SaveProviderKey();
                await h2.LastTask;

                Assert(h2.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "sk-probed",
                    "a key that passed must be written");
                Assert(h2.Flow.IsSelectedSourceUsable, "and must open the gate the Save button is shown for");
                Assert(h2.Flow.HasPendingProductionWrite,
                    "the overwrite is reversible, so it has to be recorded as pending");
            });

            Run("onboarding F3: Save API key writes nothing once the flow has finished", () =>
            {
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "PRE-EXISTING-KEY";
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.ApiKeyInput = "sk-late";
                h.Flow.AbandonSetup();

                h.Flow.SaveProviderKey();

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.OpenAI) == "PRE-EXISTING-KEY",
                    "a dead flow must not touch the credential store");
                Assert(!h.Flow.HasInFlightWorkForTesting, "and must not start a probe");
            });

            RunAsync("onboarding F4: a throwing activation clears the spinner and stays retryable", async () =>
            {
                var thrower = new OnboardingHarness();
                var flow = new OnboardingFlowViewModel(
                    thrower.Permissions,
                    thrower.Catalog,
                    new ThrowingOnboardingLicense(thrower.License, probeThrows: false, activateThrows: true),
                    thrower.Credits,
                    thrower.ProviderKeys,
                    thrower.Audio,
                    thrower.Committer,
                    OnboardingHarness.SystemDefaultName);

                thrower.Permissions.MicrophoneAuthorization = OnboardingMicrophoneAuthorization.Authorized;
                flow.RefreshPermissions();
                Assert(flow.Advance() && flow.Advance(), "welcome and permissions must pass");
                flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                Assert(flow.Advance(), "configure must be reachable");
                flow.LicenseKeyInput = "HW-KEY";
                flow.TestAccessKey();
                await flow.LastAsyncTaskForTesting!;
                Assert(flow.Advance(), "setup must be reachable once the key probes clean");

                flow.ActivateCloudLicense();
                await flow.LastAsyncTaskForTesting!;

                // ActivateCloudLicenseCoreAsync caught only OperationCanceledException,
                // so anything else escaped the un-awaited task and left the button
                // reading "Activating…" and disabled for good - with Continue gated
                // on an activation that could never be retried.
                Assert(!flow.IsActivatingLicense, "a throwing activation must not strand the spinner");
                Assert(flow.CanActivateLicense, "the button must be pressable again");
                Assert(flow.HasSetupError, "and the failure must be visible rather than swallowed");
            });

            RunAsync("onboarding F4: a throwing provider probe clears the spinner", async () =>
            {
                // The sibling. It was already recoverable by leaving and re-entering
                // the Configure step; it is now cleared where it is raised, on the
                // same terms as the activation spinner.
                var h = new OnboardingHarness();
                var flow = new OnboardingFlowViewModel(
                    h.Permissions,
                    h.Catalog,
                    h.License,
                    h.Credits,
                    new ThrowingOnboardingProviderKeys(h.ProviderKeys),
                    h.Audio,
                    h.Committer,
                    OnboardingHarness.SystemDefaultName);

                flow.SelectSource(OnboardingSourceKind.YourProvider);
                flow.ApiKeyInput = "sk-throws";
                flow.TestProviderKey();
                await flow.LastAsyncTaskForTesting!;

                Assert(!flow.IsTestingKey, "a throwing probe must not strand the spinner");
                Assert(flow.CanTestProviderKey, "the button must be pressable again");
                Assert(!flow.KeyValidated, "a throw is not a pass");
                Assert(h.ProviderKeys.Stored.Count == 0, "and must never reach the persist");
            });

            RunAsync("onboarding F5: an activation result is never shown under a superseded key", async () =>
            {
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                Assert(h.Flow.Advance(), "configure must be reachable");

                h.Flow.LicenseKeyInput = "KEY-A";
                h.Flow.TestAccessKey();
                await h.LastTask;
                Assert(h.Flow.Advance(), "setup must be reachable");

                h.License.GateActivation = true;
                h.License.ActivateOutcome = OnboardingLicenseOutcome.Failure("Activation limit reached.");
                h.Flow.ActivateCloudLicense();                      // parked, activating KEY-A
                var task = h.LastTask;

                h.Flow.LicenseKeyInput = "KEY-B";                   // the user retypes
                h.License.ReleaseActivation();
                await task;

                Assert(h.License.ActivatedKeys.Count == 1 && h.License.ActivatedKeys[0] == "KEY-A",
                    "exactly one activation, for the key that was on screen when it started");
                Assert(!h.Flow.HasSetupError,
                    $"KEY-A's failure must not be shown under KEY-B: '{h.Flow.SetupErrorMessage}'");
                Assert(!h.Flow.IsActivatingLicense, "the spinner must still stop");
            });

            Run("onboarding F6: the masked API key never splits a surrogate pair", () =>
            {
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);

                // Astral characters sit exactly on both slice boundaries: key[..8]
                // used to cut the first one in half and key[^4..] the second, so the
                // row rendered lone surrogates as replacement glyphs.
                h.Flow.ApiKeyInput = "sk-abcd\U0001F600efghijkl\U0001F600mno";

                var masked = h.Flow.MaskedApiKey;
                for (var i = 0; i < masked.Length; i++)
                {
                    if (char.IsHighSurrogate(masked[i]))
                    {
                        Assert(i + 1 < masked.Length && char.IsLowSurrogate(masked[i + 1]),
                            $"a high surrogate at {i} with no low surrogate after it: '{masked}'");
                    }
                    else if (char.IsLowSurrogate(masked[i]))
                    {
                        Assert(i > 0 && char.IsHighSurrogate(masked[i - 1]),
                            $"a low surrogate at {i} with no high surrogate before it: '{masked}'");
                    }
                }

                Assert(masked.Contains('…'), "a key of this length must still be masked, not hidden");
                Assert(!masked.Contains('�'), "and must never render a replacement character");

                // The short-key branch is unchanged, and is counted in the same unit:
                // eight emoji are eight characters to a reader, not sixteen.
                h.Flow.ApiKeyInput = string.Concat(Enumerable.Repeat("\U0001F600", 8));
                Assert(h.Flow.MaskedApiKey == HyperWhisper.Localization.Loc.S("onboarding.setup.provider.keyHidden"),
                    "eight characters is short enough to hide outright, however wide they encode");
            });

            RunAsync("onboarding F1: leaving Configure cancels a probe in flight and keeps the earlier pass", async () =>
            {
                // The other half of the F1 remedy: in-flight checks are cancelled
                // when the user moves off the step that started them, so neither
                // spinner is left running and no result lands on a screen the user
                // is no longer on. What a cancelled RE-probe must NOT do is retract
                // the pass the same key already earned - the gate is open on that
                // pass, and a cancelled call is evidence of nothing.
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.HyperWhisperCloud);
                Assert(h.Flow.Advance(), "configure must be reachable");

                h.Flow.LicenseKeyInput = "HW-KEY";
                h.Flow.TestAccessKey();
                await h.LastTask;
                Assert(h.Flow.KeyValidated && h.Flow.CanContinue, "precondition: the key passed");

                h.License.GateProbe = true;
                h.Flow.TestAccessKey();                             // a second press, parked
                var second = h.LastTask;
                Assert(h.Flow.IsTestingKey, "precondition: the re-probe is running");

                Assert(h.Flow.Advance(), "the gate is open, so Continue must work");
                Assert(!h.Flow.IsTestingKey, "leaving the step must not strand the spinner");
                Assert(!h.Flow.HasInFlightWorkForTesting, "and must not leave the check in the task box");

                h.License.ReleaseProbe();
                await second;

                Assert(h.Flow.KeyValidated,
                    "a cancelled re-probe must not retract the pass the same key already earned");
                Assert(h.Flow.Step == OnboardingStep.Setup, "and the flow must have moved on");
            });

            RunAsync("onboarding: the licence fake parks a probe and an activation independently", async () =>
            {
                // Not a product defect - a defect in the test double, which produced
                // roughly 800 false positives in one fuzz round. One shared gate
                // field meant parking a probe after an activation orphaned the
                // activation's TaskCompletionSource forever, and Release() nulling
                // the field made IsParked read false while a call was still parked.
                var license = new FakeOnboardingLicense { GateProbe = true, GateActivation = true };

                var activation = license.ActivateAsync("KEY", CancellationToken.None);
                var probe = license.ProbeAsync("KEY", CancellationToken.None);

                Assert(license.IsActivationParked && license.IsProbeParked, "both calls are parked");
                Assert(!activation.IsCompleted && !probe.IsCompleted, "and neither has landed");

                license.ReleaseProbe();
                await probe;
                Assert(!activation.IsCompleted, "releasing the probe must not land the activation");
                Assert(license.IsActivationParked, "and must not forget that the activation is still parked");

                license.ReleaseActivation();
                await activation;
                Assert(!license.IsParked, "nothing is parked once both have been released");
            });

            // =================================================================
            // ONBOARDING — REVIEW ROUND 3
            // =================================================================

            Run("onboarding: a staged-source write that throws keeps the user on the step", () =>
            {
                // C4. ModeService.SaveMode RETHROWS DbUpdateException and the path
                // from the footer button had no try/catch on it, so a locked SQLite
                // file surfaced as the app's raw unhandled-exception box on top of
                // first run - and left the user on a Try It page whose recorder was
                // never armed, over a possibly half-applied Mode.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Microphone);
                h.Committer.ApplyThrows = true;

                Assert(!h.Flow.Advance(), "a refused production write must refuse the transition");
                Assert(h.Flow.Step == OnboardingStep.Microphone,
                    $"the user stays on the step they can see, not on {h.Flow.Step}");
                Assert(h.Committer.ApplyAttempts == 1, "the write was attempted exactly once");
                Assert(h.Flow.SourceApplyFailed, "the failure is reported, not swallowed");
                Assert(h.Flow.HasPendingProductionWrite,
                    "the restore point is kept, because a throw can still leave half a Mode behind");

                // And it recovers: the same Continue works once the database does.
                h.Committer.ApplyThrows = false;
                Assert(h.Flow.Advance(), "the step advances once the write succeeds");
                Assert(h.Flow.Step == OnboardingStep.TryIt, "and lands on Try It");
                Assert(!h.Flow.SourceApplyFailed, "a successful write clears the flag");
            });

            Run("onboarding: a failed final write does not mark first run complete", () =>
            {
                // C4, the Complete() half. Discarding the restore points is what
                // makes completion irreversible, so it may only happen over a write
                // that actually landed.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Done);
                h.Committer.ApplyThrows = true;

                Assert(!h.Flow.Complete(), "Complete must report that it did not complete");
                Assert(h.Committer.MarkCompletedCount == 0,
                    "first run must be re-offered, not closed over a setup that was never written");
                Assert(h.Committer.ReturnHomeCount == 0, "and the window must not be sent home");
                Assert(h.Flow.SourceApplyFailed, "the window needs to know why to report it");
                Assert(h.Flow.HasPendingProductionWrite, "Set Up Later must still have something to undo");

                // Pressing Done again, with the database back, completes normally.
                h.Committer.ApplyThrows = false;
                Assert(h.Flow.Complete(), "the retry completes");
                Assert(h.Committer.MarkCompletedCount == 1, "exactly once");
                Assert(!h.Flow.HasPendingProductionWrite, "and there is nothing left to roll back");
            });

            Run("onboarding: back into Try It is also gated on the write", () =>
            {
                // Done -> Back re-enters Try It, which re-applies. It has to refuse
                // on the same terms as Advance, or the one guarded direction is a
                // guard with a hole in it.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.Done);
                h.Committer.ApplyThrows = true;

                Assert(!h.Flow.Back(), "Back into Try It must refuse a write it cannot make");
                Assert(h.Flow.Step == OnboardingStep.Done, "and must not move the step");
                Assert(h.Flow.SourceApplyFailed, "for the same reported reason");
            });

            Run("onboarding: a Mode rollback is judged on the row, not on DeleteMode's answer", () =>
            {
                // Codex P1. DeleteMode returns false for three unrelated reasons and
                // only one of them is a successful rollback.
                Assert(LiveOnboardingSourceCommitter.DeleteLeftNothingBehind(true, () => false),
                    "a clean delete is a restored rollback");

                Assert(LiveOnboardingSourceCommitter.DeleteLeftNothingBehind(false, () => false),
                    "'the row was already gone' answers false and IS a restored rollback");

                Assert(!LiveOnboardingSourceCommitter.DeleteLeftNothingBehind(false, () => true),
                    "a row that is still there is production state the flow created and could not remove");

                // The read is deferred: it is a database round trip and is only
                // worth doing on the false path.
                var reads = 0;
                LiveOnboardingSourceCommitter.DeleteLeftNothingBehind(true, () => { reads++; return true; });
                Assert(reads == 0, "a successful delete must not cost a second query");
            });

            Run("onboarding: plugging a microphone in re-arms the level meter", () =>
            {
                // C5. OnDevicesChanged only ever turned the meter OFF, so a machine
                // that reached the step with no capture device kept a dead meter
                // under a prompt that had already gone back to "Say something.
                // Watch the bars."
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                h.Flow.BeginMicrophoneStep();

                Assert(!h.Flow.IsLevelMeterActive, "precondition: nothing to meter");
                var startsBefore = h.Audio.PreviewStarts;

                h.Audio.PublishAvailability(OnboardingDeviceAvailability.Available);

                Assert(h.Flow.IsLevelMeterActive,
                    "a device arriving while the step is open must light the meter");
                Assert(h.Audio.PreviewStarts > startsBefore, "and must actually open a capture stream");
                Assert(h.Flow.ShowsMicrophonePrompt,
                    "precondition for the defect: the prompt is back, so the bars must be too");

                // The unplug direction still holds, and does not leave a stream open.
                var stopsBefore = h.Audio.PreviewStops;
                h.Audio.PublishAvailability(OnboardingDeviceAvailability.NoDevices);
                Assert(!h.Flow.IsLevelMeterActive, "unplugging still darkens the meter");
                Assert(h.Audio.PreviewStops > stopsBefore, "and releases the endpoint");
            });

            Run("onboarding: a device arriving off the microphone step arms nothing", () =>
            {
                // The other half of the one arming rule: availability is
                // step-independent (the Done summary reads it), the METER is not.
                var h = new OnboardingHarness();
                h.Audio.Availability = OnboardingDeviceAvailability.NoDevices;
                var startsBefore = h.Audio.PreviewStarts;

                h.Audio.PublishAvailability(OnboardingDeviceAvailability.Available);

                Assert(h.Flow.DeviceAvailability == OnboardingDeviceAvailability.Available,
                    "availability is still tracked everywhere");
                Assert(!h.Flow.IsLevelMeterActive, "but no step is showing bars");
                Assert(h.Audio.PreviewStarts == startsBefore, "so no capture stream is opened");

                // And leaving the step disarms it for good.
                h.Flow.BeginMicrophoneStep();
                Assert(h.Flow.IsLevelMeterActive, "precondition: the step arms it");
                h.Flow.EndMicrophoneStep();
                h.Audio.PublishAvailability(OnboardingDeviceAvailability.NoDevices);
                h.Audio.PublishAvailability(OnboardingDeviceAvailability.Available);
                Assert(!h.Flow.IsLevelMeterActive, "a closed step never re-arms itself");
            });

            Run("onboarding: deferring falls back when the captured device is gone", () =>
            {
                // C6. On MainViewModel null is not "system default", it is "no
                // microphone": StartRecordingAsync refuses and raises
                // errors.noMicrophone. Restoring null for a device that has since
                // been unplugged left the app unable to record for the rest of the
                // session, with the rollback reporting a clean deferral.
                var h = new OnboardingHarness();
                h.Audio.SelectedDeviceId = "builtin";
                h.Audio.StoredDeviceId = "builtin";
                h.Flow.BeginMicrophoneStep();
                h.Flow.SelectDevice("usb");

                // The user's own microphone goes away mid-flow.
                h.Audio.Publish(new[] { FakeOnboardingAudio.ConnectedDevices[1] });

                h.Flow.DeferSetup();

                Assert(h.Audio.StoredDeviceId == "builtin",
                    "the PREFERENCE still names the device the user chose; it may come back");
                Assert(h.Audio.SelectedDeviceId is not null,
                    "but the app must be left with something it can actually open");
                Assert(h.Audio.SelectedDeviceId == "usb",
                    "and it is the first connected device, which is what MainViewModel picks for itself");

                // A captured null is still restored as null: that is a faithful
                // restore of a view model that genuinely had nothing selected.
                var none = new OnboardingHarness();
                none.Flow.BeginMicrophoneStep();
                none.Flow.SelectDevice("usb");
                none.Flow.DeferSetup();
                Assert(none.Audio.SelectedDeviceId is null,
                    "null is a real captured value, not 'the device went away'");
            });

            RunAsync("onboarding: a vendor that cannot be probed still saves its key", async () =>
            {
                // C1. CloudProviderHealthService answers Unknown for Meta MuseSTT
                // unconditionally - it documents no content-free validation
                // endpoint - so waiting for Healthy meant the key was never written,
                // Continue was disabled for good, and NOTHING appeared on screen,
                // because every pill needs an exact enum match.
                Assert(!CloudTranscriptionProvider.Meta.SupportsKeyHealthProbe(),
                    "precondition: Meta is the vendor with no validation endpoint");

                var h = new OnboardingHarness();
                h.ProviderKeys.Providers = new[] { CloudTranscriptionProvider.Meta };
                h.ProviderKeys.Health = ProviderHealth.Unknown;
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.SelectProvider(CloudTranscriptionProvider.Meta);
                h.Flow.ApiKeyInput = "meta-key";
                h.Flow.TestProviderKey();
                await h.LastTask;

                Assert(h.ProviderKeys.CurrentKey(CloudTranscriptionProvider.Meta) == "meta-key",
                    "an unverifiable vendor's key is still written; that is the only way to use it");
                Assert(h.Flow.ShowsProviderTestUnverified,
                    "and the user is told it was SAVED rather than validated");
                Assert(!h.Flow.ShowsProviderTestUnreachable,
                    "it is not a failed probe");
                Assert(!h.Flow.ShowsProviderTestError, "and it is not an error");
                Assert(h.Flow.CanContinue, "the gate opens, or the flow is a dead end");

                // The SAME Unknown from a vendor that CAN be probed means the
                // opposite, and must not be accepted.
                var probeable = new OnboardingHarness();
                probeable.ProviderKeys.Health = ProviderHealth.Unknown;
                probeable.GrantMicrophone();
                probeable.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                probeable.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                probeable.Flow.ApiKeyInput = "sk-live";
                probeable.Flow.TestProviderKey();
                await probeable.LastTask;

                Assert(!probeable.ProviderKeys.HasKey(CloudTranscriptionProvider.OpenAI),
                    "a probeable vendor that answered nothing has not validated anything");
                Assert(probeable.Flow.ShowsProviderTestUnreachable,
                    "and reads as unreachable, which is what it is");
                Assert(!probeable.Flow.ShowsProviderTestUnverified,
                    "the 'saved, cannot be checked' pill belongs to the other case only");
            });

            Run("onboarding: every offered vendor draws a mark, never a blank gap", () =>
            {
                // C7. Two screens built "/Assets/Providers/{name}.png" by
                // concatenation; "providerMeta" is a sentinel with no PNG behind it.
                // One screen guarded it, the other did not, and an earlier review
                // ruled the unguarded one safe BECAUSE Meta was not on the chip
                // strip - which round 2 then changed.
                foreach (var provider in Enum.GetValues<CloudTranscriptionProvider>())
                {
                    var name = provider.GetAssetName();
                    Assert(!string.IsNullOrEmpty(name), $"{provider} has no asset name at all");

                    // The sentinel is the ONE documented exception. Any other vendor
                    // whose logo does not ship fails here rather than rendering an
                    // empty 14x14 square in first run.
                    if (name == "providerMeta") continue;

                    Assert(ProviderAssets.Exists(name),
                        $"{provider} maps to '{name}', which is not a PNG in Assets/Providers");
                }

                // And the row a missing logo produces is renderable.
                var h = new OnboardingHarness();
                h.ProviderKeys.Providers = LiveOnboardingProviderKeyGateway.ByokProviders;
                foreach (var row in h.Flow.ProviderOptions)
                {
                    Assert(row.HasAsset ^ row.ShowsMonogram,
                        $"{row.Provider}: exactly one of the logo and the monogram is shown");
                    Assert(row.HasAsset || row.Monogram.Length > 0,
                        $"{row.Provider} has neither a logo nor a monogram, so its chip is a blank gap");
                    Assert(!row.HasAsset || ProviderAssets.Exists(row.AssetPath
                        .Replace("/Assets/Providers/", string.Empty)
                        .Replace(".png", string.Empty)),
                        $"{row.Provider} binds an image path with no file behind it");
                }

                // The local engines go through the same set. "providerLocalParakeet"
                // was in the tree for two rounds and has never been a file.
                foreach (var kind in new[] { OnboardingModelKind.Whisper, OnboardingModelKind.Parakeet })
                {
                    var name = LiveOnboardingModelCatalog.ProviderAssetNameFor(kind);
                    Assert(ProviderAssets.Exists(name),
                        $"the onboarding {kind} row uses '{name}', which is not a PNG in Assets/Providers");
                }
            });

            Run("onboarding: the shortlist only offers engines this platform can run", () =>
            {
                // Codex P1. ModelLibraryManager and ModeService both already refuse
                // an unsupported engine; first run offered the whole shortlist and
                // RECOMMENDED Parakeet, so an ARM64 machine with no native daemon
                // could download, select and COMMIT a Mode that fails on the very
                // next screen.
                var supported = LiveOnboardingModelCatalog.SupportedModels;

                foreach (var model in supported)
                {
                    var ok = model.Kind switch
                    {
                        OnboardingModelKind.Whisper => PlatformHelper.SupportsWhisperTranscription,
                        OnboardingModelKind.Parakeet => PlatformHelper.SupportsParakeetTranscription,
                        _ => false
                    };
                    Assert(ok, $"{model.Id} ({model.Kind}) is offered but unsupported on this platform");
                }

                Assert(supported.Count <= LiveOnboardingModelCatalog.CuratedModels.Count,
                    "the supported list is a subset of the shortlist");

                if (PlatformHelper.SupportsLocalTranscription)
                {
                    Assert(supported.Count > 0,
                        "a platform that runs a local engine must be offered at least one model");
                    Assert(supported.Count(m => m.IsRecommended) == 1,
                        "exactly one model carries the recommendation, whichever survived the filter");
                }
            });

            Run("onboarding: an empty on-device shortlist is never offered as a choice", () =>
            {
                // The dead end the platform filter could otherwise create. The
                // Configure step for the on-device branch has NO error surface of
                // its own - the two on that page belong to the licence and the BYOK
                // probe - so an empty model list there is a gate that can never open
                // with nothing on screen explaining it. The honest place to say no
                // is the step that offers the choice, which is what ModesPage and
                // MainViewModel already do for the same predicate.
                var empty = new OnboardingHarness();
                empty.Catalog.Catalog.Clear();

                Assert(!empty.Flow.IsOnDeviceAvailable, "precondition: no local engine on this machine");
                Assert(empty.Flow.SourceOptions.All(r => r.Kind != OnboardingSourceKind.OnDevice),
                    "the on-device card must not be offered where no model can be installed");
                Assert(empty.Flow.SourceOptions.Count == 2, "the other two branches are unaffected");

                empty.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                Assert(empty.Flow.SelectedSource != OnboardingSourceKind.OnDevice,
                    "and it cannot be staged by any other route either");

                // The normal machine still gets all three.
                var normal = new OnboardingHarness();
                Assert(normal.Flow.IsOnDeviceAvailable, "precondition: the fake catalog has models");
                Assert(normal.Flow.SourceOptions.Count == 3, "every branch is offered where it works");
                Assert(normal.Flow.SourceOptions.Any(r => r.Kind == OnboardingSourceKind.OnDevice),
                    "including on-device");
            });

            SynchronizationContext.SetSynchronizationContext(onboardingPreviousContext);

            // =================================================================
            // ONBOARDING — LIVE ADAPTERS (phase 2)
            //
            // The seams above are covered against fakes. These cover the pure
            // decision points of the REAL adapters in
            // HyperWhisper/Services/Onboarding/OnboardingLiveDependencies.cs.
            //
            // Deliberately absent: anything that would write to the machine
            // running the suite. LiveOnboardingProviderKeyGateway.Persist puts a
            // secret in Windows Credential Manager and LiveOnboardingPermissions
            // reads HKCU, so those are exercised through their pure halves
            // (TranscriptionKeyType, BuildState, Evaluate) instead of by
            // touching real state.
            // =================================================================

            Run("onboarding: the restore point clones every Mode field", () =>
            {
                // Reflection, not a hand-written field list: a column added to Mode
                // and forgotten in Clone() fails here rather than silently failing
                // to come back when the user picks "Set Up Later".
                var settable = typeof(Mode)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite)
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();

                Assert(settable.Length >= 34,
                    $"Mode should expose at least 34 settable columns, found {settable.Length}");

                var source = new Mode();
                var seed = 1;
                foreach (var property in settable)
                {
                    object value =
                        property.PropertyType == typeof(string) ? $"value-{seed}"
                        : property.PropertyType == typeof(bool) ? (object)(seed % 2 == 0)
                        : property.PropertyType == typeof(int) ? 1000 + seed
                        : property.PropertyType == typeof(Guid) ? Guid.NewGuid()
                        : property.PropertyType == typeof(DateTime)
                            ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(seed)
                        : property.PropertyType == typeof(List<string>) ? new List<string> { $"term-{seed}" }
                        : throw new InvalidOperationException(
                            $"Mode.{property.Name} is a {property.PropertyType.Name}. Teach this case how to "
                            + "seed that type, and teach WindowsOnboardingRestorePoint.Clone how to copy it.");

                    property.SetValue(source, value);
                    seed++;
                }

                var clone = WindowsOnboardingRestorePoint.Clone(source);

                foreach (var property in settable)
                {
                    var original = property.GetValue(source);
                    var copied = property.GetValue(clone);

                    if (property.PropertyType == typeof(List<string>))
                    {
                        // The committer mutates the live row on its way to Apply().
                        // A snapshot that aliased the same list would follow the
                        // mutation and restore nothing.
                        Assert(!ReferenceEquals(original, copied),
                            $"Mode.{property.Name} must be deep-copied, not aliased");
                        Assert(((List<string>)original!).SequenceEqual((List<string>)copied!),
                            $"Mode.{property.Name} contents must survive the clone");
                        continue;
                    }

                    Assert(Equals(original, copied),
                        $"WindowsOnboardingRestorePoint.Clone dropped Mode.{property.Name}. Add it to Clone().");
                }
            });

            Run("onboarding: microphone consent is denied only when a ConsentStore toggle says Deny", () =>
            {
                // Nothing here reads HKCU. Evaluate() exists as an internal so the
                // policy can be pinned without writing to the real consent store of
                // whoever is running the suite.
                Assert(MicrophonePrivacyService.Evaluate(null, null) == MicrophoneConsent.Allowed,
                    "a machine that has never recorded has no value under either key; that is not a denial");
                Assert(MicrophonePrivacyService.Evaluate("Allow", "Allow") == MicrophoneConsent.Allowed,
                    "both toggles on must read as allowed");
                Assert(MicrophonePrivacyService.Evaluate("Deny", "Allow") == MicrophoneConsent.Denied,
                    "the global toggle alone blocks every app");
                Assert(MicrophonePrivacyService.Evaluate("Allow", "Deny") == MicrophoneConsent.Denied,
                    "the desktop-app toggle alone blocks THIS app, which is the case that matters");
                Assert(MicrophonePrivacyService.Evaluate("deny", null) == MicrophoneConsent.Denied,
                    "the registry value's case must not decide whether the user is blocked");
                Assert(MicrophonePrivacyService.Evaluate("Prompt", null) == MicrophoneConsent.Allowed,
                    "an unrecognised value must not be reported as a denial");
            });

            Run("onboarding: the text delivery gate suppresses and clears", () =>
            {
                var before = TextDeliveryGate.IsSuppressed;
                try
                {
                    TextDeliveryGate.SetSuppressed(false);
                    Assert(!TextDeliveryGate.IsSuppressed, "the gate must start open");

                    TextDeliveryGate.SetSuppressed(true);
                    Assert(TextDeliveryGate.IsSuppressed,
                        "onboarding raises this so its Try It transcript never lands in the user's editor");

                    TextDeliveryGate.SetSuppressed(true);
                    Assert(TextDeliveryGate.IsSuppressed, "raising it twice must not toggle it back");

                    TextDeliveryGate.SetSuppressed(false);
                    Assert(!TextDeliveryGate.IsSuppressed,
                        "the gate must reopen on close, or dictation stays dead for the rest of the session");
                }
                finally
                {
                    TextDeliveryGate.SetSuppressed(before);
                }
            });

            Run("onboarding: the shortcut row separates 'not registered yet' from a real conflict", () =>
            {
                var shortcut = new KeyboardShortcut { Control = true, Shift = true, Key = Key.Space };
                var display = shortcut.ToDisplayString();

                var never = LiveOnboardingPermissions.BuildState(display, shortcut, null);
                Assert(never.Status == OnboardingShortcutStatus.Unknown && never.FailureReason is null,
                    "no recorded attempt means unknown, not failed");

                var noHwnd = LiveOnboardingPermissions.BuildState(
                    display, shortcut, Result.Failure("Cannot register shortcut: HwndSource is null"));
                Assert(noHwnd.Status == OnboardingShortcutStatus.Unknown && noHwnd.FailureReason is null,
                    "the window having no HWND yet is an ordering fact about the app, never a verdict "
                    + "about the user's shortcut");

                var ok = LiveOnboardingPermissions.BuildState(display, shortcut, Result.Success());
                Assert(ok.Status == OnboardingShortcutStatus.Registered && ok.FailureReason is null,
                    "a successful registration is registered");
                Assert(ok.DisplayText == display, "the row must print the configured chord");

                var taken = LiveOnboardingPermissions.BuildState(
                    display, shortcut, Result.Failure("RegisterHotKey failed (Win32 error=1409)"));
                Assert(taken.Status == OnboardingShortcutStatus.Failed,
                    "1409 is a real conflict and must render as one");
                Assert(taken.FailureReason == ShortcutValidationService.GetRegistrationErrorMessage(1409, shortcut),
                    "the onboarding row must print the same sentence as the main window's banner");

                var reserved = LiveOnboardingPermissions.BuildState(
                    display, shortcut, Result.Failure("RegisterHotKey failed (Win32 error=1413)"));
                Assert(reserved.FailureReason == ShortcutValidationService.GetRegistrationErrorMessage(1413, shortcut),
                    "1413 must map to the 'reserved by Windows' sentence");

                var unparsed = LiveOnboardingPermissions.BuildState(
                    display, shortcut, Result.Failure("something else went wrong"));
                Assert(unparsed.Status == OnboardingShortcutStatus.Failed
                    && unparsed.FailureReason == ShortcutValidationService.GetRegistrationErrorMessage(0, shortcut),
                    "a failure with no Win32 code still has to say something");
            });

            Run("onboarding: the credits panel flattens the cloud balance and survives having none", () =>
            {
                Assert(LiveOnboardingCreditsGateway.Flatten(null) is null,
                    "no balance fetched yet must stay null so the panel renders an ellipsis, not a zero");

                var credits = new HyperWhisperCloudCredits
                {
                    CreditsRemaining = 1234.5,
                    MinutesRemaining = 78
                };
                var flat = LiveOnboardingCreditsGateway.Flatten(credits);

                Assert(flat is not null, "a fetched balance must flatten");
                Assert(flat!.CreditsRemaining == 1234.5, "credits must cross unchanged");
                Assert(flat.MinutesRemaining == 78, "minutes must cross unchanged");
                Assert(flat.FormattedBalance == credits.FormattedBalance,
                    "the onboarding panel must print the same balance string as the rest of the app, "
                    + "not its own re-formatting of the number");
                Assert(flat.FormattedBalance == "$1.23 remaining (~78 minutes)",
                    $"unexpected balance rendering '{flat.FormattedBalance}'");
            });

            Run("onboarding: the curated model shortlist maps onto Model Library download ids", () =>
            {
                var curated = LiveOnboardingModelCatalog.CuratedModels;
                Assert(curated.Count == 4, $"the shortlist is four models, found {curated.Count}");
                Assert(curated.Count(m => m.IsRecommended) == 1,
                    "exactly one model may carry the recommended badge");

                foreach (var model in curated)
                {
                    var libraryId = LiveOnboardingModelCatalog.LibraryId(model);
                    var expectedPrefix = model.Kind == OnboardingModelKind.Parakeet ? "parakeet-" : "whisper-";
                    Assert(libraryId.StartsWith(expectedPrefix, StringComparison.Ordinal),
                        $"'{model.Id}' must resolve to a {expectedPrefix}* library row, got '{libraryId}'");

                    // ModelDownloadService keys on the Model Library row id, not on
                    // the raw model id. Getting this wrong makes StartDownload a
                    // silent no-op and leaves the step spinning forever.
                    var known = model.Kind == OnboardingModelKind.Parakeet
                        ? ParakeetModelInfo.AllModels.Any(m => m.Id == model.Id)
                        // Whisper's identity column is Type, not Id.
                        : WhisperModelInfo.AllModels.Any(m => m.Type == model.Id);
                    Assert(known, $"'{model.Id}' is not a model this app knows how to download");
                }
            });

            Run("onboarding: every offered BYOK provider has somewhere to store its key", () =>
            {
                var offered = LiveOnboardingProviderKeyGateway.ByokProviders;
                Assert(offered.Count == offered.Distinct().Count(), "the provider list must not repeat a vendor");

                foreach (var provider in offered)
                {
                    var routed = provider.GetApiKeyProvider() != PostProcessingProvider.None
                        || LiveOnboardingProviderKeyGateway.TranscriptionKeyType(provider) is not null;
                    Assert(routed,
                        $"{provider} is offered on the BYOK branch but Persist() has no slot for its key, "
                        + "so the setup step would fail with 'could not save'");
                }

                // These three need no key: HyperWhisper Cloud has its own step, and
                // the health probe for the two platform providers short-circuits to
                // Healthy WITHOUT one. Offering them opens the gate on a pass that
                // proves nothing.
                foreach (var excluded in new[]
                {
                    CloudTranscriptionProvider.HyperWhisperCloud,
                    CloudTranscriptionProvider.MicrosoftAzureSpeech,
                    CloudTranscriptionProvider.GoogleSpeech,
                })
                    Assert(!offered.Contains(excluded), $"{excluded} must not be offered as a BYOK key vendor");
            });

            Run("onboarding: committing a staged source writes the Windows engine columns", () =>
            {
                // Windows Modes carry LocalEngine / LocalParakeetModel, which the
                // macOS shape has no equivalent of. The committer derives them from
                // the staged model id, so OnboardingStagedSource needs no extra
                // field — but only if this mapping stays right.
                var parakeet = new Mode();
                LiveOnboardingSourceCommitter.ApplyStagedFields(
                    parakeet,
                    new OnboardingStagedSource(OnboardingSourceKind.OnDevice, "parakeet-v2", null, 0, null));

                Assert(parakeet.ProviderType == "local", "an on-device source is local");
                Assert(parakeet.LocalEngine == "parakeet", "a Parakeet id must select the Parakeet engine");
                Assert(parakeet.LocalParakeetModel == "parakeet-v2", "the Parakeet slot holds the id");
                Assert(parakeet.CloudProvider is null, "an on-device Mode has no cloud provider");

                var whisper = new Mode();
                LiveOnboardingSourceCommitter.ApplyStagedFields(
                    whisper,
                    new OnboardingStagedSource(OnboardingSourceKind.OnDevice, "base", null, 0, null));

                Assert(whisper.LocalEngine == "whisper", "a Whisper id must select the Whisper engine");
                Assert(whisper.Model == "base" && whisper.ModelType == "base",
                    "both Whisper columns carry the id; TranscriptionService reads ModelType");
                Assert(whisper.LocalParakeetModel is null,
                    "a Whisper selection must not leave a stale Parakeet model behind");

                var cloud = new Mode { CloudTranscriptionModel = "left-over-model" };
                LiveOnboardingSourceCommitter.ApplyStagedFields(
                    cloud,
                    new OnboardingStagedSource(
                        OnboardingSourceKind.HyperWhisperCloud, "hyperwhisper", "hyperwhisper", 1,
                        CloudAccuracyTier.ElevenLabsScribeV2.ToStorageValue()));

                Assert(cloud.ProviderType == "cloud", "a cloud source is cloud");
                Assert(cloud.CloudProvider == "hyperwhisper", "the provider id must land");
                Assert(cloud.PostProcessingMode == 1, "the staged post-processing mode must land");
                Assert(cloud.CloudAccuracyTier == CloudAccuracyTier.ElevenLabsScribeV2.ToStorageValue(),
                    "the staged accuracy tier must land");
                Assert(cloud.CloudTranscriptionModel is null,
                    "a stale per-provider model override would silently outrank the tier");
            });

            Run("onboarding: the sample clip ships in the build and extracts as a real WAV", () =>
            {
                // The no-microphone path is the ONLY thing the Try It step can offer
                // on a machine with no input device, so a missing resource turns that
                // step into a dead end.
                Assert(OnboardingLiveDependencies.SampleClipExists(),
                    $"'{OnboardingLiveDependencies.SampleClipResourceName}' is not embedded in this build. "
                    + "Check the EmbeddedResource LogicalName in HyperWhisper.csproj.");

                var path = OnboardingLiveDependencies.ExtractSampleClip();
                Assert(path is not null, "the clip must extract to a real file for FileTranscriptionService");

                try
                {
                    var bytes = File.ReadAllBytes(path!);
                    Assert(bytes.Length > 1024, $"the extracted clip is only {bytes.Length} bytes");

                    // Assert on the header, not on a transcription: no smoke test may
                    // depend on a model being installed or a network being up.
                    Assert(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                        && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE",
                        "the extracted file is not a RIFF/WAVE container");

                    var channels = BitConverter.ToUInt16(bytes, 22);
                    var sampleRate = BitConverter.ToUInt32(bytes, 24);
                    var bitsPerSample = BitConverter.ToUInt16(bytes, 34);

                    Assert(channels == 1 && sampleRate == 16000 && bitsPerSample == 16,
                        $"the clip is {channels}ch/{sampleRate}Hz/{bitsPerSample}bit; the transcription path "
                        + "wants 16 kHz mono 16-bit");
                }
                finally
                {
                    try { if (path is not null) File.Delete(path); } catch { /* best effort */ }
                }
            });

            // =================================================================
            // ONBOARDING — FIRST RUN TRIGGER (phase 4)
            //
            // The whole trigger is one pure function plus one environment
            // variable. Both are pinned here because getting either wrong is
            // silent: too eager and every launch shows a modal, too shy and a
            // fresh install never sees setup at all.
            // =================================================================

            Run("onboarding trigger: the pending flag alone decides", () =>
            {
                Assert(!OnboardingLaunchPolicy.ShouldShowOnboarding(false, null),
                    "a settled profile must never be shown the first-run flow");
                Assert(OnboardingLaunchPolicy.ShouldShowOnboarding(true, null),
                    "a pending profile must be shown the first-run flow");
            });

            Run("onboarding trigger: only \"1\" opts out", () =>
            {
                Assert(!OnboardingLaunchPolicy.ShouldShowOnboarding(true, "1"),
                    "HYPERWHISPER_WINDOWS_SKIP_ONBOARDING=1 must suppress the flow");
                Assert(!OnboardingLaunchPolicy.ShouldShowOnboarding(true, " 1 "),
                    "the opt-out must survive stray whitespace");

                // A stale variable left in a shell must not silently disable first
                // run, so anything that is not "1" is not an opt-out.
                foreach (var value in new[] { "", "0", "false", "no", "yes", "true", "2" })
                {
                    Assert(OnboardingLaunchPolicy.ShouldShowOnboarding(true, value),
                        $"skip value '{value}' must NOT suppress the flow");
                }

                Assert(!OnboardingLaunchPolicy.ShouldShowOnboarding(false, "0"),
                    "the opt-out must not be able to FORCE the flow on a settled profile");
            });

            Run("onboarding trigger: an isolated app-data profile does not suppress it", () =>
            {
                // This is the one the plan was amended over. The app-data override
                // guards the machine-wide Run key registration above it in
                // App.OnStartup; onboarding writes nothing outside AppDataRoot, and
                // a scratch profile is precisely how a fresh OnboardingPending ==
                // true is produced. Guarding on it would break the only end-to-end
                // verification there is.
                Assert(AppPaths.IsAppDataRootOverridden,
                    "the smoke harness is expected to run under an app-data override");
                Assert(OnboardingLaunchPolicy.ShouldShowOnboarding(true, null),
                    "the app-data override must not suppress the first-run flow");

                Assert(OnboardingLaunchPolicy.SkipEnvironmentVariable == "HYPERWHISPER_WINDOWS_SKIP_ONBOARDING",
                    "the opt-out variable name is documented in the PR and must not drift");
            });

            Run("onboarding trigger: the \"Run setup again\" tray string resolves", () =>
            {
                // The tray label is the only onboarding string that never enters a
                // page's visual tree, so the phase 3 raw-key case cannot see it.
                const string key = "onboarding.menu.runAgain";
                Assert(HyperWhisper.Localization.Loc.S(key) != key, $"'{key}' is missing from Strings.resx");
            });

            // =================================================================
            // ONBOARDING — WPF (phase 3)
            //
            // XAML faults are build-time for syntax and RUN-time for everything
            // else: a missing StaticResource, a style whose TargetType does not
            // match, a converter that is not in scope. None of those show up in
            // `dotnet build`, and there is no other harness on this repo that
            // constructs an onboarding page. These three cases are that harness.
            // ----- Review round 1 regressions ---------------------------------
            // One case per finding whose defect a test can express. Each names the
            // shape that was wrong, not just the shape that is right, so a
            // reintroduction fails here rather than on someone's first run.

            Run("onboarding: download progress crosses the seam as a fraction, not a percentage", () =>
            {
                // ModelDownloadService reports 0-100 (Math.Clamp(p * 100, 0, 100)),
                // every onboarding consumer reads 0-1. Storing the percentage
                // verbatim painted the bar full and the pill "100%" at 1% of a
                // ~170 s download, so the user believed it had hung.
                Assert(Math.Abs(LiveOnboardingModelCatalog.ProgressFraction(0) - 0.0) < 1e-9,
                    "0% is 0.0");
                Assert(Math.Abs(LiveOnboardingModelCatalog.ProgressFraction(1) - 0.01) < 1e-9,
                    "the FIRST tick the service ever emits is ~1%, and it must not read as full");
                Assert(Math.Abs(LiveOnboardingModelCatalog.ProgressFraction(42) - 0.42) < 1e-9,
                    "42% is 0.42");
                Assert(Math.Abs(LiveOnboardingModelCatalog.ProgressFraction(100) - 1.0) < 1e-9,
                    "100% is 1.0");
                Assert(LiveOnboardingModelCatalog.ProgressFraction(140) <= 1.0
                    && LiveOnboardingModelCatalog.ProgressFraction(-5) >= 0.0,
                    "the fraction stays inside [0,1] whatever the producer says");

                // And the presentation still reads it as a fraction.
                var h = new OnboardingHarness();
                h.Flow.SelectSource(OnboardingSourceKind.OnDevice);
                h.Flow.SelectModel(FakeOnboardingCatalog.Parakeet);
                h.Catalog.Progresses[FakeOnboardingCatalog.Parakeet.Id] =
                    LiveOnboardingModelCatalog.ProgressFraction(1);
                h.Catalog.PublishActivity();
                Assert(h.Flow.SelectedModelProgressPercent == 1,
                    $"1% of a download must render as 1%, rendered {h.Flow.SelectedModelProgressPercent}%");
            });

            Run("onboarding: abandoning the window leaves first run to be re-offered", () =>
            {
                // macOS's deferSetup() DOES mark completion, and Set Up Later is the
                // same explicit decision here. But Alt+F4, tray Quit and an OS
                // shutdown are not decisions: macOS never reaches
                // markOnboardingCompleted() when its process dies mid-sheet, and a
                // PC that restarts for an update must not strand a brand-new user in
                // an unconfigured app forever.
                var explicitDefer = new OnboardingHarness();
                explicitDefer.StageInstalledOnDeviceModel();
                explicitDefer.AdvanceTo(OnboardingStep.TryIt);
                explicitDefer.Flow.DeferSetup();
                Assert(explicitDefer.Committer.MarkCompletedCount == 1,
                    "Set Up Later is an explicit decision and closes first run, as on macOS");
                Assert(explicitDefer.Committer.RestoreCount == 1, "and it still rolls back");

                var abandoned = new OnboardingHarness();
                abandoned.StageInstalledOnDeviceModel();
                abandoned.AdvanceTo(OnboardingStep.TryIt);
                Assert(abandoned.Committer.ProductionState != FakeOnboardingCommitter.Seed,
                    "precondition: the staged source was applied");

                abandoned.Flow.AbandonSetup();

                Assert(abandoned.Committer.MarkCompletedCount == 0,
                    "an abandoned run must NOT clear OnboardingPending, or it is never re-offered");
                Assert(abandoned.Committer.RestoreCount == 1, "it must still roll back every staged write");
                Assert(abandoned.Committer.ProductionState == FakeOnboardingCommitter.Seed,
                    "the staged write must be undone");
                Assert(abandoned.Committer.ReturnHomeCount == 1, "and the shell still goes home");
                Assert(!abandoned.Flow.IsLiveForTesting, "the commit boundary still closes");
            });

            Run("onboarding: a rollback that cannot restore a key reports it rather than closing over it", () =>
            {
                // Test API key writes the candidate to Credential Manager before any
                // commit boundary. If putting the ORIGINAL back fails, the user has
                // lost a key they never asked to change, and reporting that as a
                // clean deferral is the whole defect.
                var h = new OnboardingHarness();
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "sk-original";
                h.GrantMicrophone();
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                h.Flow.ApiKeyInput = "sk-candidate";
                h.Flow.TestProviderKey();
                h.LastTask.GetAwaiter().GetResult();

                Assert(h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] == "sk-candidate",
                    "precondition: the candidate overwrote the original");
                Assert(h.Flow.HasPendingProductionWrite, "precondition: there is something to roll back");

                h.ProviderKeys.PersistSucceeds = false;
                h.Flow.DeferSetup();

                Assert(h.Flow.UnrestoredProviderKeys.Count == 1
                    && h.Flow.UnrestoredProviderKeys[0] == CloudTranscriptionProvider.OpenAI,
                    "the provider whose key could not be put back must be named");
                Assert(h.Flow.HasPendingProductionWrite,
                    "a restore point that could not be spent must not be discarded");

                // A clean rollback reports nothing and forgets the restore point.
                var clean = new OnboardingHarness();
                clean.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "sk-original";
                clean.GrantMicrophone();
                clean.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                clean.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                clean.Flow.ApiKeyInput = "sk-candidate";
                clean.Flow.TestProviderKey();
                clean.LastTask.GetAwaiter().GetResult();
                clean.Flow.DeferSetup();

                Assert(clean.Flow.UnrestoredProviderKeys.Count == 0, "a clean rollback reports nothing");
                Assert(clean.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] == "sk-original",
                    "and the original key is back");
                Assert(!clean.Flow.HasPendingProductionWrite, "nothing is left to roll back");
            });

            Run("onboarding: a validated provider key stops being validated once the field is edited", () =>
            {
                // The record used to track only the PROVIDER, so it survived a key
                // edit: validate A, type B, and Continue stayed enabled while
                // Credential Manager still held A.
                var h = new OnboardingHarness();
                h.GrantMicrophone();
                h.AdvanceTo(OnboardingStep.Source);
                h.Flow.SelectSource(OnboardingSourceKind.YourProvider);
                h.AdvanceTo(OnboardingStep.Configure);
                h.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                h.Flow.ApiKeyInput = "sk-key-a";
                h.Flow.TestProviderKey();
                h.LastTask.GetAwaiter().GetResult();

                Assert(h.Flow.KeyValidated, "precondition: key A passed");
                Assert(h.Flow.CanContinue, "precondition: the Configure gate is open for key A");
                Assert(h.Flow.IsSelectedSourceUsable, "precondition: the setup checklist accepts key A");

                h.Flow.ApiKeyInput = "sk-key-b";

                Assert(!h.Flow.KeyValidated, "editing the field clears the inline pass");
                Assert(!h.Flow.CanContinue, "and Continue must NOT stay enabled for an untested key");
                Assert(!h.Flow.IsSelectedSourceUsable,
                    "an untested key must not read as validated on the setup checklist");
                Assert(h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] == "sk-key-a",
                    "and nothing wrote key B, so the gate would have been lying");

                // Retyping the key that DID pass reopens the gate, exactly as the
                // licence branch does. This is what has to survive Back navigation.
                h.Flow.ApiKeyInput = "sk-key-a";
                Assert(h.Flow.CanContinue, "the key that passed is still the key that passed");

                h.Flow.ResetConfigureTestResults();
                Assert(h.Flow.CanContinue,
                    "Back navigation clears the inline result, not the fact that this key was verified");

                // Emptying the field is the FLOW's doing, not the user's:
                // SelectProvider clears it so a masked key typed for one vendor can
                // never be saved under another. A round trip through a second
                // provider must not throw away a pass the user already paid a
                // network round trip for.
                h.Flow.SelectProvider(CloudTranscriptionProvider.Groq);
                Assert(h.Flow.ApiKeyInput.Length == 0, "precondition: the provider switch cleared the field");
                Assert(!h.Flow.CanContinue, "the other provider was never validated");

                h.Flow.SelectProvider(CloudTranscriptionProvider.OpenAI);
                Assert(h.Flow.ApiKeyInput.Length == 0, "precondition: still cleared");
                Assert(h.Flow.CanContinue,
                    "an empty field falls back to the stored key, which IS the validated one");

                // But a stored key that no longer matches the validated one does not
                // reopen the gate, however it got there.
                h.ProviderKeys.Stored[CloudTranscriptionProvider.OpenAI] = "sk-something-else";
                Assert(!h.Flow.CanContinue, "a stored key that was never probed here is not a pass");
            });

            Run("onboarding: the Try It microphone transcription is owned, guarded and cancellable", () =>
            {
                // It used to be a discarded task on CancellationToken.None: no
                // transcribing state, no re-entrancy guard, and nothing for teardown
                // to cancel, so Set Up Later disposed the recorder out from under a
                // running, billable orchestrator call.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                h.Flow.ToggleTestRecording();
                Assert(h.Audio.StartRecordingCalls == 1, "the first press starts the capture");
                Assert(h.Flow.IsRecording, "and the flow follows the gateway");

                h.Audio.GateStopAndTranscribe = true;
                h.Flow.ToggleTestRecording();

                Assert(h.Audio.StopAndTranscribeCalls == 1, "the second press stops and transcribes");
                Assert(h.Flow.IsTranscribingTestRecording, "the step must say it is transcribing");
                Assert(h.Flow.HasInFlightWorkForTesting, "the transcription must be OWNED by the task box");
                Assert(!h.Flow.ShowsRecordButton,
                    "a live Record button beside 'Nothing here yet' is what invited a second capture");
                Assert(!h.Flow.ShowsEmptyTranscriptHint, "and the empty hint must not claim nothing happened");
                Assert(h.Flow.ShowsTestRecordingTranscribing, "the transcribing pill takes its place");

                // Re-entrancy: pressing again while it runs must not start a second
                // capture into the same transcript channel.
                h.Flow.ToggleTestRecording();
                Assert(h.Audio.StartRecordingCalls == 1, "no second capture may overlap the transcription");
                Assert(h.Audio.StopAndTranscribeCalls == 1, "and no second transcription either");

                // Teardown reaches it, which is the whole point of registering it.
                h.Flow.DeferSetup();
                Assert(!h.Flow.IsLiveForTesting, "the flow closed");
                h.Audio.Release();
                h.LastTask.GetAwaiter().GetResult();
                Assert(h.Flow.Transcript.Length == 0,
                    "a transcription that lands after the window closed must not write flow state");
            });

            Run("onboarding: the level meter reports the OPEN, not the availability", () =>
            {
                // A device can enumerate and still refuse to open: another app holds
                // it in exclusive mode, consent flips between the read and the open,
                // the driver faults. StartInputLevelPreview swallowed that and the
                // flag was set from availability alone, so 33 bars froze under a live
                // "speak to see the level" hint.
                var h = new OnboardingHarness();
                h.Audio.PreviewOpenFails = true;
                h.Flow.BeginMicrophoneStep();

                Assert(h.Audio.PreviewStarts == 1, "the open is still attempted");
                Assert(!h.Flow.IsLevelMeterActive,
                    "a preview that failed to open must leave the meter explicitly inactive");

                h.Audio.PreviewOpenFails = false;
                h.Flow.SelectDevice("usb");
                Assert(h.Flow.IsLevelMeterActive, "a device that DOES open lights the meter");

                // And unplugging it mid-step reconciles the flag rather than leaving
                // the bars frozen at their last heights.
                h.Audio.PublishAvailability(OnboardingDeviceAvailability.NoDevices);
                Assert(!h.Flow.IsLevelMeterActive,
                    "losing the device mid-step must turn the meter off");
            });

            Run("onboarding: a post-processing warning reaches the Try It panel", () =>
            {
                // MainViewModel deliberately drops the Onboarding call site (a toast
                // behind a modal is unreachable) and TranscriptionResult carries no
                // warning field, so without this channel a 401 on the post-processing
                // LLM showed a raw transcript under full success chrome. Five of the
                // six seeded Modes post-process through a cloud LLM.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                h.Audio.PublishTranscript("the quick brown fox");
                h.Audio.PublishWarning("Post-processing was skipped: the API key was rejected.");

                Assert(h.Flow.HasTranscriptWarning, "the flow must hold the warning");
                Assert(h.Flow.TranscriptWarning == "Post-processing was skipped: the API key was rejected.",
                    "verbatim: the orchestrator's sentence is already localized");
                Assert(h.Flow.ShowsTranscriptWarning, "and the panel must render it beside the transcript");

                // An error transcript already reads as a failure; two failure
                // surfaces at once is worse than one.
                h.Audio.PublishTranscript("Error: something broke");
                Assert(!h.Flow.ShowsTranscriptWarning, "no warning on top of an error transcript");

                // A new attempt does not inherit the last one's warning.
                h.Flow.BeginTryItStep();
                Assert(!h.Flow.HasTranscriptWarning, "clearing the transcript clears its warning");
            });

            Run("onboarding: the session flag and the delivery gate cannot disagree", () =>
            {
                // Two flags for one fact is how the global hotkey came to have no
                // guard at all: exclusivity was enforced on the tray items and on the
                // paste sink, and the recording ENTRY POINTS were never told.
                Assert(!OnboardingSession.IsActive, "precondition: no session");
                Assert(!TextDeliveryGate.IsSuppressed, "precondition: delivery is allowed");

                try
                {
                    OnboardingSession.SetActive(true);
                    Assert(OnboardingSession.IsActive, "the session is open");
                    Assert(TextDeliveryGate.IsSuppressed,
                        "opening the session must raise the delivery gate in the same call");

                    // The leaf sink still refuses, which is the backstop for anything
                    // already in flight when the window opened.
                    var paste = new SmartPasteService();
                    Assert(!paste.CopyToClipboard("a test transcript"),
                        "a suppressed clipboard copy must REPORT that it did nothing");
                }
                finally
                {
                    OnboardingSession.SetActive(false);
                }

                Assert(!OnboardingSession.IsActive, "the session closes");
                Assert(!TextDeliveryGate.IsSuppressed, "and the gate comes down with it");
            });

            Run("onboarding: every string the flow resolves outside onboarding.* exists", () =>
            {
                // Loc.S returns the KEY on a miss and the visual-tree check only
                // rejects keys starting with "onboarding.", so "app.unknown.error"
                // was ported from the macOS catalog without its .resx entry and was
                // shown to the user, literally, in all 40 languages.
                string[] keys =
                {
                    "app.unknown.error",
                    "errors.noMicrophone",
                    "errors.noModeSelected",
                    "errors.recordingStartFailed",
                    "audio.error.stopRecordingFailed",
                    "recording.state.transcribing",
                    "errors.unhandled.title",
                };

                foreach (var key in keys)
                {
                    Assert(HyperWhisper.Localization.Loc.S(key) != key,
                        $"'{key}' resolves to itself, so the user sees the raw key");
                }

                // And in every shipped culture, because a key added to Strings.resx
                // alone is a key that is English in one language and raw in 39.
                var cultures = new[] { "de", "ja", "fr", "ar", "zh-Hant", "pt", "tr" };
                var original = HyperWhisper.Resources.Strings.Culture;
                try
                {
                    foreach (var name in cultures)
                    {
                        HyperWhisper.Resources.Strings.Culture =
                            new System.Globalization.CultureInfo(name);
                        foreach (var key in keys)
                        {
                            Assert(HyperWhisper.Localization.Loc.S(key) != key,
                                $"'{key}' is missing from Strings.{name}.resx");
                        }
                    }
                }
                finally
                {
                    HyperWhisper.Resources.Strings.Culture = original;
                }
            });

            // ----- Review round 2 regressions ---------------------------------
            // Same rule as round 1: one case per finding whose defect a test can
            // express, naming the shape that was wrong. The four whose defect
            // needs a database, a WPF Application or a real capture device are
            // gated in scripts\verify_onboarding.ps1 instead, and say so there.

            Run("onboarding: the BYOK list is every key-taking vendor the app supports", () =>
            {
                // The list was hand-written and #393 shipped Meta MuseSTT with full
                // Windows BYOK support without growing it, so a first-run user who
                // paid for Meta simply could not pick it. Derive the expectation
                // from RequiresApiKey — the same predicate ProviderApiKeyWindow uses
                // — so the next vendor added to the enum fails here instead.
                var offered = LiveOnboardingProviderKeyGateway.ByokProviders;
                var keyless = LiveOnboardingProviderKeyGateway.KeylessProviders;

                var expected = Enum.GetValues<CloudTranscriptionProvider>()
                    .Where(p => p != CloudTranscriptionProvider.None)
                    .Where(p => p.RequiresApiKey())
                    .Where(p => !keyless.Contains(p))
                    .ToList();

                var missing = expected.Where(p => !offered.Contains(p)).ToList();
                Assert(missing.Count == 0,
                    "these vendors take an API key on Windows but first run does not offer them: "
                    + string.Join(", ", missing));

                var extra = offered.Where(p => !expected.Contains(p)).ToList();
                Assert(extra.Count == 0,
                    "these are offered on the BYOK branch but do not need a key: "
                    + string.Join(", ", extra));

                Assert(offered.Contains(CloudTranscriptionProvider.Meta),
                    "Meta MuseSTT is live on Windows with no feature gate and must be offered");

                // Merely listing it is not enough: with no key slot, Persist takes
                // its failure branch and rejects a correctly typed key with the
                // generic "could not save" string.
                Assert(LiveOnboardingProviderKeyGateway.TranscriptionKeyType(CloudTranscriptionProvider.Meta)
                        == TranscriptionApiKeyType.Meta,
                    "Meta needs its own key slot, or saving one lands in Persist's failure branch");
            });

            Run("onboarding: leaving Try It cancels the SAMPLE transcription too", () =>
            {
                // EndTryItStep cancelled only the microphone key. A sample clip
                // started here therefore survived Back with no chrome, and because
                // walking forward into Try It again resets TranscriptCameFromSample,
                // its result then rendered as the user's OWN recording — device name,
                // "recorded" pill and all.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                h.Audio.GateSample = true;
                h.Flow.TranscribeSampleClip();

                Assert(h.Audio.SampleTranscriptions == 1, "precondition: the sample transcription started");
                Assert(h.Flow.IsTranscribingSample, "precondition: the step says it is transcribing");
                Assert(h.Flow.HasInFlightWorkForTesting, "precondition: it is owned by the task box");

                h.Flow.Back();

                Assert(!h.Flow.HasInFlightWorkForTesting,
                    "Back must cancel the sample clip, not just the microphone recording");
                Assert(!h.Flow.IsTranscribingSample, "and clear the transcribing state");

                // And the late result is dropped rather than published into a step
                // the user has walked away from.
                h.Audio.ReleaseSample();
                h.LastTask.GetAwaiter().GetResult();
                Assert(h.Flow.Transcript.Length == 0,
                    "a sample transcript that lands after Back must not write flow state");
            });

            Run("onboarding: a Mode restore the database refuses keeps its restore point", () =>
            {
                // Restore() swallows database failures. Discarding the snapshot
                // regardless turned "your default Mode is still the staged one" into
                // a clean deferral, with nothing left to retry from. Same mechanism
                // as the credential rollback, not a second one.
                var h = new OnboardingHarness();
                h.StageInstalledOnDeviceModel();
                h.AdvanceTo(OnboardingStep.TryIt);

                Assert(h.Committer.ProductionState != FakeOnboardingCommitter.Seed,
                    "precondition: the staged source was applied");
                Assert(h.Flow.HasPendingProductionWrite, "precondition: there is something to roll back");

                h.Committer.RestoreSucceeds = false;
                h.Flow.DeferSetup();

                Assert(h.Committer.RestoreCount == 1, "the restore was attempted");
                Assert(h.Flow.ModeRestoreFailed, "the failure must be reported, not swallowed");
                Assert(h.Flow.HasPendingProductionWrite,
                    "a restore point that could not be spent must not be discarded");

                // A clean rollback still forgets it.
                var clean = new OnboardingHarness();
                clean.StageInstalledOnDeviceModel();
                clean.AdvanceTo(OnboardingStep.TryIt);
                clean.Flow.DeferSetup();

                Assert(!clean.Flow.ModeRestoreFailed, "a clean rollback reports nothing");
                Assert(clean.Committer.ProductionState == FakeOnboardingCommitter.Seed, "and puts the Mode back");
                Assert(!clean.Flow.HasPendingProductionWrite, "nothing is left to roll back");
            });

            Run("onboarding: an unflagged row on the seed id is snapshotted, not overwritten", () =>
            {
                // Apply's fallback builds a Mode on ModeDefaults.DefaultModeId and
                // SaveMode writes every column, so a row already sitting there was
                // silently clobbered and a deferral then DELETED it, because the
                // restore point recorded "no default existed". The Local API can
                // clear IsDefault on that very row.
                var flagged = new Mode { Id = Guid.NewGuid(), Name = "User's own", IsDefault = true };
                var seedRow = new Mode { Id = ModeDefaults.DefaultModeId, Name = "Hyper", IsDefault = false };
                var unrelated = new Mode { Id = Guid.NewGuid(), Name = "Email", IsDefault = false };

                var withFlag = LiveOnboardingSourceCommitter.SelectTargetMode(new[] { unrelated, seedRow, flagged });
                Assert(ReferenceEquals(withFlag, flagged),
                    "a flagged default always wins; that is macOS's findDefaultMode and must not change");

                var withoutFlag = LiveOnboardingSourceCommitter.SelectTargetMode(new[] { unrelated, seedRow });
                Assert(ReferenceEquals(withoutFlag, seedRow),
                    "with nothing flagged, the row already on the seed id is the target — "
                    + "so it is snapshotted and reconfigured in place rather than overwritten and deleted");

                var neither = LiveOnboardingSourceCommitter.SelectTargetMode(new[] { unrelated });
                Assert(neither is null,
                    "with neither, there is genuinely nothing to snapshot and Apply creates the row");

                // And the fallback must never reach for one of the user's OWN Modes,
                // which is what ModeService.GetDefaultMode's lowest-SortOrder rule
                // would have done.
                Assert(!ReferenceEquals(neither, unrelated), "an unrelated Mode is never the target");
            });

            Run("onboarding: the session refuses state changes and says which", () =>
            {
                // Round 1 put the guard on the two recording entry points. Round 2's
                // census found four more classes of writer — the changeMode hotkey,
                // the tray's Mode and Microphone submenus, and the Local API — and
                // they all ask THIS, at one funnel each, so a silently dropped write
                // is at least a logged one.
                Assert(!OnboardingSession.BlocksStateChange("test"),
                    "nothing is blocked while no session is open");

                try
                {
                    OnboardingSession.SetActive(true);
                    Assert(OnboardingSession.BlocksStateChange("change the active Mode"),
                        "a state change must be refused while the first-run window owns the app");
                }
                finally
                {
                    OnboardingSession.SetActive(false);
                }

                Assert(!OnboardingSession.BlocksStateChange("test"), "and allowed again once it closes");
            });

            Run("onboarding: the Local API refuses every mutating method while first run is open", () =>
            {
                // The server kept serving /modes and /transcribe against the exact
                // Mode row and shared orchestrator the flow stages and blind-restores:
                // a client PATCH landed, and Restore then destroyed it with no
                // version check and no log. The rule is per-METHOD so a new endpoint
                // inherits it.
                foreach (var method in new[] { "GET", "get", "HEAD" })
                {
                    Assert(LocalApiServer.IsReadOnlyMethod(method),
                        $"{method} only reads and must stay available");
                }

                foreach (var method in new[] { "POST", "PATCH", "PUT", "DELETE", "OPTIONS", "", null })
                {
                    Assert(!LocalApiServer.IsReadOnlyMethod(method),
                        $"'{method}' is not a read and must be refused while onboarding is open");
                }

                // ENGINE_UNAVAILABLE, not a fifteenth code: hw-localapi's wire codes
                // are a closed fourteen shared with macOS, and its conformance test
                // fails on anything else.
                Assert(HyperwhisperCoreMethods.LocalApiErrorCodeFromWireValue(
                           LocalApiErrorCode.EngineUnavailable) is not null,
                    "the refusal code must be one hw-localapi knows, or the macOS decoder breaks");
            });

            Run("onboarding: the UI dispatch runs inline when there is no WPF application", () =>
            {
                // The download-progress dictionary and the device-change handler both
                // marshal through this now. In the smoke harness there is no
                // Application, and a helper that quietly dropped the callback there
                // would make every case below it pass for the wrong reason.
                var ran = 0;
                OnboardingUiDispatch.Post(() => ran++);
                Assert(ran == 1, "with no Application the work must run inline, not be dropped");

                // A throwing handler must not escape: on the posted path it would
                // surface on the dispatcher, far from the OS callback that caused it.
                OnboardingUiDispatch.Post(() => throw new InvalidOperationException("handler blew up"));
                OnboardingUiDispatch.Post(() => ran++);
                Assert(ran == 2, "a throwing handler must not stop the next one");
            });

            Run("single instance: the mutex is per profile, not per product", () =>
            {
                // Not a review finding. The end-to-end demo could not run: a scratch
                // HYPERWHISPER_WINDOWS_APPDATA_ROOT instance was refused whenever any
                // other HyperWhisper was running, so the process lived 7 s and its log
                // never reached "APPLICATION STARTING". The guard exists to stop two
                // instances fighting over ONE app-data root, and a scratch profile
                // shares none of it — which is also the only way this head reaches
                // first run while the user's own copy is up.
                var original = Environment.GetEnvironmentVariable(
                    AppPaths.AppDataRootOverrideEnvironmentVariable);
                try
                {
                    Environment.SetEnvironmentVariable(
                        AppPaths.AppDataRootOverrideEnvironmentVariable, null);

                    Assert(!AppPaths.IsAppDataRootOverridden, "precondition: no override");
                    Assert(SingleInstanceGuard.MutexName == "HyperWhisper_SingleInstance_Mutex",
                        "the production mutex name must stay byte-identical, or an upgrade "
                        + "stops seeing the running instance it is replacing");
                    Assert(SingleInstanceGuard.MessageName == "HyperWhisper_ShowExistingInstance",
                        "and so must the activation message");
                    Assert(AppPaths.CredentialResource == "HyperWhisper",
                        "precondition: the credential resource is undecorated too");

                    var scratchA = Path.Combine(Path.GetTempPath(), "hw-smoke-profile-a");
                    Environment.SetEnvironmentVariable(
                        AppPaths.AppDataRootOverrideEnvironmentVariable, scratchA);

                    var mutexA = SingleInstanceGuard.MutexName;
                    Assert(mutexA != "HyperWhisper_SingleInstance_Mutex",
                        "an overridden root is a separate profile and must not be refused");
                    Assert(mutexA.StartsWith("HyperWhisper_SingleInstance_Mutex.Test.", StringComparison.Ordinal),
                        $"unexpected scratch mutex name '{mutexA}'");

                    // ONE hashing scheme, not two: the suffix is the same 16 hex
                    // digits CredentialResource already appends.
                    var credentialSuffix = AppPaths.CredentialResource.Split(".Test.")[1];
                    Assert(mutexA.EndsWith(credentialSuffix, StringComparison.Ordinal),
                        "the mutex suffix must be AppPaths.AppDataRootHash, the same fingerprint "
                        + "CredentialResource uses, rather than a second scheme");

                    var scratchB = Path.Combine(Path.GetTempPath(), "hw-smoke-profile-b");
                    Environment.SetEnvironmentVariable(
                        AppPaths.AppDataRootOverrideEnvironmentVariable, scratchB);
                    Assert(SingleInstanceGuard.MutexName != mutexA,
                        "two different scratch profiles must not collide with each other either");
                    Assert(SingleInstanceGuard.MessageName != "HyperWhisper_ShowExistingInstance",
                        "the activation broadcast is scoped too, or a third launch raises the wrong profile");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(
                        AppPaths.AppDataRootOverrideEnvironmentVariable, original);
                }
            });

            //
            // Deliberately last: constructing WPF objects installs a Dispatcher
            // SynchronizationContext, which the flow-model cases above detach
            // precisely because it never pumps in a console process.
            // =================================================================

            Run("onboarding: the window and all eight step pages initialize under WPF", () =>
            {
                var pages = BuildOnboardingStepPages(out var flow);

                Assert(pages.Count == OnboardingSteps.Count,
                    $"expected {OnboardingSteps.Count} step pages, built {pages.Count}");

                foreach (var (step, page) in pages)
                {
                    Assert(page.IsInitialized,
                        $"{page.GetType().Name} ({step}) did not finish WPF initialization");
                    Assert(ReferenceEquals(page.DataContext, flow),
                        $"{page.GetType().Name} must bind the window's flow model, not build one");
                }

                // The window itself: its resources, its footer bindings and its
                // step-to-page switch all parse here or not at all.
                var window = new OnboardingWindow(flow);
                Assert(window.IsInitialized, "OnboardingWindow did not finish WPF initialization");
                Assert(ReferenceEquals(window.DataContext, flow),
                    "OnboardingWindow must bind the flow model it was handed");
            });

            Run("onboarding: every step page is a scrolling stage, so no step can strand its footer", () =>
            {
                // macOS wraps each step in GeometryReader { ScrollView { ... } } so a
                // long step scrolls and a short one still centres. The Windows window
                // is fixed-size and cannot resize, so a step that does not scroll
                // simply loses its lower half on a 125% display.
                foreach (var (step, page) in BuildOnboardingStepPages(out _))
                {
                    var stage = FindDescendant<OnboardingStage>(page);
                    Assert(stage is not null,
                        $"{page.GetType().Name} ({step}) is not wrapped in an OnboardingStage");
                    Assert(stage!.Scroll is not null,
                        $"{page.GetType().Name} ({step}) has an OnboardingStage with no ScrollViewer");
                    Assert(stage.Scroll.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Auto,
                        $"{page.GetType().Name} ({step}) must scroll vertically on demand");
                }
            });

            Run("onboarding: the window never exceeds the work area it opens on", () =>
            {
                // 760 x 624 mirrors the macOS sheet and is right on a desktop. On a
                // 1366 x 768 laptop at 150% the whole work area is ~910 x 464 DIP, so
                // the designed height is a third taller than the screen - and the
                // window is NoResize with a custom caption, so a Continue button below
                // the bottom edge cannot be reached by dragging, by keyboard or by
                // maximizing. The stage is a ScrollViewer and takes the slack.

                // A desktop: nothing is clamped.
                var desktop = OnboardingWindow.FitToWorkArea(1920, 1032);
                Assert(desktop.Width == 760 && desktop.Height == 624,
                    $"a 1920x1032 work area must give the design size, got {desktop.Width}x{desktop.Height}");

                // 1366 x 768 at 150%: 910.67 x 512 DIP, less a 48 DIP taskbar.
                var laptop150 = OnboardingWindow.FitToWorkArea(1366 / 1.5, (768 - 72) / 1.5);
                Assert(laptop150.Width == 760,
                    $"150% still has room for the designed width; got {laptop150.Width}");
                Assert(laptop150.Height <= (768 - 72) / 1.5 + 0.001,
                    $"150% must clamp the height to the work area; got {laptop150.Height} " +
                    $"against {(768 - 72) / 1.5}");
                Assert(laptop150.Height < 624, "150% on a 768-tall screen has to clamp at all");

                // 1366 x 768 at 200%: 683 x 348 DIP. Now the width has to give too.
                var laptop200 = OnboardingWindow.FitToWorkArea(1366 / 2.0, (768 - 72) / 2.0);
                Assert(laptop200.Width <= 1366 / 2.0 + 0.001,
                    $"200% must clamp the width as well; got {laptop200.Width}");
                Assert(laptop200.Height <= (768 - 72) / 2.0 + 0.001,
                    $"200% must clamp the height; got {laptop200.Height}");

                Assert(laptop200.Height < 624, "200% on a 768-tall screen has to clamp at all");

                // The floor is a backstop against a nonsense work area, not a design
                // minimum: it has to sit BELOW the smallest real one, which is the
                // 200% case above. If a future floor rises past it, that case fails
                // first - this only pins that the floor exists at all.
                var absurd = OnboardingWindow.FitToWorkArea(10, 10);
                Assert(absurd.Width >= 480 && absurd.Height >= 320,
                    $"the floor must win over an impossible work area, got {absurd.Width}x{absurd.Height}");
                Assert(absurd.Height < (768 - 72) / 2.0,
                    $"the floor ({absurd.Height}) is above the 200% work area, so it would push " +
                    "the window off a 1366x768 laptop instead of protecting it");
            });

            Run("onboarding: the microphone step stops asking for speech when there is no device", () =>
            {
                // Found in a recording of the real flow on a box with no capture
                // device: the step still said "Say something. Watch the bars." and "If
                // the level moves when you talk, HyperWhisper can hear you." over an
                // honest "No microphone is connected" a few rows below.
                var h = new OnboardingHarness();

                h.Audio.PublishAvailability(OnboardingDeviceAvailability.Available);
                Assert(h.Flow.ShowsMicrophonePrompt,
                    "with a working microphone the prompt is the whole point of the step");
                var withDevice = h.Flow.MicrophoneStepTitle;

                foreach (var dead in new[]
                         {
                             OnboardingDeviceAvailability.NoDevices,
                             OnboardingDeviceAvailability.Blocked,
                             OnboardingDeviceAvailability.EnumerationFailed
                         })
                {
                    h.Audio.PublishAvailability(dead);

                    Assert(!h.Flow.ShowsMicrophonePrompt,
                        $"{dead}: the flow must not ask the user to watch a level it cannot show");
                    Assert(h.Flow.MicrophoneStepTitle != withDevice,
                        $"{dead}: the step's question must change too - the title IS the prompt");
                    Assert(!string.IsNullOrWhiteSpace(h.Flow.MicrophoneStepTitle),
                        $"{dead}: the step still needs a question");

                    // And the honest diagnosis is still the thing that explains it.
                    Assert(!string.IsNullOrWhiteSpace(h.Flow.MicrophoneHintText),
                        $"{dead}: suppressing the prompt must not suppress the explanation");
                }
            });

            Run("onboarding: no footer button takes its height from the 56px footer band", () =>
            {
                // RAY'S OWN COMPLAINT, FROM WATCHING THE RECORDING: "the back button
                // and the continue button are way too tall compared to a Windows
                // application."
                //
                // The footer is a fixed 56 row and the three buttons carried no
                // VerticalAlignment, so WPF's default Stretch made every one of them
                // render the full 56 - close to double what the same styles render on
                // any Settings page, where a button sits in a StackPanel and is never
                // stretched.
                //
                // This is asserted by MEASURING and not by grepping for an attribute:
                // the defect is a rendered height, an attribute can be added in the
                // wrong place, and a future container change could reintroduce the
                // stretch with every attribute still present.
                // Goes through the shared builder so the Application and its three
                // theme dictionaries exist however this case is ordered.
                BuildOnboardingStepPages(out var footerFlow);
                var window = new OnboardingWindow(footerFlow);

                var back = window.FindName("BackButton") as System.Windows.Controls.Button;
                var setUpLater = window.FindName("SetUpLaterButton") as System.Windows.Controls.Button;
                var primary = window.FindName("PrimaryButton") as System.Windows.Controls.Button;
                Assert(back is not null && setUpLater is not null && primary is not null,
                    "the footer no longer has all three named buttons");

                // Back and Set Up Later are bound to flow flags that are false on step
                // one. A collapsed button measures 0, which would pass this check
                // without proving anything, so they are made visible for the layout
                // pass; setting a local value replaces the binding on this throwaway
                // window only.
                back!.Visibility = Visibility.Visible;
                setUpLater!.Visibility = Visibility.Visible;

                // A Window that was never shown cannot be laid out, but its content
                // can. The root Grid IS the four-band layout, footer row included.
                var root = window.Content as FrameworkElement;
                Assert(root is not null, "OnboardingWindow no longer has a FrameworkElement root");
                window.Content = null;
                root!.DataContext = footerFlow;
                root.Measure(new Size(760, 624));
                root.Arrange(new Rect(0, 0, 760, 624));
                root.UpdateLayout();

                // The reference is not a number in this file. It is the height the
                // SAME styles render at on a real Settings page, measured in the same
                // process: BackupExportSettingsPage's Export button is
                // PrimaryButtonStyle with VerticalAlignment=Center and no other
                // metrics, which is exactly what a Windows button in this app is.
                var settingsPage = new BackupExportSettingsPage();
                settingsPage.Measure(new Size(760, 624));
                settingsPage.Arrange(new Rect(0, 0, 760, 624));
                settingsPage.UpdateLayout();
                var reference = settingsPage.ExportButton.ActualHeight;

                Assert(reference > 20 && reference < 40,
                    $"the Settings reference button measured {reference:F2}, which is not a plausible " +
                    "Windows button height - the reference itself is wrong, not the footer");

                foreach (var (name, button) in new[]
                         {
                             ("BackButton", back!),
                             ("SetUpLaterButton", setUpLater!),
                             ("PrimaryButton", primary!)
                         })
                {
                    var height = button.ActualHeight;

                    Assert(height > 0,
                        $"{name} measured 0 - it never took part in the layout pass, so this " +
                        "case proves nothing about it");

                    // The bug, stated as the bug: the button must not be as tall as
                    // the band it sits in.
                    Assert(height < 56,
                        $"{name} rendered {height:F2} tall, the full height of the 56 footer band. " +
                        "It is being stretched by its container again.");

                    // And it must match what the rest of the app renders. One pixel of
                    // tolerance covers the 12 vs 13 FontSize and the border on
                    // MacButtonStyle; anything wider than that is a different control.
                    Assert(Math.Abs(height - reference) <= 1.0,
                        $"{name} rendered {height:F2} tall against a Settings page button's " +
                        $"{reference:F2}. Onboarding must look like the rest of the app.");
                }
            });

            Run("onboarding: no control on any step page is stretched by its container", () =>
            {
                // The footer was the only place a FIXED row forced the stretch, but the
                // same default is one sibling away from doing it on any card row: a
                // Grid row is as tall as its tallest child, so a wrapped label next to
                // a button silently makes the button match it.
                //
                // The four selectable styles are the deliberate exception and say so in
                // OnboardingResources.xaml: a row or card that IS the tap target has to
                // fill its slot. They are recognised by their template's own Border
                // padding rather than by name, so renaming a style cannot slip a
                // stretched button past this.
                foreach (var (step, page) in BuildOnboardingStepPages(out _))
                {
                    foreach (var control in DescendantsOf<System.Windows.Controls.Control>(page))
                    {
                        if (control is not (System.Windows.Controls.Button
                                            or System.Windows.Controls.ComboBox
                                            or System.Windows.Controls.TextBox
                                            or System.Windows.Controls.PasswordBox))
                            continue;

                        if (control.VerticalAlignment != VerticalAlignment.Stretch)
                            continue;

                        // A stretched control is only acceptable when it was MEANT to
                        // be one: the selectable row and card styles set Stretch
                        // explicitly and carry a Command, i.e. the whole thing is the
                        // click target.
                        Assert(control is System.Windows.Controls.Primitives.ButtonBase { Command: not null },
                            $"{page.GetType().Name} ({step}) has a stretched " +
                            $"{control.GetType().Name} with no explicit height or alignment - " +
                            "its container decides how tall it is");
                    }
                }
            });

            Run("onboarding: no step page renders a raw onboarding.* key", () =>
            {
                // Loc.S returns the KEY when a resource is missing, and {loc:Loc}
                // resolves once at parse time, so a typo or a key that never reached
                // Strings.resx shows up on screen as "onboarding.mic.title". After a
                // layout pass every {loc:Loc} and every bound VM string has run, so
                // the visual tree is the honest place to look for one.
                foreach (var (step, page) in BuildOnboardingStepPages(out _))
                {
                    foreach (var text in VisualTextOf(page))
                    {
                        Assert(!text.StartsWith("onboarding.", StringComparison.Ordinal),
                            $"{page.GetType().Name} ({step}) renders the unresolved key '{text}'");
                    }
                }
            });

            Run("single instance: a second profile boots, but never takes the global keyboard", () =>
            {
                // C10. Making the mutex per-profile was deliberate and is what lets
                // a scratch-profile instance run beside the user's own app, which is
                // what every dev box GUI test depends on. It also removed the only
                // barrier to two processes fighting over machine-global input:
                // WH_KEYBOARD_LL is per PROCESS and non-exclusive, so one press of
                // the default Ctrl+Alt toggle started a recording in BOTH, and
                // RegisterHotKey failed with 1409 against the app's own registration.
                //
                // The two facts are now separate. This suite runs with the app-data
                // root overridden, so it IS a secondary instance.
                Assert(AppPaths.IsAppDataRootOverridden,
                    "precondition: the smoke suite runs on an overridden app-data root");

                Assert(SingleInstanceGuard.MutexName != "HyperWhisper_SingleInstance_Mutex",
                    "the mutex must stay per profile, or the scratch-profile GUI test route dies");
                Assert(SingleInstanceGuard.MutexName.Contains(AppPaths.AppDataRootHash, StringComparison.Ordinal),
                    "and must be keyed on the one profile hash this head already uses");

                Assert(!SingleInstanceGuard.OwnsGlobalInput,
                    "a secondary instance must not install hooks or register hotkeys by default");

                // The escape hatch, for testing hotkeys from a scratch profile with
                // everything else closed. Parsed here so the test never has to write
                // to the environment of whoever runs it.
                foreach (var yes in new[] { "1", "true", "TRUE", "Yes", " 1 " })
                {
                    Assert(SingleInstanceGuard.EvaluateGlobalInputOverride(yes),
                        $"'{yes}' must opt back in");
                }

                foreach (var no in new string?[] { null, "", "   ", "0", "no", "false", "maybe" })
                {
                    Assert(!SingleInstanceGuard.EvaluateGlobalInputOverride(no),
                        $"'{no ?? "<null>"}' must not opt back in - an empty value is how PowerShell unsets");
                }
            });

            Run("delivery: a refused clipboard write is reported unless the gate refused it", () =>
            {
                // C3. Honouring CopyToClipboard's return value was right, and it
                // mapped the PRE-EXISTING clipboard failures - a clipboard manager,
                // an RDP monitor, Excel holding the Win32 clipboard - onto
                // SmartPasteResult.Failed, which has an empty arm in one switch and
                // no arm at all in the other. A wrong "Copied" overlay became total
                // silence over a transcript that reached nothing.
                var previous = TextDeliveryGate.IsSuppressed;
                try
                {
                    TextDeliveryGate.SetSuppressed(false);
                    Assert(MainViewModel.ShouldReportUndeliveredTranscript(),
                        "a clipboard failure with no onboarding window open has to be reported");

                    // And the round-1 rule this must not undo: while first run is
                    // open the gate refuses delivery ON PURPOSE, the Try It panel
                    // shows the transcript itself, and a toast behind an
                    // application-modal window is unreachable anyway.
                    TextDeliveryGate.SetSuppressed(true);
                    Assert(!MainViewModel.ShouldReportUndeliveredTranscript(),
                        "a deliberate refusal is not a failure to report");
                }
                finally
                {
                    TextDeliveryGate.SetSuppressed(previous);
                }
            });

            Run("shortcuts: the recorder's red border never appears without its reason", () =>
            {
                // C8. ShowError gated only the TEXT on ShowsInlineError and painted
                // the border unconditionally, so the one box declared
                // ShowsInlineError="False" turned red with nothing saying why. The
                // page it was lifted from drew neither for that role.
                var box = new ShortcutRecorderBox();
                Assert(box.ShowsInlineError,
                    "the default is to explain a rejected chord, and every host now takes it");

                // The chord the finding names is still rejected; the recorder's own
                // rule for that has not moved.
                Assert(new KeyboardShortcut { Control = true }.IsSingleBareModifier,
                    "precondition: a bare Ctrl is what the push-to-talk box rejected");

                box.ShowError("Single modifier shortcuts are not supported.");

                Assert(!string.IsNullOrWhiteSpace(box.ErrorMessage), "the verdict is recorded");
                Assert(box.ErrorText.Visibility == Visibility.Visible,
                    "and the reason is on screen beside the border");
                Assert(box.Field.BorderThickness.Left > 1,
                    "precondition: the border is what the user actually sees change");

                // No host may take the border without the line again. The one that
                // did produced a field that turned red with nothing explaining it.
                var silent = new ShortcutRecorderBox { ShowsInlineError = false };
                silent.ShowError("rejected");
                Assert(silent.ErrorText.Visibility != Visibility.Visible,
                    "precondition: this host wants no line");
                Assert(silent.Field.BorderThickness.Left <= 1,
                    "so it must get no border either - the two are one thing");

                // Nothing cleared it before: ClearError had one caller, there was no
                // focus handler, and the settings page's reset writes DisplayText
                // and nothing else.
                box.DisplayText = "Ctrl+Shift+F9";
                Assert(box.ErrorMessage is null, "re-seeding the field clears the last verdict");
                Assert(box.ErrorText.Visibility != Visibility.Visible, "text and border go together");
                Assert(box.Field.BorderThickness.Left <= 1, "including the border");
            });

            Console.WriteLine(_failures == 0
                ? "All smoke tests passed."
                : $"{_failures} smoke test(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(OnboardingLaunchPolicy.SkipEnvironmentVariable, null);

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup; a failed delete must not mask the smoke result.
            }
        }
    }

    // =========================================================================
    // Harness
    // =========================================================================

    private static void Run(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine($"  ok   {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine($"  FAIL {name}");
            Console.Error.WriteLine($"       {ex}");
        }
    }

    private static void RunAsync(string name, Func<Task> check)
        => Run(name, () => check().GetAwaiter().GetResult());

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>
    /// The tag-free text of a run sequence — what the old assertions got by
    /// calling <c>InlineHtml.PlainText</c> on a block's inner HTML, now that the
    /// blocks arrive already parsed (#284).
    /// </summary>
    private static string RunText(IReadOnlyList<HtmlRun> runs)
        => string.Concat(runs.Select(run => run.Text));

    /// <summary>
    /// Compares one field of a backup-vectors.json expectation against the value the
    /// native mapper produced. JSON <c>null</c> means the field must come out null —
    /// the absent-stays-absent rule the vectors exist to pin.
    /// </summary>
    private static void AssertModeVectorField(
        string label,
        string field,
        JsonElement expected,
        string? actual)
    {
        Assert(expected.TryGetProperty(field, out var element),
            $"vector '{label}' is missing the expected field '{field}'");

        var want = element.ValueKind == JsonValueKind.Null ? null : element.GetString();
        Assert(want == actual,
            $"vector '{label}': {field} expected {Quote(want)}, got {Quote(actual)}");

        static string Quote(string? value) => value is null ? "null" : $"'{value}'";
    }

    /// <summary>
    /// The serializer BackupService writes the universal document with
    /// (<c>BackupService.UniversalJsonOptions</c>, minus <c>WriteIndented</c>) and the
    /// one <c>UniversalBackupMapper</c> uses internally. Only
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> is load-bearing here — every
    /// Universal* property carries an explicit <c>[JsonPropertyName]</c>, so the naming
    /// policy never decides a key — but it is kept identical so the captured shape is
    /// the shape that reaches a user's .hwbackup.json.
    /// </summary>
    private static readonly JsonSerializerOptions UniversalCaptureOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Compares a backup-vectors.json settings expectation against what the native
    /// adapter produced. <see cref="JsonNode.DeepEquals"/> is insensitive to key order
    /// and to number representation, so a row pins VALUES, never formatting — and it
    /// compares whole objects, so an unexpected EXTRA key fails just as loudly as a
    /// missing one.
    /// </summary>
    /// <remarks>
    /// The message names the OFFENDING KEYS on its FIRST LINE, the way
    /// <see cref="AssertModeVectorField"/> does — <c>advanced.maxRecordingDuration
    /// expected absent, got 600</c> — because windows-ci is the only instrument that
    /// can run this file and a round trip costs ~12 minutes. A bare "mismatch"
    /// followed by two whole settings blobs to eyeball-diff spends that round trip
    /// without saying what differed. The full blobs are still printed underneath, so
    /// nothing that used to be in the log is lost.
    /// </remarks>
    private static void AssertVectorJson(string label, string what, JsonNode? expected, JsonNode? actual)
    {
        if (JsonNode.DeepEquals(expected, actual)) return;

        var differences = new List<string>();
        Describe(differences, path: "", expected, actual);
        if (differences.Count == 0)
            differences.Add($"whole value expected {Render(expected)}, got {Render(actual)}");

        // Cap the first line: a wholesale shape change must not bury the log.
        const int shown = 8;
        var head = string.Join("; ", differences.Take(shown));
        if (differences.Count > shown)
            head += $"; (+{differences.Count - shown} more)";

        throw new InvalidOperationException(
            $"vector '{label}': {what} mismatch — {head}{Environment.NewLine}"
            + $"  expected {Render(expected)}{Environment.NewLine}"
            + $"  actual   {Render(actual)}");

        // Walks both trees together and records one entry per differing LEAF, so the
        // message reports the dotted key plus both values rather than the whole
        // section. An absent key and an explicit JSON null read differently on
        // purpose: that distinction is exactly what several rows exist to pin.
        static void Describe(List<string> into, string path, JsonNode? expected, JsonNode? actual)
        {
            if (JsonNode.DeepEquals(expected, actual)) return;

            if (expected is JsonObject wantObject && actual is JsonObject gotObject)
            {
                var keys = wantObject.Select(p => p.Key)
                    .Concat(gotObject.Select(p => p.Key))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal);

                foreach (var key in keys)
                {
                    var child = path.Length == 0 ? key : $"{path}.{key}";
                    var hasWant = wantObject.TryGetPropertyValue(key, out var want);
                    var hasGot = gotObject.TryGetPropertyValue(key, out var got);

                    if (!hasWant) into.Add($"{child} expected absent, got {Render(got)}");
                    else if (!hasGot) into.Add($"{child} expected {Render(want)}, got absent");
                    else Describe(into, child, want, got);
                }
                return;
            }

            if (expected is JsonArray wantArray && actual is JsonArray gotArray
                && wantArray.Count == gotArray.Count)
            {
                for (var i = 0; i < wantArray.Count; i++)
                    Describe(into, $"{path}[{i}]", wantArray[i], gotArray[i]);
                return;
            }

            into.Add($"{(path.Length == 0 ? "<root>" : path)} expected "
                + $"{Render(expected)}, got {Render(actual)}");
        }

        static string Render(JsonNode? node) => node?.ToJsonString() ?? "null";
    }

    /// <summary>
    /// Writes a backup-vectors.json <c>native</c> block onto the live SettingsService.
    /// Keys are the NATIVE names — settings.json is PascalCase — and every one of them
    /// goes through the real public setter, so the row is seeded through exactly the
    /// path a restore uses, dirty-check, Save() and all. An unrecognized key throws:
    /// a typo in the vectors must fail, not silently seed nothing.
    /// </summary>
    private static void SeedWindowsSettings(SettingsService settings, JsonObject native, string label)
    {
        foreach (var entry in native)
        {
            var value = entry.Value
                ?? throw new InvalidOperationException(
                    $"vector '{label}': native settings key '{entry.Key}' is null; "
                    + "a seed must be an explicit value");

            switch (entry.Key)
            {
                case "LaunchMinimized": settings.LaunchMinimized = value.GetValue<bool>(); break;
                case "ShowRecordingWindow": settings.ShowRecordingWindow = value.GetValue<bool>(); break;
                case "CheckForUpdatesAutomatically": settings.CheckForUpdatesAutomatically = value.GetValue<bool>(); break;
                case "EnableErrorLogging": settings.EnableErrorLogging = value.GetValue<bool>(); break;
                case "ShareAnonymousSpeedData": settings.ShareAnonymousSpeedData = value.GetValue<bool>(); break;
                case "EnableSoundEffects": settings.EnableSoundEffects = value.GetValue<bool>(); break;
                case "AutoPasteEnabled": settings.AutoPasteEnabled = value.GetValue<bool>(); break;
                case "RemoveFillerWords": settings.RemoveFillerWords = value.GetValue<bool>(); break;
                case "RestoreClipboardAfterPaste": settings.RestoreClipboardAfterPaste = value.GetValue<bool>(); break;
                case "HideFromClipboardHistory": settings.HideFromClipboardHistory = value.GetValue<bool>(); break;
                case "ClipboardRestoreDelaySeconds": settings.ClipboardRestoreDelaySeconds = value.GetValue<double>(); break;
                case "AutocapitalizeInsert": settings.AutocapitalizeInsert = value.GetValue<bool>(); break;
                case "StoreAsM4A": settings.StoreAsM4A = value.GetValue<bool>(); break;
                case "KeepAudioFiles": settings.KeepAudioFiles = value.GetValue<bool>(); break;
                case "MaxRecordingDuration": settings.MaxRecordingDurationSeconds = value.GetValue<int>(); break;
                case "StreamingEnabled": settings.StreamingEnabled = value.GetValue<bool>(); break;
                case "StreamingProvider": settings.StreamingProvider = value.GetValue<string>(); break;
                case "StreamingLanguage": settings.StreamingLanguage = value.GetValue<string>(); break;
                case "StreamingDeepgramModel": settings.StreamingDeepgramModel = value.GetValue<string>(); break;
                case "StreamingCloudTier": settings.StreamingCloudTier = value.GetValue<string>(); break;
                case "StreamingFastFormatting": settings.StreamingFastFormatting = value.GetValue<bool>(); break;
                case "StreamingShortcut": settings.StreamingShortcut = KeyboardShortcut.FromPersistedString(value.GetValue<string>()); break;
                case "TypingSpeedWPM": settings.TypingSpeedWPM = value.GetValue<int>(); break;
                case "MinimizeToTray": settings.MinimizeToTray = value.GetValue<bool>(); break;
                case "ThemeMode": settings.ThemeMode = (HyperWhisper.Models.ThemeMode)value.GetValue<int>(); break;
                case "AutoDeleteEnabled": settings.AutoDeleteEnabled = value.GetValue<bool>(); break;
                case "AutoDeleteDaysOld": settings.AutoDeleteDaysOld = value.GetValue<int>(); break;
                case "ParakeetEnabled": settings.ParakeetEnabled = value.GetValue<bool>(); break;
                case "KeepMicrophoneWarm": settings.KeepMicrophoneWarm = value.GetValue<bool>(); break;
                case "MediaControlMode": settings.MediaControlMode = value.GetValue<string>(); break;
                case "ToggleShortcut": settings.ToggleShortcut = KeyboardShortcut.FromPersistedString(value.GetValue<string>()); break;
                case "CancelShortcut": settings.CancelShortcut = KeyboardShortcut.FromPersistedString(value.GetValue<string>()); break;
                case "ChangeModeShortcut": settings.ChangeModeShortcut = KeyboardShortcut.FromPersistedString(value.GetValue<string>()); break;
                case "AutoIncreaseMicVolume": settings.AutoIncreaseMicVolume = value.GetValue<bool>(); break;
                default:
                    throw new InvalidOperationException(
                        $"vector '{label}': unknown Windows native settings key '{entry.Key}'");
            }
        }
    }

    /// <summary>
    /// Drives one <c>unknownKeyRoundTrip</c> row of kind <c>settingsUnknownKey</c>:
    /// import the row's settings block, check what was persisted, then re-export and
    /// check the captured key comes back at its ORIGINAL PATH.
    /// </summary>
    private static void RunSettingsUnknownKeyRow(
        SettingsService settings, JsonObject row, string label)
    {
        ImportSettingsJson(settings, row["importedSettings"]!);
        if (row["thenImportedSettings"] is { } second) ImportSettingsJson(settings, second);

        var expectedStored = row["expectedStored"];
        var actualStored = settings.BackupUnknownSettings is null
            ? null
            : JsonNode.Parse(settings.BackupUnknownSettings);
        AssertVectorJson(label, "SettingsData.BackupUnknownSettings", expectedStored, actualStored);

        var exported = JsonSerializer.SerializeToNode(
            UniversalBackupMapper.MapSettings(settings), UniversalCaptureOptions)!.AsObject();

        foreach (var expected in row["expectedReExportedPaths"]!.AsObject())
        {
            var actual = ResolvePath(exported, expected.Key);
            Assert(actual is not null,
                $"vector '{label}': re-export lost '{expected.Key}'. That is the #288 bug.");
            AssertVectorJson(label, $"re-export at '{expected.Key}'", expected.Value, actual);
        }

        foreach (var forbidden in row["forbiddenReExportedPaths"]!.AsArray())
        {
            var path = forbidden!.GetValue<string>();
            Assert(ResolvePath(exported, path) is null,
                $"vector '{label}': re-export put a preserved key at '{path}'. A captured "
                + "settings key must come back under its own SECTION, never at the root — "
                + "the root is a schema violation.");
        }

        static JsonNode? ResolvePath(JsonObject root, string dotted)
        {
            JsonNode? node = root;
            foreach (var segment in dotted.Split('.'))
            {
                if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node))
                    return null;
            }
            return node;
        }
    }

    /// <summary>
    /// Deserializes a vector settings block exactly as BackupService would (so the
    /// <c>[JsonExtensionData]</c> bags fill), then applies it.
    /// </summary>
    private static void ImportSettingsJson(SettingsService settings, JsonNode block)
    {
        var universal = JsonSerializer.Deserialize<UniversalSettings>(
            block.ToJsonString(), UniversalCaptureOptions)
            ?? throw new InvalidOperationException("settings block did not deserialize");
        UniversalBackupMapper.ApplySettings(universal, settings);
    }

    /// <summary>
    /// Drives one <c>unknownKeyRoundTrip</c> row of kind
    /// <c>topLevelPlatformExtensions</c> for the Windows head.
    /// </summary>
    private static void RunForeignSliceRow(SettingsService settings, JsonObject row, string label)
    {
        var imported = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            row["imported"]!.ToJsonString())
            ?? throw new InvalidOperationException($"vector '{label}': imported did not deserialize");

        UniversalBackupMapper.ApplyWindowsPlatformSettings(imported, settings);

        // Windows stores ONLY the foreign slices and adds its own at export time.
        // Linux stores the whole imported map instead and overwrites its own slice on
        // the way out; the row records both, because the observable contract below is
        // what has to match, not the storage strategy.
        var expectedStored = row["expectedStoredByHead"]!["windows"];
        var actualStored = settings.BackupForeignPlatformExtensions is null
            ? null
            : JsonNode.Parse(settings.BackupForeignPlatformExtensions);
        AssertVectorJson(label, "SettingsData.BackupForeignPlatformExtensions",
            expectedStored, actualStored);

        // Our own slice must be REBUILT from live settings on every export, not
        // replayed from anything preserved. Flip a live Windows-only value after the
        // import: if the export still shows the imported value, the own slice lost to
        // a stale copy — the exact failure MapMode's per-mode rule already prevents.
        settings.MinimizeToTray = !settings.MinimizeToTray;

        var reExported = UniversalBackupMapper.BuildPlatformExtensions(settings);
        var actualKeys = reExported.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var wantKeys = row["expectedReExportedKeysByHead"]!["windows"]!.AsArray()
            .Select(k => k!.GetValue<string>())
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert(actualKeys.SequenceEqual(wantKeys, StringComparer.Ordinal),
            $"vector '{label}': re-exported top-level platformExtensions keys were "
            + $"[{string.Join(", ", actualKeys)}], expected [{string.Join(", ", wantKeys)}]");

        var windowsSlice = JsonNode.Parse(reExported["windows"].GetRawText())!.AsObject();
        Assert(windowsSlice["settings"]!["minimizeToTray"]!.GetValue<bool>() == settings.MinimizeToTray,
            $"vector '{label}': the \"windows\" slice must be rebuilt from live settings and "
            + "overwrite any preserved copy of itself.");

        // And the foreign slices must have come through verbatim.
        foreach (var expected in row["expectedStoredByHead"]!["windows"] as JsonObject ?? [])
        {
            Assert(reExported.ContainsKey(expected.Key),
                $"vector '{label}': the preserved '{expected.Key}' slice was not re-emitted");
            AssertVectorJson(label, $"re-emitted '{expected.Key}' slice", expected.Value,
                JsonNode.Parse(reExported[expected.Key].GetRawText()));
        }
    }

    /// <summary>
    /// Reads back the same native key set <see cref="SeedWindowsSettings"/> writes, as
    /// JSON, so an import row can be compared whole rather than field by field.
    /// </summary>
    private static JsonObject ReadWindowsSettings(SettingsService settings) => new()
    {
        ["LaunchMinimized"] = settings.LaunchMinimized,
        ["ShowRecordingWindow"] = settings.ShowRecordingWindow,
        ["CheckForUpdatesAutomatically"] = settings.CheckForUpdatesAutomatically,
        ["EnableErrorLogging"] = settings.EnableErrorLogging,
        ["ShareAnonymousSpeedData"] = settings.ShareAnonymousSpeedData,
        ["EnableSoundEffects"] = settings.EnableSoundEffects,
        ["AutoPasteEnabled"] = settings.AutoPasteEnabled,
        ["RemoveFillerWords"] = settings.RemoveFillerWords,
        ["RestoreClipboardAfterPaste"] = settings.RestoreClipboardAfterPaste,
        ["HideFromClipboardHistory"] = settings.HideFromClipboardHistory,
        ["ClipboardRestoreDelaySeconds"] = settings.ClipboardRestoreDelaySeconds,
        ["AutocapitalizeInsert"] = settings.AutocapitalizeInsert,
        ["StoreAsM4A"] = settings.StoreAsM4A,
        ["KeepAudioFiles"] = settings.KeepAudioFiles,
        ["StreamingEnabled"] = settings.StreamingEnabled,
        ["StreamingProvider"] = settings.StreamingProvider,
        ["StreamingLanguage"] = settings.StreamingLanguage,
        ["StreamingDeepgramModel"] = settings.StreamingDeepgramModel,
        ["StreamingCloudTier"] = settings.StreamingCloudTier,
        ["StreamingFastFormatting"] = settings.StreamingFastFormatting,
        ["StreamingShortcut"] = settings.StreamingShortcut.ToPersistedString(),
        ["TypingSpeedWPM"] = settings.TypingSpeedWPM,
        ["MaxRecordingDuration"] = settings.MaxRecordingDurationSeconds,
        ["MinimizeToTray"] = settings.MinimizeToTray,
        ["ThemeMode"] = (int)settings.ThemeMode,
        ["AutoDeleteEnabled"] = settings.AutoDeleteEnabled,
        ["AutoDeleteDaysOld"] = settings.AutoDeleteDaysOld,
        ["ParakeetEnabled"] = settings.ParakeetEnabled,
        ["KeepMicrophoneWarm"] = settings.KeepMicrophoneWarm,
        ["MediaControlMode"] = settings.MediaControlMode,
        ["ToggleShortcut"] = settings.ToggleShortcut.ToPersistedString(),
        ["CancelShortcut"] = settings.CancelShortcut.ToPersistedString(),
        ["ChangeModeShortcut"] = settings.ChangeModeShortcut.ToPersistedString(),
        ["AutoIncreaseMicVolume"] = settings.AutoIncreaseMicVolume,
    };

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name} was not thrown");
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name} was not thrown");
    }

    /// <summary>
    /// A live-streaming session config with every credential filled in, so a
    /// strategy built from it reaches the shared core rather than short-circuiting
    /// on a missing key.
    /// </summary>
    private static StreamingSessionConfig LiveConfig(
        string? language = "en",
        IReadOnlyList<string>? vocabulary = null,
        string? model = null,
        bool fastFormatting = false)
        => new(
            LicenseKey: "smoke-license-key",
            DeviceId: "smoke-device-id",
            Language: language,
            Vocabulary: vocabulary,
            ApiKey: "test-api-key",
            Model: model,
            FastFormatting: fastFormatting,
            RemoveFillerWords: false);

    /// <summary>
    /// The one streaming strategy (#281), driven through the real FFI. Every
    /// assertion below that reads a URL, a frame, a parsed event or a stop step
    /// is reading what the shared Rust core produced on this machine, not a C#
    /// copy of it.
    /// </summary>
    /// <param name="nowMs">
    /// Injected monotonic clock. The core reads none of its own, so OpenAI's
    /// 1.2 s commit interval and Deepgram's 3 s keepalive are driven by moving
    /// this instead of sleeping.
    /// </param>
    private static LiveProtocolStreamingStrategy LiveStrategy(
        StreamingTranscriptionProvider provider,
        StreamingSessionConfig? config = null,
        Func<long>? nowMs = null)
        => new(provider, config ?? LiveConfig(), nowMs);

    /// <summary>
    /// Build a post-processing request through the shared core (#282). The URL,
    /// the auth headers and the body shape all come from Rust, so a smoke test
    /// that reads them back is checking the real builder, not a local copy.
    /// </summary>
    private static HttpRequestMessage BuildLlmRequest(
        PortableLlmProvider provider,
        string model,
        string? customEndpoint = null)
        => LlmPostProcessing.BuildRequest(new PortableLlmRequest(
            provider,
            model,
            ApiKey: "smoke-key",
            SystemPrompt: "system",
            SystemInfo: "",
            Transcript: "user",
            CustomEndpoint: customEndpoint));

    /// <summary>Parse the JSON body of a shared-core post-processing request.</summary>
    private static JsonDocument ReadLlmBody(
        PortableLlmProvider provider,
        string model,
        string? customEndpoint = null)
    {
        using var request = BuildLlmRequest(provider, model, customEndpoint);
        var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonDocument.Parse(json);
    }

    /// <summary>Minimal core-shaped request; the stub handler never reads it.</summary>
    private static uniffi.hyperwhisper_core.HttpRequest BuildDummyRequest()
        => new(
            uniffi.hyperwhisper_core.HttpMethod.Get,
            "http://127.0.0.1:9/smoke",
            new List<Header>(),
            new Body.Empty());

    /// <summary>
    /// Scripted HttpMessageHandler that also records where the call went.
    /// Set <see cref="Next"/> before each send. Used by the custom-endpoint test
    /// checks, which assert on the request as well as on the reply.
    /// </summary>
    /// <summary>
    /// Everything the Local API's <c>file</c> containment guard looks at, in one
    /// string: the trusted roots, and each ancestor of <paramref name="path"/>
    /// that is a reparse point or that cannot be resolved. The guard's own
    /// refusal is deliberately uniform — it must not leak whether a path exists
    /// — so a test that trips it has nothing to report without this.
    /// </summary>
    private static string TrustedPathDiagnostics(string path)
    {
        var report = new System.Text.StringBuilder();
        report.Append($"path={path}; tempRoot={AppPaths.ProfileTempRecordingsDirectory}; ");
        try { report.Append($"recordings={StorageService.Instance.GetRecordingsFolder()}; "); }
        catch (Exception ex) { report.Append($"recordings=<{ex.GetType().Name}>; "); }
        try { report.Append($"legacy={SettingsService.GetLegacyAudioFolder()}; "); }
        catch (Exception ex) { report.Append($"legacy=<{ex.GetType().Name}>; "); }
        report.Append($"lexicalTrusted={HistoryService.IsTrustedAudioPath(path)}; chain=[");

        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)!)
        {
            try
            {
                var target = File.Exists(current)
                    ? File.ResolveLinkTarget(current, returnFinalTarget: true)
                    : Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target != null) report.Append($"{current} -> {target.FullName}; ");
            }
            catch (Exception ex)
            {
                report.Append($"{current} !! {ex.GetType().Name}: {ex.Message}; ");
            }
        }
        return report.Append(']').ToString();
    }

    /// <summary>
    /// Run <paramref name="act"/> and return the <c>ApiInputException</c> it was
    /// supposed to throw. Failing to throw is itself the failure, and says so —
    /// a bare try/catch would report "the guard is missing" as a pass.
    /// </summary>
    private static HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApiInputException
        CaptureApiInputException(Action act)
    {
        try
        {
            act();
        }
        catch (HyperWhisper.Services.LocalApi.Endpoints.TranscribeEndpoints.ApiInputException ex)
        {
            return ex;
        }
        throw new InvalidOperationException(
            "expected the size guard to refuse this input, but ResolveAudioSource returned normally");
    }

    /// <summary>
    /// POST <paramref name="payload"/> to <c>/probe</c> on a raw loopback
    /// socket and return the status and failure envelope, reading the response
    /// CONCURRENTLY with the send.
    ///
    /// That concurrency is the whole point. A server that refuses an oversized
    /// body answers before the body has finished arriving and then resets the
    /// connection, because the rest is still in flight. An HttpClient that is
    /// only writing at that moment surfaces the reset as
    /// <c>HttpRequestException: Error while copying content to a stream</c> and
    /// throws away the response it had already been sent — which reads as "the
    /// head answered nothing" when the head answered correctly. Reading while
    /// writing takes the envelope off the socket before the reset can matter,
    /// and write failures after that point are expected and ignored.
    ///
    /// <paramref name="chunked"/> frames the body with
    /// <c>Transfer-Encoding: chunked</c>, which is how a caller sends a body
    /// whose length the head cannot pre-check.
    /// </summary>
    private static async Task<(int Status, string? Code, string? Message)> RawProbeAsync(
        int port, string framingHeader, byte[] payload, bool chunked)
    {
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        using var stream = socket.GetStream();

        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"POST /probe HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\n"
            + $"Connection: close\r\n{framingHeader}\r\n"));

        var received = new MemoryStream();
        var reader = Task.Run(async () =>
        {
            try { await stream.CopyToAsync(received); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });

        try
        {
            if (chunked) await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"{payload.Length:x}\r\n"));
            // In slices, so a reset partway through ends the send instead of
            // blocking on a socket buffer nobody is draining.
            for (var offset = 0; offset < payload.Length; offset += 1 << 20)
            {
                await stream.WriteAsync(payload.AsMemory(offset, Math.Min(1 << 20, payload.Length - offset)));
            }
            if (chunked) await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray());
            await stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // The server answered and reset. `reader` already has the answer.
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(60));
        var text = System.Text.Encoding.UTF8.GetString(received.ToArray());
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidOperationException(
                $"the server sent no complete HTTP response; got {received.Length} bytes: {text}");
        }

        var status = int.Parse(text.Split(' ')[1]);
        // Slice the envelope out by its braces rather than taking everything
        // after the headers: `Connection: close` lets the server answer with
        // chunked framing, and its length prefix is not JSON.
        var open = text.IndexOf('{', separator);
        var close = text.LastIndexOf('}');
        if (open < 0 || close < open)
        {
            throw new InvalidOperationException($"the {status} response carried no JSON envelope: {text}");
        }

        using var document = JsonDocument.Parse(text[open..(close + 1)]);
        var error = document.RootElement.GetProperty("error");
        return (status, error.GetProperty("code").GetString(), error.GetProperty("message").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Next;
        public int Sends;
        public Uri? LastRequestUri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            LastRequestUri = request.RequestUri;
            if (Next is null)
                throw new InvalidOperationException("CapturingHandler.Next was not set");
            return Task.FromResult(Next());
        }
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    /// <summary>
    /// Scripted HttpMessageHandler: counts sends and delegates each attempt to
    /// the supplied responder (which may throw, hang on the token, or respond).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;
        public int Sends;

        public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return _respond(cancellationToken);
        }
    }

    /// <summary>
    /// Response body that invokes <paramref name="onDispose"/> when the response
    /// message is disposed — which <c>RustHttpExecutor</c> does strictly AFTER
    /// <c>SendAsync</c> returned and after the body was read. That lets a test
    /// change the world (cancel a token) at a point no HttpClient-internal read
    /// can observe, so only the code under test can react to the change.
    /// </summary>
    private sealed class SignalOnDisposeContent : StringContent
    {
        private readonly Action _onDispose;
        private bool _signalled;

        public SignalOnDisposeContent(string content, Action onDispose) : base(content)
            => _onDispose = onDispose;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && !_signalled)
            {
                _signalled = true;
                _onDispose();
            }
        }
    }

    /// <summary>
    /// Current length of every file in the log directory. Taken before the act
    /// step so <see cref="ReadLogSince"/> returns exactly what that step
    /// appended — including into a file that did not exist yet, which is what a
    /// run crossing local midnight produces (LoggingService derives its path
    /// from <c>DateTime.Now</c> on every single write).
    /// </summary>
    private static Dictionary<string, long> SnapshotLogOffsets()
    {
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var directory = LoggingService.LogDirectory;
        if (!Directory.Exists(directory))
        {
            return offsets;
        }

        foreach (var file in Directory.GetFiles(directory))
        {
            offsets[file] = new FileInfo(file).Length;
        }
        return offsets;
    }

    /// <summary>
    /// Everything LoggingService appended anywhere in the log directory since
    /// <paramref name="offsets"/> was taken, in filename order (the names are
    /// <c>hyperwhisper-yyyy-MM-dd.log</c>, so that is chronological). Shared
    /// read access because the writer may still hold a file; a file that is
    /// unreadable or gone contributes nothing rather than throwing, so the
    /// caller's assertion — which prints what WAS captured — reports the
    /// problem instead of this helper.
    /// </summary>
    private static string ReadLogSince(Dictionary<string, long> offsets)
    {
        var directory = LoggingService.LogDirectory;
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        var files = Directory.GetFiles(directory);
        Array.Sort(files, StringComparer.Ordinal);

        var captured = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            var from = offsets.TryGetValue(file, out var offset) ? offset : 0L;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (from >= stream.Length)
                {
                    continue;
                }
                stream.Seek(from, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                captured.Append(reader.ReadToEnd());
            }
            catch (IOException)
            {
                // Includes FileNotFoundException; see summary.
            }
        }
        return captured.ToString();
    }

    /// <summary>
    /// Log text split into non-empty lines, newline style stripped. Multi-line
    /// entries (LoggingService.Warn with an exception writes one) therefore
    /// contribute one element per physical line, which is what the positional
    /// banner assertions want.
    /// </summary>
    private static string[] SplitLogLines(string text)
    {
        var raw = text.Split('\n');
        var lines = new List<string>(raw.Length);
        foreach (var line in raw)
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Minimal IStreamingProviderStrategy for exercising StreamingTranscriptionClient
    /// methods (like AppendFinalTranscript) that never touch the provider strategy.
    /// Any member a test does end up hitting should throw loudly rather than fake
    /// network behavior.
    /// </summary>
    private sealed class NoOpStreamingProviderStrategy : IStreamingProviderStrategy
    {
        public string TranscriptionProviderLabel => "test";
        public bool SupportsVocabulary => false;
        public bool SessionStartsOnWebSocketOpen => false;
        public int AudioSampleRate => 16000;

        public Uri? BuildWebSocketUri(StreamingSessionConfig config) => throw new NotSupportedException();
        public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config) => throw new NotSupportedException();
        public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData) => throw new NotSupportedException();
        public StreamingProviderEvent? ParseMessage(string text) => throw new NotSupportedException();
        public IReadOnlyList<StreamingStopStep> GetStopSequence() => throw new NotSupportedException();
        public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) => throw new NotSupportedException();
        public Task OnAudioSendOpportunityAsync(
            Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// An <c>ISampleProvider</c> that hands out a scripted, deliberately awkward number of
    /// samples per Read — starting with a count SMALLER than one sample frame, which is legal
    /// and which the mono fold must not mistake for end-of-stream.
    /// </summary>
    private sealed class ChoppySampleProvider(float[] samples, int channels, int[] chunkSizes)
        : NAudio.Wave.ISampleProvider
    {
        private int _position;
        private int _chunk;

        public NAudio.Wave.WaveFormat WaveFormat { get; } =
            NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var remaining = samples.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var size = _chunk < chunkSizes.Length ? chunkSizes[_chunk++] : remaining;
            size = Math.Min(Math.Min(size, count), remaining);
            Array.Copy(samples, _position, buffer, offset, size);
            _position += size;
            return size;
        }
    }

    private sealed class InMemoryCredentialBackend : IWindowsCredentialBackend
    {
        public string? StoredValue { get; private set; }
        public bool ThrowOnRead { get; set; }

        public bool TryRead(string resource, string account, out string? value)
        {
            if (ThrowOnRead) throw new InvalidOperationException("expected backend failure");
            value = StoredValue;
            return value != null;
        }

        public void Write(string resource, string account, string value) => StoredValue = value;
        public void Delete(string resource, string account) => StoredValue = null;
    }

    /// <summary>
    /// Builds one instance of each of the eight step pages, bound to a fake-backed
    /// flow model and laid out once so every binding and every {loc:Loc} has run.
    /// Layout is what turns a page from parsed XAML into something worth asserting
    /// on: ItemsControl containers do not exist until it happens.
    /// </summary>
    private static IReadOnlyList<(OnboardingStep Step, System.Windows.Controls.Page Page)>
        BuildOnboardingStepPages(out OnboardingFlowViewModel flow)
    {
        // One Application per AppDomain, and an earlier case may already own it.
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            application = new System.Windows.Application();
            LoadApplicationResources(application);
        }

        var harness = new OnboardingHarness();
        flow = harness.Flow;

        var pages = new List<(OnboardingStep, System.Windows.Controls.Page)>();

        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            System.Windows.Controls.Page page = step switch
            {
                OnboardingStep.Welcome => new WelcomeStepPage(),
                OnboardingStep.Permissions => new PermissionsStepPage(),
                OnboardingStep.Source => new SourceStepPage(),
                OnboardingStep.Configure => new ConfigureStepPage(),
                OnboardingStep.Setup => new SetupStepPage(),
                OnboardingStep.Microphone => new MicrophoneStepPage(),
                OnboardingStep.TryIt => new TryItStepPage(),
                _ => new DoneStepPage()
            };

            page.DataContext = flow;

            // The window's own size, so a step that overflows here overflows there.
            page.Measure(new Size(760, 521));
            page.Arrange(new Rect(0, 0, 760, 521));
            page.UpdateLayout();

            pages.Add((step, page));
        }

        return pages;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
            return match;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Every descendant of the given type, in visual-tree order.</summary>
    private static List<T> DescendantsOf<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        Collect(root);
        return found;

        void Collect(DependencyObject node)
        {
            if (node is T match)
                found.Add(match);

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Collect(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>Every string this subtree actually puts in front of a user.</summary>
    private static List<string> VisualTextOf(DependencyObject root)
    {
        var texts = new List<string>();
        Collect(root);
        return texts;

        void Collect(DependencyObject node)
        {
            switch (node)
            {
                case System.Windows.Controls.TextBlock block when !string.IsNullOrEmpty(block.Text):
                    texts.Add(block.Text);
                    break;
                case System.Windows.Controls.ContentControl { Content: string content } when content.Length > 0:
                    texts.Add(content);
                    break;
            }

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Collect(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void LoadApplicationResources(System.Windows.Application application)
    {
        AddResourceDictionary(application, "Themes/LightColors.xaml");
        AddResourceDictionary(application, "Themes/Brushes.xaml");
        AddResourceDictionary(application, "Themes/Generic.xaml");
    }

    private static void AddResourceDictionary(System.Windows.Application application, string resourcePath)
    {
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/HyperWhisper;component/{resourcePath}", UriKind.Absolute)
        });
    }
}
