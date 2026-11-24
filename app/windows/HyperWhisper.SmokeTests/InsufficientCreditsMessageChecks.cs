// CLOUD 402 OUT-OF-CREDITS MESSAGE (issue #2807)
//
// Cloud's insufficientCreditsResponse (hyperwhisper-cloud/src/lib/responses.ts)
// sends a fractional credits_remaining and no credits_required. The exception
// message is what History stores as FailedReason, so it must show the real
// balance and never a fake "required: 0". These go through the core's real
// parser and the shipping RustCoreMapping, as HyperWhisperCloudService does.

using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SmokeTests;

internal static class InsufficientCreditsMessageChecks
{
    // The body Cloud sends for insufficientCreditsResponse(0.5, 3.2).
    private const string CloudBody =
        "{\"error\":\"Insufficient credits\",\"message\":\"You have 0.5 credits remaining. This transcription requires approximately 3.2 credits.\",\"credits_remaining\":0.5,\"minutes_remaining\":0,\"minutes_required\":2,\"credits_per_minute\":1.67}";

    public static void RunAll(Action<string, Action> run)
    {
        run("cloud 402: the message shows the real fractional balance and no fake required (#2807)", () =>
        {
            var mapped = MapCloud402(CloudBody);
            Assert(mapped.IsInsufficientCredits && mapped.Code == TranscriptionErrorCode.QuotaExceeded,
                $"a 402 mapped to {mapped.Code} (insufficient={mapped.IsInsufficientCredits})");
            Assert(mapped.Message == "Insufficient credits (remaining: 0.5)",
                $"FailedReason would read '{mapped.Message}'");
            Assert(mapped.GetUserMessage() == "Insufficient credits. Add more credits in Settings.",
                $"the status bar would read '{mapped.GetUserMessage()}'");
        });

        run("cloud 402: a sent credits_required rounds so a short balance never reads as enough (#2807)", () =>
        {
            var close = MapCloud402("{\"error\":\"Insufficient credits\",\"credits_remaining\":0.96,\"credits_required\":1.0}");
            Assert(close.Message == "Insufficient credits (remaining: 0.9, required: 1)",
                $"0.96 of 1.0 read '{close.Message}'");

            var tiny = MapCloud402("{\"error\":\"Insufficient credits\",\"credits_remaining\":0,\"credits_required\":0.04}");
            Assert(tiny.Message == "Insufficient credits (remaining: 0, required: 0.1)",
                $"a 0.04 cost read '{tiny.Message}'");

            var overdrawn = MapCloud402("{\"error\":\"Insufficient credits\",\"credits_remaining\":-0.2}");
            Assert(overdrawn.Message == "Insufficient credits (remaining: 0)",
                $"an overdrawn balance read '{overdrawn.Message}'");
        });
    }

    private static TranscriptionException MapCloud402(string body)
    {
        var response = new HttpResponse(402, new List<Header>(), System.Text.Encoding.UTF8.GetBytes(body));
        HwTranscriptionException parsed;
        try
        {
            HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(response);
            throw new InvalidOperationException("the core accepted a 402 as a transcript");
        }
        catch (HwTranscriptionException ex)
        {
            parsed = ex;
        }

        var (remaining, required) = RustCoreMapping.CreditContext(response);
        return RustCoreMapping.MapTranscriptionError(
            parsed,
            "HyperWhisper Cloud",
            httpStatusCode: 402,
            insufficientCredits: true,
            creditsRemaining: remaining,
            creditsRequired: required);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
