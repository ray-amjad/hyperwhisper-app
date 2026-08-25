using uniffi.hyperwhisper_core;

namespace HyperWhisper.AppClassification;

/// <summary>
/// Coarse application type driving app-aware formatting.
/// </summary>
public enum AppType
{
    Email,
    Ai,
    WorkMessaging,
    PersonalMessaging,
    Document,
    Code,
    Terminal,
    Sensitive,
    Other
}

/// <summary>
/// The derived strings for an <see cref="AppType"/>. The shared core resolves
/// the same three strings and returns them on every classification, and
/// <c>HyperWhisper.AppTypeConformance.Tests</c> asserts these switches against
/// that answer, so the pair cannot drift.
/// </summary>
public static class AppTypeExtensions
{
    public static string ToPromptValue(this AppType appType) => appType switch
    {
        AppType.WorkMessaging => "work_messaging",
        AppType.PersonalMessaging => "personal_messaging",
        _ => appType.ToString().ToLowerInvariant()
    };

    public static string ToCategory(this AppType appType) => appType switch
    {
        AppType.Email => "Email Client",
        AppType.Ai => "AI",
        AppType.WorkMessaging or AppType.PersonalMessaging => "Communication",
        AppType.Document => "Document",
        AppType.Code => "Code Editor",
        AppType.Terminal => "Terminal",
        AppType.Sensitive => "Sensitive",
        _ => "Application"
    };

    public static string ToTextFormat(this AppType appType) => appType switch
    {
        AppType.Email => "email",
        AppType.Code => "code",
        AppType.Terminal => "command",
        AppType.Document => "markdown",
        _ => "text"
    };
}

/// <summary>
/// Everything the platform observed about the foreground app. Pass an empty
/// string, <c>null</c>, or an empty list for a signal this platform cannot see.
/// </summary>
/// <param name="BundleId">macOS bundle identifier. Always empty on .NET heads.</param>
/// <param name="ProcessName">Process name without an extension, e.g. <c>OUTLOOK</c> or <c>konsole</c>.</param>
/// <param name="AppName">The app's display name, when the platform has one distinct from the process.</param>
/// <param name="Host">Browser host for a web app. A full URL is accepted and normalized.</param>
/// <param name="HostConfidence">
/// The confidence to report for a host hit. It reaches the LLM prompt, so the
/// caller owns it; an empty value means <c>strong</c>.
/// </param>
/// <param name="Title">Window and/or browser-tab title, composed by the caller.</param>
/// <param name="FocusedPieces">Text read off the focused accessibility element.</param>
public sealed record AppClassificationRequest(
    string BundleId = "",
    string ProcessName = "",
    string AppName = "",
    string? Host = null,
    string HostConfidence = "",
    string Title = "",
    IReadOnlyList<string>? FocusedPieces = null);

public sealed record AppClassificationResult(
    AppType AppType,
    string Confidence,
    string Source,
    string? Matched);

/// <summary>
/// Catalog-backed application classification, for every .NET head.
/// </summary>
/// <remarks>
/// The algorithm itself lives in <c>hw-catalog</c> and nowhere else — issue
/// #279 deleted the 320-line C# copy and the 300-line Swift one. What is left
/// here is the mapping between the generated binding's types and the platform's
/// own, which is why the classifier can now be reached from Linux at all.
/// </remarks>
public static class AppTypeClassifier
{
    /// <summary>
    /// Classify the foreground app. Signals are tried in order — host, bundle
    /// id, process name, title, app name, focused element — and the first hit
    /// wins; otherwise the result is <see cref="AppType.Other"/> at
    /// <c>unknown</c>/<c>default</c>.
    /// </summary>
    public static AppClassificationResult Classify(AppClassificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var classification = HyperwhisperCoreMethods.AppClassify(new AppClassifyRequest(
            @bundleId: request.BundleId ?? string.Empty,
            @processName: request.ProcessName ?? string.Empty,
            @appName: request.AppName ?? string.Empty,
            @host: string.IsNullOrWhiteSpace(request.Host) ? null : request.Host,
            @hostConfidence: request.HostConfidence ?? string.Empty,
            @title: request.Title ?? string.Empty,
            @focusedPieces: request.FocusedPieces is null
                ? []
                : [.. request.FocusedPieces.Where(piece => !string.IsNullOrWhiteSpace(piece))]));

        return new AppClassificationResult(
            FromBinding(classification.@appType),
            classification.@confidence,
            classification.@source,
            classification.@matched);
    }

    /// <summary>
    /// Whether a browser-tab title looks like webmail. Call this ONLY when the
    /// foreground app is already known to be a browser and nothing else
    /// classified the window — a title is not evidence of webmail on its own.
    /// </summary>
    public static bool IsWebmail(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return HyperwhisperCoreMethods.AppIsWebmail(title);
    }

    private static AppType FromBinding(ClassifiedAppType appType) => appType switch
    {
        ClassifiedAppType.Email => AppType.Email,
        ClassifiedAppType.Ai => AppType.Ai,
        ClassifiedAppType.WorkMessaging => AppType.WorkMessaging,
        ClassifiedAppType.PersonalMessaging => AppType.PersonalMessaging,
        ClassifiedAppType.Document => AppType.Document,
        ClassifiedAppType.Code => AppType.Code,
        ClassifiedAppType.Terminal => AppType.Terminal,
        ClassifiedAppType.Sensitive => AppType.Sensitive,
        ClassifiedAppType.Other => AppType.Other,
        _ => AppType.Other
    };
}
