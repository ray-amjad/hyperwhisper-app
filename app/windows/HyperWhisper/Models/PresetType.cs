namespace HyperWhisper.Models;

/// <summary>
/// PRESET TYPES
///
/// Defines preset categories that provide styling hints and default configurations.
/// Matches macOS PresetType enum for consistency.
/// </summary>
public enum PresetType
{
    /// <summary>Recommended all-purpose mode with AI enhancement.</summary>
    Hyper,

    /// <summary>Optimized for messaging apps.</summary>
    Message,

    /// <summary>Optimized for email composition.</summary>
    Mail,

    /// <summary>Optimized for note-taking.</summary>
    Note,

    /// <summary>Optimized for meeting transcription.</summary>
    Meeting,

    /// <summary>User-defined with custom instructions.</summary>
    Custom,

    /// <summary>Voice-to-code dictation with symbol conversion.</summary>
    Code
}

public static class PresetTypeExtensions
{
    public static string ToDisplayName(this PresetType preset) => preset switch
    {
        PresetType.Hyper => "Hyper (Recommended)",
        PresetType.Message => "Message",
        PresetType.Mail => "Mail",
        PresetType.Note => "Note",
        PresetType.Meeting => "Meeting",
        PresetType.Custom => "Custom",
        PresetType.Code => "Code",
        _ => preset.ToString()
    };

    public static string ToDescription(this PresetType preset) => preset switch
    {
        PresetType.Hyper => "Context-aware formatting that adapts to your active app. Fixes punctuation, capitalization, and minor grammar while keeping your original wording. Automatically applies email formatting in mail apps.",
        PresetType.Message => "Casual, conversational style for chat apps",
        PresetType.Mail => "Professional formatting for emails",
        PresetType.Note => "Clean format for note-taking apps",
        PresetType.Meeting => "Detailed transcription for meetings",
        PresetType.Custom => "Define your own custom instructions",
        PresetType.Code => "Voice-to-code dictation with symbol conversion",
        _ => ""
    };

    public static string ToStringValue(this PresetType preset) => preset switch
    {
        PresetType.Hyper => "hyper",
        PresetType.Message => "message",
        PresetType.Mail => "mail",
        PresetType.Note => "note",
        PresetType.Meeting => "meeting",
        PresetType.Custom => "custom",
        PresetType.Code => "code",
        _ => "hyper"
    };

    public static PresetType FromString(string value) => value switch
    {
        "hyper" => PresetType.Hyper,
        "voiceToText" => PresetType.Hyper, // Legacy migration
        "message" => PresetType.Message,
        "mail" => PresetType.Mail,
        "note" => PresetType.Note,
        "meeting" => PresetType.Meeting,
        "custom" => PresetType.Custom,
        "code" => PresetType.Code,
        _ => PresetType.Hyper
    };
}
