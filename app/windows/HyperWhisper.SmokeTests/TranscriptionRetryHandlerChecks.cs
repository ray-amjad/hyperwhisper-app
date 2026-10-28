// HISTORY RETRY: WHAT A RETRY WRITES (issue #1037)
//
// TranscriptionRetryHandler is what the History "Retry" button runs. Its public
// constructor takes the process-wide orchestrator (a paid Cloud call) and the
// live History database. These checks use its internal constructor instead: a
// scripted transcribe and a list as the save sink, over a cloud mode and a temp
// audio file. Each saved row is copied at save time, because the handler hands
// the sink the view model's own source entity and keeps writing the view model.

using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.Transcription;
using HyperWhisper.ViewModels;

namespace HyperWhisper.SmokeTests;

internal static class TranscriptionRetryHandlerChecks
{
    private const string OldReason = "test1037 old reason";

    public static void RunAll(Action<string, Action> run)
    {
        run("history retry: a success writes Completed, the texts, both providers and the mode to the row and the view model (#1037)", () =>
            WithAudio(audio =>
            {
                var result = new TranscriptionResult("raw 1037", "final 1037", "Provider 1037", "Post 1037", "post-processed 1037");
                var rig = new Rig((_, _, _, _, _, _) => Task.FromResult(result));
                var vm = FailedTranscript(audio);
                var mode = CloudMode();
                using var cts = new CancellationTokenSource();

                var returned = Await(rig.Handler.RetryTranscriptionAsync(vm, mode, cts.Token));

                Assert(ReferenceEquals(returned, result), "the retry did not return the transcribe result");
                Assert(rig.Calls.Count == 1, $"transcribe ran {rig.Calls.Count} times");
                var call = rig.Calls[0];
                Assert(call.AudioPath == audio && ReferenceEquals(call.Mode, mode), $"transcribe got '{call.AudioPath}'");
                Assert(call.Vocabulary == null && call.LocalProvider == null && call.HadContext == false,
                    "a cloud retry passed a vocabulary, a local provider or an application context");
                Assert(call.Token == cts.Token, "transcribe did not get the caller's token");

                Assert(rig.Saved.Count == 1, $"the row was saved {rig.Saved.Count} times");
                var saved = rig.Saved[0];
                var shown = Row.Of(vm.ToEntity());
                foreach (var (label, row) in new[] { ("saved row", saved), ("view model", shown) })
                {
                    Assert(row.Status == TranscriptStatus.Completed, $"{label}: status {row.Status}");
                    Assert(row.FailedReason == null, $"{label}: kept FailedReason '{row.FailedReason}'");
                    Assert(row.Text == "final 1037", $"{label}: Text '{row.Text}'");
                    Assert(row.TranscribedText == "raw 1037", $"{label}: TranscribedText '{row.TranscribedText}'");
                    Assert(row.PostProcessedText == "post-processed 1037", $"{label}: PostProcessedText '{row.PostProcessedText}'");
                    Assert(row.TranscriptionProvider == "Provider 1037", $"{label}: TranscriptionProvider '{row.TranscriptionProvider}'");
                    Assert(row.PostProcessingProvider == "Post 1037", $"{label}: PostProcessingProvider '{row.PostProcessingProvider}'");
                    Assert(row.Mode == mode.Name, $"{label}: Mode '{row.Mode}'");
                }
            }));

        run("history retry: a TranscriptionException saves Failed with its user message, and the caller still sees it (#1037)", () =>
            WithAudio(audio =>
            {
                var failure = new TranscriptionException(TranscriptionErrorCode.Unauthorized, "test1037 raw provider body", "Provider 1037");
                var rig = new Rig((_, _, _, _, _, _) => Task.FromException<TranscriptionResult>(failure));
                var vm = FailedTranscript(audio);

                var thrown = Catch(() => rig.Handler.RetryTranscriptionAsync(vm, CloudMode()));

                Assert(ReferenceEquals(thrown, failure), $"the caller got {thrown?.GetType().Name ?? "no exception"}, not the failure");
                var userMessage = failure.GetUserMessage();
                Assert(userMessage != failure.Message, "the rig's user message equals its raw message");
                Assert(rig.Saved.Count == 1, $"the row was saved {rig.Saved.Count} times");
                foreach (var (label, row) in new[] { ("saved row", rig.Saved[0]), ("view model", Row.Of(vm.ToEntity())) })
                {
                    Assert(row.Status == TranscriptStatus.Failed, $"{label}: status {row.Status}");
                    Assert(row.FailedReason == userMessage, $"{label}: FailedReason '{row.FailedReason}'");
                    Assert(row.Text == $"Transcription failed: {userMessage}", $"{label}: Text '{row.Text}'");
                }

                // Any other exception stores its own message.
                var plain = new InvalidOperationException("test1037 plain failure");
                var plainRig = new Rig((_, _, _, _, _, _) => Task.FromException<TranscriptionResult>(plain));
                var plainVm = FailedTranscript(audio);
                Assert(ReferenceEquals(Catch(() => plainRig.Handler.RetryTranscriptionAsync(plainVm, CloudMode())), plain),
                    "a plain failure did not reach the caller");
                Assert(plainRig.Saved.Count == 1 && plainRig.Saved[0].FailedReason == plain.Message,
                    $"a plain failure saved FailedReason '{plainRig.Saved.ElementAtOrDefault(0)?.FailedReason}'");
                Assert(plainVm.FailedReason == plain.Message, $"a plain failure showed FailedReason '{plainVm.FailedReason}'");
            }));

        run("history retry: RetryCount goes 2 to 3 and LastRetryDate is now, on the row and the view model, both arms (#1037)", () =>
            WithAudio(audio =>
            {
                var arms = new (string Arm, TranscriptionRetryHandler.TranscribeAudio Transcribe)[]
                {
                    ("success", (_, _, _, _, _, _) => Task.FromResult(new TranscriptionResult("raw", "final", "Provider 1037"))),
                    ("failure", (_, _, _, _, _, _) => Task.FromException<TranscriptionResult>(
                        new TranscriptionException(TranscriptionErrorCode.NetworkError, "test1037 offline"))),
                };
                foreach (var (arm, transcribe) in arms)
                {
                    var rig = new Rig(transcribe);
                    var vm = FailedTranscript(audio);
                    var before = DateTime.UtcNow;
                    Catch(() => rig.Handler.RetryTranscriptionAsync(vm, CloudMode()));
                    var after = DateTime.UtcNow;

                    Assert(rig.Saved.Count == 1, $"{arm}: the row was saved {rig.Saved.Count} times");
                    foreach (var (label, row) in new[] { ("saved row", rig.Saved[0]), ("view model", Row.Of(vm.ToEntity())) })
                    {
                        Assert(row.RetryCount == 3, $"{arm} {label}: RetryCount {row.RetryCount}");
                        Assert(row.LastRetryDate is { } at && at >= before && at <= after,
                            $"{arm} {label}: LastRetryDate {row.LastRetryDate:O} not in {before:O}..{after:O}");
                    }
                }
            }));

        run("history retry: IsRetrying is set while it transcribes and cleared after a success and a failure (#1037)", () =>
            WithAudio(audio =>
            {
                foreach (var fail in new[] { false, true })
                {
                    var arm = fail ? "failure" : "success";
                    TranscriptViewModel? vm = null;
                    bool? duringCall = null;
                    var rig = new Rig((_, _, _, _, _, _) =>
                    {
                        duringCall = vm!.IsRetrying;
                        return fail
                            ? Task.FromException<TranscriptionResult>(new TranscriptionException(TranscriptionErrorCode.Unknown, "test1037"))
                            : Task.FromResult(new TranscriptionResult("raw", "final", "Provider 1037"));
                    });
                    vm = FailedTranscript(audio);

                    Catch(() => rig.Handler.RetryTranscriptionAsync(vm, CloudMode()));

                    Assert(duringCall == true, $"{arm}: IsRetrying was {duringCall?.ToString() ?? "never read"} during transcribe");
                    Assert(!vm.IsRetrying, $"{arm}: IsRetrying stuck on after the retry");
                }
            }));

        run("history retry: a row that is not Failed, has no audio path or a missing file is refused before any call (#1037)", () =>
            WithAudio(audio =>
            {
                var missing = Path.Combine(Path.GetDirectoryName(audio)!, $"missing-{Guid.NewGuid():N}.wav");
                var cases = new (string Label, TranscriptViewModel Vm)[]
                {
                    ("completed", FailedTranscript(audio, TranscriptStatus.Completed)),
                    ("processing", FailedTranscript(audio, TranscriptStatus.Processing)),
                    ("null path", FailedTranscript(null)),
                    ("empty path", FailedTranscript("")),
                    ("missing file", FailedTranscript(missing)),
                };
                var rig = new Rig((_, _, _, _, _, _) => Task.FromResult(new TranscriptionResult("raw", "final", "Provider 1037")));
                Assert(!rig.Handler.CanRetry(null), "CanRetry(null) was true");
                foreach (var (label, vm) in cases)
                {
                    Assert(!rig.Handler.CanRetry(vm), $"{label}: CanRetry was true");
                    var thrown = Catch(() => rig.Handler.RetryTranscriptionAsync(vm, CloudMode()));
                    Assert(thrown is InvalidOperationException, $"{label}: threw {thrown?.GetType().Name ?? "nothing"}");
                    Assert(!vm.IsRetrying, $"{label}: IsRetrying was left on");
                }
                Assert(rig.Calls.Count == 0, $"transcribe ran {rig.Calls.Count} times for refused rows");
                Assert(rig.Saved.Count == 0, $"{rig.Saved.Count} refused rows were saved");
                Assert(rig.Handler.CanRetry(FailedTranscript(audio)), "the control row (Failed, file present) was refused");
            }));
    }

