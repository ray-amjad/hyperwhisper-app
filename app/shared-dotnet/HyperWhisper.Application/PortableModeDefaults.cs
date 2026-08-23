using System.Globalization;
using System.Text.Json;
using HyperWhisper.Data.Entities;

namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>Creates the six cross-platform first-install modes from shared catalogs.</summary>
public static class PortableModeDefaults
{
    public static readonly Guid HyperModeId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid VoiceToTextModeId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid MessageModeId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid MailModeId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public static readonly Guid NoteModeId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static readonly Guid MeetingModeId = Guid.Parse("00000000-0000-0000-0000-000000000006");

    private const string CloudSttCatalogResource = "HyperWhisper.SharedAppClassification.cloud-stt-catalog.json";
    private const string CloudPostProcessingCatalogResource = "HyperWhisper.SharedAppClassification.cloud-pp-catalog.json";

    public static IReadOnlyList<Mode> CreateForCurrentRegion()
    {
        var assembly = typeof(PortableModeDefaults).Assembly;
        using var sttCatalog = assembly.GetManifestResourceStream(CloudSttCatalogResource)
            ?? throw new InvalidDataException("The shared cloud STT catalog is missing.");
        using var postProcessingCatalog = assembly.GetManifestResourceStream(CloudPostProcessingCatalogResource)
            ?? throw new InvalidDataException("The shared cloud post-processing catalog is missing.");
        return CreateForRegion(CurrentRegionCode(), sttCatalog, postProcessingCatalog, DateTime.UtcNow);
    }

    public static IReadOnlyList<Mode> CreateForRegion(
        string? regionCode,
        Stream cloudSttCatalog,
        Stream cloudPostProcessingCatalog,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(cloudSttCatalog);
        ArgumentNullException.ThrowIfNull(cloudPostProcessingCatalog);
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("The seed timestamp must be UTC.", nameof(utcNow));

        var transcriptionModel = FindEnabledDefaultModel(cloudSttCatalog, "elevenLabsScribeV2");
        var postProcessingModel = $"anthropic:{FindEnabledDefaultModel(cloudPostProcessingCatalog, "anthropic")}";
        var spelling = EnglishSpellingForRegion(regionCode);

        Mode Create(Guid id, string name, string preset, int order, bool postProcess) => new()
        {
            Id = id,
            Name = name,
            Preset = preset,
            ProviderType = "cloud",
            CloudProvider = "hyperwhisper",
            CloudAccuracyTier = "elevenLabsScribeV2",
            CloudTranscriptionModel = transcriptionModel,
            Language = "auto",
            EnglishSpelling = spelling,
            IsDefault = id == HyperModeId,
            IsSystemProvided = true,
            SortOrder = order,
            Punctuation = true,
            Capitalization = true,
            PostProcessingMode = postProcess ? 1 : 0,
            PostProcessingProvider = postProcess ? "hyperwhispercloud" : null,
            CloudPostProcessingModel = postProcessingModel,
            CreatedDate = utcNow,
            ModifiedDate = utcNow
        };

        return
        [
            Create(HyperModeId, "Hyper", "hyper", 0, postProcess: true),
            Create(VoiceToTextModeId, "Voice to Text", "hyper", 1, postProcess: false),
            Create(MessageModeId, "Message", "message", 2, postProcess: true),
            Create(MailModeId, "Mail", "mail", 3, postProcess: true),
            Create(NoteModeId, "Note", "note", 4, postProcess: true),
            Create(MeetingModeId, "Meeting", "meeting", 5, postProcess: true)
        ];
    }

    public static string EnglishSpellingForRegion(string? regionCode)
    {
        var region = (regionCode ?? string.Empty).Trim().ToUpperInvariant();
        if (region == "CA") return "canadian";
        if (AustralianRegions.Contains(region)) return "australian";
        if (BritishRegions.Contains(region)) return "british";
        return "american";
    }

    private static string FindEnabledDefaultModel(Stream catalogStream, string providerId)
    {
        using var document = JsonDocument.Parse(catalogStream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        if (!document.RootElement.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The shared provider catalog is invalid.");
        foreach (var provider in providers.EnumerateArray())
        {
            if (!provider.TryGetProperty("id", out var id)
                || !string.Equals(id.GetString(), providerId, StringComparison.Ordinal)) continue;
            if (IsDisabled(provider)) throw new InvalidDataException($"The required shared provider '{providerId}' is disabled.");
            if (!provider.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                break;
            foreach (var model in models.EnumerateArray())
            {
                if (!model.TryGetProperty("isDefault", out var isDefault)
                    || isDefault.ValueKind != JsonValueKind.True
                    || IsDisabled(model)
                    || !model.TryGetProperty("id", out var modelId)
                    || string.IsNullOrWhiteSpace(modelId.GetString())) continue;
                return modelId.GetString()!;
            }
            break;
        }
        throw new InvalidDataException($"The shared provider catalog has no enabled default model for '{providerId}'.");
    }

    private static bool IsDisabled(JsonElement value)
    {
        if (!value.TryGetProperty("enabled", out var enabled)) return false;
        return enabled.ValueKind switch
        {
            JsonValueKind.True => false,
            JsonValueKind.False => true,
            _ => throw new InvalidDataException("A shared provider catalog has an invalid enabled flag.")
        };
    }

    private static string CurrentRegionCode()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static readonly HashSet<string> AustralianRegions = new(StringComparer.Ordinal)
    {
        "AU", "CC", "CX", "NF"
    };

    private static readonly HashSet<string> BritishRegions = new(StringComparer.Ordinal)
    {
        "GB", "IE", "IM", "JE", "GG", "GI", "MT", "CY",
        "ZA", "NG", "GH", "KE", "UG", "TZ", "RW", "ZM", "ZW", "BW", "NA",
        "MW", "MU", "SC", "SZ", "LS", "GM", "SL", "SS",
        "IN", "PK", "BD", "LK", "NP", "BT", "MV", "SG", "MY", "BN", "HK",
        "JM", "TT", "BB", "BS", "BZ", "GY", "AG", "DM", "GD", "KN", "LC",
        "VC", "VG", "KY", "TC", "MS", "AI", "BM", "FK", "SH",
        "NZ", "FJ", "PG", "SB", "VU", "WS", "TO", "KI", "TV", "NR", "CK",
        "NU", "TK"
    };
}
