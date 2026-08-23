using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public sealed record BackupValidationFailure(string Path, string Message);

/// <summary>
/// Stable public surface over the generated UniFFI binding. Platform projects
/// consume this assembly instead of compiling private copies of the binding.
/// </summary>
public static class SharedCoreBridge
{
    public static bool ContainsCjk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ContainsCjk(text);
    }

    public static string NormalizeAppType(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var appType = HyperwhisperCoreMethods.AppTypeFromRaw(value);
        return HyperwhisperCoreMethods.AppTypePromptValue(appType);
    }

    public static string AppendTrailingSpace(string text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        return HyperwhisperCoreMethods.AppendTrailingSpace(text, language);
    }

    public static IReadOnlyList<BackupValidationFailure> ValidateBackup(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HyperwhisperCoreMethods.ValidateBackupJson(json)
            .Select(error => new BackupValidationFailure(error.path, error.message))
            .ToArray();
    }
}
