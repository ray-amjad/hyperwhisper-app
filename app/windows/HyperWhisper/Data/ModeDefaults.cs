using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Data;

/// <summary>
/// MODE DEFAULTS
///
/// Provides default modes for initialization.
/// Matches macOS CoreData Mode defaults with well-known UUIDs for cross-platform consistency.
/// </summary>
public static class ModeDefaults
{
    // Well-known mode IDs (matching macOS)
    public static readonly Guid DefaultModeId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid VoiceToTextModeId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid MessageModeId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid MailModeId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public static readonly Guid NoteModeId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static readonly Guid MeetingModeId = Guid.Parse("00000000-0000-0000-0000-000000000006");

    /// <summary>
    /// Returns the list of default modes.
    /// Uses HyperWhisper Cloud as the default provider.
    /// </summary>
    public static List<Mode> GetDefaultModes()
    {
        var now = DateTime.UtcNow;

        // Seed the ElevenLabs tier's own default model (Scribe v2) explicitly so a new
        // install's modes carry the correct transcription model rather than leaving it
        // null (which would otherwise resolve via the stale BYOK default `whisper-1`).
        var elevenLabsScribeModel =
            Services.AppClassification.CloudSttCatalog.Shared.DefaultModelIdForId("elevenLabsScribeV2");

        return new List<Mode>
        {
            new Mode
            {
                Id = DefaultModeId,
                Name = "Hyper",
                Preset = "hyper",
                ProviderType = "cloud",
                CloudProvider = "hyperwhisper",
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudTranscriptionModel = elevenLabsScribeModel,
                Language = "auto",
                IsDefault = true,
                IsSystemProvided = true,
                SortOrder = 0,
                Punctuation = true,
                Capitalization = true,
                PostProcessingMode = 1,
                PostProcessingProvider = "hyperwhispercloud",
                CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
                CreatedDate = now,
                ModifiedDate = now
            },
            new Mode
            {
                Id = VoiceToTextModeId,
                Name = "Voice to Text",
                Preset = "hyper",
                ProviderType = "cloud",
                CloudProvider = "hyperwhisper",
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudTranscriptionModel = elevenLabsScribeModel,
                Language = "auto",
                IsSystemProvided = true,
                SortOrder = 1,
                PostProcessingMode = 0,  // Off - direct transcription
                CreatedDate = now,
                ModifiedDate = now
            },
            new Mode
            {
                Id = MessageModeId,
                Name = "Message",
                Preset = "message",
                ProviderType = "cloud",
                CloudProvider = "hyperwhisper",
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudTranscriptionModel = elevenLabsScribeModel,
                Language = "auto",
                IsSystemProvided = true,
                SortOrder = 2,
                PostProcessingMode = 1,
                PostProcessingProvider = "hyperwhispercloud",
                CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
                CreatedDate = now,
                ModifiedDate = now
            },
            new Mode
            {
                Id = MailModeId,
                Name = "Mail",
                Preset = "mail",
                ProviderType = "cloud",
                CloudProvider = "hyperwhisper",
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudTranscriptionModel = elevenLabsScribeModel,
                Language = "auto",
                IsSystemProvided = true,
                SortOrder = 3,
                PostProcessingMode = 1,
                PostProcessingProvider = "hyperwhispercloud",
                CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
                CreatedDate = now,
                ModifiedDate = now
            },
            new Mode
            {
                Id = NoteModeId,
                Name = "Note",
                Preset = "note",
                ProviderType = "cloud",
                CloudProvider = "hyperwhisper",
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudTranscriptionModel = elevenLabsScribeModel,
                Language = "auto",
                IsSystemProvided = true,
                SortOrder = 4,
                PostProcessingMode = 1,
                PostProcessingProvider = "hyperwhispercloud",
                CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
                CreatedDate = now,
                ModifiedDate = now
            },
            new Mode
            {
                Id = MeetingModeId,
                Name = "Meeting",
                Preset = "meeting",
                ProviderType = "cloud",
                CloudProvider = "hyperwhisper",
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudTranscriptionModel = elevenLabsScribeModel,
                Language = "auto",
                IsSystemProvided = true,
                SortOrder = 5,
                PostProcessingMode = 1,
                PostProcessingProvider = "hyperwhispercloud",
                CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
                CreatedDate = now,
                ModifiedDate = now
            }
        };
    }
}