    /// <summary>PROBE ONLY: runs the 5 checks under each mutant and prints the matrix.</summary>
    public static void Matrix()
    {
        string[] names =
        {
            "none", "success entity.RetryCount++ deleted", "failure entity.RetryCount++ deleted",
            "ex.Message instead of GetUserMessage()", "entity.FailedReason = null dropped",
            "IsRetrying = false moved out of finally", "throw; removed",
            "CanRetry File.Exists check dropped", "CanRetry Failed-status check dropped",
        };
        for (var m = 0; m < names.Length; m++)
        {
            TranscriptionRetryHandler.Mutant = m;
            var k = 0;
            RunAll((name, check) =>
            {
                k++;
                string verdict;
                try { check(); verdict = "green"; }
                catch (Exception ex) { verdict = "RED   " + ex.Message.Split('\n')[0]; }
                Console.WriteLine($"  MATRIX mutant={m} ({names[m]}) check={k}: {verdict}");
            });
        }
        TranscriptionRetryHandler.Mutant = 0;
    }

    // =========================================================================
    // RIG
    // =========================================================================

    private sealed class Rig
    {
        public List<Call> Calls { get; } = new();
        public List<Row> Saved { get; } = new();
        public TranscriptionRetryHandler Handler { get; }

        public Rig(TranscriptionRetryHandler.TranscribeAudio transcribe)
        {
            Handler = new TranscriptionRetryHandler(
                (audioPath, mode, vocabulary, localProvider, applicationContext, token) =>
                {
                    Calls.Add(new Call(audioPath, mode, vocabulary, localProvider, applicationContext != null, token));
                    return transcribe(audioPath, mode, vocabulary, localProvider, applicationContext, token);
                },
                transcript => Saved.Add(Row.Of(transcript)));
        }
    }

