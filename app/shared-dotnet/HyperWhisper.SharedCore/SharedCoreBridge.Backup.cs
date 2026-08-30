using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public sealed record BackupValidationFailure(string Path, string Message);

public static partial class SharedCoreBridge
{
    public static IReadOnlyList<BackupValidationFailure> ValidateBackup(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HyperwhisperCoreMethods.ValidateBackupJson(json)
            .Select(error => new BackupValidationFailure(error.path, error.message))
            .ToArray();
    }

    /// <summary>
    /// Canonicalize ONE universal-v2 mode object's five cloud-routing fields:
    /// the cloudProvider catalog fold, the legacy model-alias tables, the
    /// present-only cloudAccuracyTier / cloudPostProcessingModel migration
    /// (including the platformExtensions.windows override) and the
    /// cloudTranscriptionDomain gate. Windows' UniversalBackupMapper calls the
    /// same core function, so both non-macOS importers agree.
    /// </summary>
    /// <remarks>
    /// A field no source supplied comes back ABSENT, not defaulted — the caller
    /// applies its own Mode entity default. Stamping the core's own defaults here
    /// would regress both heads, whose shared native pair is elevenLabsScribeV2 /
    /// anthropic:claude-haiku-4-5.
    /// </remarks>
    public static string NormalizeUniversalMode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HyperwhisperCoreMethods.NormalizeUniversalModeJson(json);
    }

    /// <summary>
    /// Map the Linux settings store (flat, dotted keys) into the universal-v2
    /// shared <c>settings</c> block.
    /// </summary>
    /// <remarks>
    /// The whole store may be passed in: only keys with a row in the core's
    /// <c>LINUX_*_PAIRS</c> tables are promoted, so Linux-only and device-local
    /// keys cannot reach an exported backup through here. The result is always
    /// COMPLETE — an absent key is emitted with the backup path's own default,
    /// which is what makes an untouched profile export all 23 shared keys.
    /// </remarks>
    public static string LinuxSettingsToUniversal(string linuxJson)
    {
        ArgumentNullException.ThrowIfNull(linuxJson);
        return HyperwhisperCoreMethods.LinuxSettingsToUniversalSettingsJson(linuxJson);
    }

    /// <summary>
    /// Inverse of <see cref="LinuxSettingsToUniversal"/>: the universal-v2 shared
    /// <c>settings</c> block into the flat dotted keys the Linux settings store
    /// holds.
    /// </summary>
    /// <remarks>
    /// PRESENT-ONLY and null-dropping, reproducing the shipping
    /// <c>ApplySharedSettings</c>/<c>CopyCategory</c> allowlist: unknown keys and
    /// unknown categories are dropped, and an explicit JSON <c>null</c> leaves the
    /// live value alone. The caller deep-merges this over its own baseline
    /// snapshot before writing it back.
    /// </remarks>
    public static string UniversalSettingsToLinuxSettings(string universalJson)
    {
        ArgumentNullException.ThrowIfNull(universalJson);
        return HyperwhisperCoreMethods.UniversalSettingsToLinuxSettingsJson(universalJson);
    }
}
