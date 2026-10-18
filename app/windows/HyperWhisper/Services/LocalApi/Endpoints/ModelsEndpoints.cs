using System.Runtime.Versioning;
using HyperWhisper.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace HyperWhisper.Services.LocalApi.Endpoints;

/// <summary>
/// `GET /models` — flat list of every voice/text model the app knows about
/// (cloud + local), with installed status. Backs the cross-platform
/// `hyperwhisper.list_models` MCP tool and benchmark scripts that need to
/// iterate over every model without parsing UI state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ModelsEndpoints
{
    public static void Map(IEndpointRouteBuilder app, LocalApiServer server)
    {
        app.MapGet("/models", () =>
        {
            var library = server.ModelLibrary;
            if (library == null)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.EngineUnavailable,
                    "Model library not yet initialized.");
            }

            // Read once per request: the account key /transcribe refuses on.
            var cloudAccountKeyPresent = HealthEndpoints.CloudAccountKeyPresent();
            var models = library.Rebuild()
                .Select(row => ToEntry(row, cloudAccountKeyPresent))
                .ToList();

            return LocalApiResponder.Ok(new ModelsListResponse { Models = models });
        });
    }

    internal static ModelEntry ToEntry(LibraryModel row, bool cloudAccountKeyPresent)
    {
        return new ModelEntry
        {
            Id = row.Id,
            Kind = row.Kind == LibraryModelKind.Voice ? "voice" : "text",
            Provider = ProviderId(row),
            DisplayName = row.DisplayName,
            // `installed` means "configured and selectable", not "proven to
            // work". A custom endpoint that has never been tested is Untested
            // rather than Enabled since #509; it is still a usable row, so it
            // keeps reporting installed and the wire contract does not move.
            // The HW-Cloud-routed rows are always Enabled in the library (no
            // BYOK key), but /transcribe and Cloud post-processing refuse them
            // with MISSING_API_KEY when no account key is stored, so they are
            // installed only with one, the same check as /health key_present
            // (issue #650).
            Installed = (row.StatusKind is LibraryModelStatusKind.Enabled or LibraryModelStatusKind.Untested)
                && (cloudAccountKeyPresent || !UsesCloudAccountKey(row)),
            SizeMb = ExtractSizeMb(row.SizeDescription)
        };
    }

    private static bool UsesCloudAccountKey(LibraryModel row) => row.Payload switch
    {
        CloudTranscriptionModel model => HealthEndpoints.UsesCloudAccountKey(model.Provider),
        LanguageModelInfo model => model.Provider == PostProcessingProvider.HyperWhisperCloud,
        _ => false
    };

    private static string ProviderId(LibraryModel row)
        => row.LocationKind == LibraryModelLocationKind.Offline
            ? "local"
            : row.ProviderName;

    /// <summary>
    /// Library rows store size as a human string ("1.5 GB", "474 MB"). Parse
    /// it back into megabytes for `/models`. Returns null when no size is
    /// known (cloud models, custom endpoints).
    /// </summary>
    private static double? ExtractSizeMb(string? sizeDescription)
    {
        if (string.IsNullOrWhiteSpace(sizeDescription)) return null;
        var trimmed = sizeDescription.Trim();

        int i = 0;
        while (i < trimmed.Length && (char.IsDigit(trimmed[i]) || trimmed[i] == '.' || trimmed[i] == ',')) i++;
        if (i == 0) return null;

        var numericPart = trimmed[..i].Replace(",", ".", StringComparison.Ordinal);
        if (!double.TryParse(numericPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = trimmed[i..].TrimStart().ToUpperInvariant();
        return unit switch
        {
            var u when u.StartsWith("GB", StringComparison.Ordinal) => value * 1024.0,
            var u when u.StartsWith("MB", StringComparison.Ordinal) => value,
            var u when u.StartsWith("KB", StringComparison.Ordinal) => value / 1024.0,
            _ => value
        };
    }
}