    private sealed record Call(
        string AudioPath, Mode Mode, IReadOnlyList<string>? Vocabulary,
        ITranscriptionProvider? LocalProvider, bool HadContext, CancellationToken Token);

    /// <summary>The fields a retry writes, copied when the row is saved.</summary>
    private sealed record Row(
        TranscriptStatus Status, string? FailedReason, string Text, string? TranscribedText,
        string? PostProcessedText, string? TranscriptionProvider, string? PostProcessingProvider,
        int RetryCount, DateTime? LastRetryDate, string? Mode)
    {
        public static Row Of(Transcript t) => new(
            t.Status, t.FailedReason, t.Text, t.TranscribedText, t.PostProcessedText,
            t.TranscriptionProvider, t.PostProcessingProvider, t.RetryCount, t.LastRetryDate, t.Mode);
    }

    private static Mode CloudMode() => new() { Name = "Retry 1037", ProviderType = "cloud", CloudProvider = "openai" };

    private static TranscriptViewModel FailedTranscript(string? audioPath, TranscriptStatus status = TranscriptStatus.Failed) =>
        new(new Transcript
        {
            Text = $"Transcription failed: {OldReason}",
            AudioFilePath = audioPath,
            Status = status,
            FailedReason = OldReason,
            TranscriptionProvider = "Old provider",
            PostProcessingProvider = "Old post",
            Mode = "Old mode",
            RetryCount = 2,
            LastRetryDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

    private static void WithAudio(Action<string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var audio = Path.Combine(folder, "retry.wav");
        File.WriteAllBytes(audio, new byte[44]);

        // Blocking on the retry below must not wait on a DispatcherSynchronizationContext
        // an earlier case left on this thread with no loop pumping it (#2366).
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            check(audio);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();

    /// <summary>The exception the retry threw, or null when it returned.</summary>
    private static Exception? Catch(Func<Task> retry)
    {
        try
        {
            retry().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
