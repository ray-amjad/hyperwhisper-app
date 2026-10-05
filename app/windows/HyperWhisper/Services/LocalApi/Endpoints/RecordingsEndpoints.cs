using System.Globalization;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace HyperWhisper.Services.LocalApi.Endpoints;

/// <summary>
/// `GET /recordings/search` and `GET /recordings/{id}` — read-only projection
/// over the Transcript history table. `q`, `since`, `until`, the count and
/// `limit` run in SQL through <see cref="HistoryService.QueryPage"/> (issue
/// #1123), not over the whole table. There is intentionally no DELETE —
/// the macOS API exposes none and adding it Windows-only would break parity.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RecordingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app, LocalApiServer server)
    {
        app.MapGet("/recordings", ListRecordings);
        app.MapGet("/recordings/search", ListRecordings);

        app.MapGet("/recordings/{id}", (string id) =>
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    "Invalid recording id");
            }
            var rec = HistoryService.Instance.GetTranscript(guid);
            if (rec == null)
            {
                // macOS reuses `LocalAPIErrorCode.modeNotFound` for missing
                // recordings — we mirror that to keep the closed error set
                // stable across platforms.
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.ModeNotFound,
                    $"No recording with id '{id}'");
            }
            return LocalApiResponder.Ok(new RecordingResponse { Recording = ToDto(rec) });
        });
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static IResult ListRecordings(HttpContext ctx)
    {
        var q = ctx.Request.Query["q"].ToString().Trim();
        var since = ParseDateOrEpoch(ctx.Request.Query["since"]);
        var until = ParseDateOrEpoch(ctx.Request.Query["until"]);
        var limit = ClampLimit(ctx.Request.Query["limit"], fallback: 50, min: 1, max: 500);

        var (page, total) = HistoryService.Instance.QueryPage(q, since, until, limit);
        var recordings = page.Select(ToDto).ToList();
        return LocalApiResponder.Ok(new RecordingsListResponse
        {
            Total = total,
            Returned = recordings.Count,
            Recordings = recordings
        });
    }

    private static RecordingDto ToDto(Transcript t)
    {
        return new RecordingDto
        {
            Id = t.Id.ToString("D"),
            Text = t.Text ?? "",
            PostProcessedText = t.PostProcessedText,
            TranscribedText = t.TranscribedText,
            Date = DateTime.SpecifyKind(t.Date, DateTimeKind.Utc),
            Duration = t.Duration,
            Mode = t.Mode,
            TranscriptionProvider = t.TranscriptionProvider,
            PostProcessingProvider = t.PostProcessingProvider,
            Status = t.Status.ToString().ToLowerInvariant(),
            AudioFilePath = t.AudioFilePath
        };
    }

    /// <summary>
    /// Accepts ISO8601 (`2025-01-01T00:00:00Z`) or numeric epoch seconds
    /// (`1704067200`). Empty / unparseable returns null.
    /// </summary>
    private static DateTime? ParseDateOrEpoch(StringValues raw)
    {
        var s = raw.ToString().Trim();
        if (s.Length == 0) return null;

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        if (DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var iso))
        {
            return iso;
        }

        return null;
    }

    private static int ClampLimit(StringValues raw, int fallback, int min, int max)
    {
        var s = raw.ToString().Trim();
        if (s.Length == 0) return fallback;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return fallback;
        }
        if (n < min) return min;
        if (n > max) return max;
        return n;
    }
}
