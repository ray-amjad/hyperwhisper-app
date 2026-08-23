using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public sealed record LinuxContextCaptureOutcome(
    ApplicationContextSnapshot? Snapshot,
    PlatformError? ContextFailure = null,
    PlatformError? OcrFailure = null);

/// <summary>
/// Gathers application metadata first, then performs user-consented OCR only
/// when the active mode requests it. Either capability may fail without losing
/// data produced by the other capability.
/// </summary>
public sealed class LinuxContextCaptureCoordinator(
    IApplicationContextProvider contextProvider,
    IScreenOcrService screenOcr)
{
    private readonly IApplicationContextProvider _contextProvider =
        contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
    private readonly IScreenOcrService _screenOcr =
        screenOcr ?? throw new ArgumentNullException(nameof(screenOcr));

    public async ValueTask<LinuxContextCaptureOutcome> CaptureAsync(
        bool enableScreenOcr,
        int maximumOcrCharacters = 2000,
        CancellationToken cancellationToken = default)
    {
        if (maximumOcrCharacters is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumOcrCharacters));

        ApplicationContextSnapshot? snapshot = null;
        PlatformError? contextFailure = null;
        try
        {
            var context = await _contextProvider.GatherAsync(cancellationToken).ConfigureAwait(false);
            if (context.IsSuccess) snapshot = context.Value;
            else contextFailure = context.Error;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            contextFailure = new PlatformError(
                "application_context_failed", "Application context could not be gathered.");
        }

        if (!enableScreenOcr)
            return new LinuxContextCaptureOutcome(snapshot, contextFailure);

        PlatformError? ocrFailure = null;
        string? screenText = null;
        try
        {
            var ocr = await _screenOcr.CaptureAndRecognizeAsync(maximumOcrCharacters, cancellationToken)
                .ConfigureAwait(false);
            if (ocr.IsSuccess) screenText = Normalize(ocr.Value, maximumOcrCharacters);
            else ocrFailure = ocr.Error;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            ocrFailure = new PlatformError("screen_ocr_failed", "Screen recognition could not be completed.");
        }

        if (screenText is not null)
            snapshot = (snapshot ?? new ApplicationContextSnapshot()) with { ScreenOcrText = screenText };
        return new LinuxContextCaptureOutcome(snapshot, contextFailure, ocrFailure);
    }

    private static string? Normalize(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maximumCharacters ? normalized : normalized[..maximumCharacters];
    }
}
