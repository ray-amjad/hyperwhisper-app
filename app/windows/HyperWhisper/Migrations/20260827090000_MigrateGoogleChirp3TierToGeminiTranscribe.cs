using HyperWhisper.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations;

/// <summary>
/// Catalog v8 replaced <c>googleChirp3</c> with <c>geminiTranscribe</c> as the
/// Google HyperWhisper Cloud tier, so every persisted <c>CloudAccuracyTier</c>
/// carrying the old id (or one of its aliases) now names a tier with no catalog
/// entry.
///
/// Read-time rescue alone is NOT enough. <c>CloudAccuracyTierExtensions.FromString</c>
/// does resolve the alias through the catalog, but the fallback on every arm that
/// misses is <c>DeepgramNova3</c> — so a single unrescued read path silently moves
/// the user off Google. Converge the stored data once, here, instead of relying on
/// every reader.
///
/// This file lives under <c>app/windows/</c> but is compiled by
/// <c>HyperWhisper.Application.csproj</c> through a <c>&lt;Compile Include&gt;</c> source
/// glob over <c>Migrations/**</c>, so it runs on Windows AND Linux. Data-only —
/// no schema change, so no ModelSnapshot bump and no Designer file (precedent:
/// <c>20260823180000_AddWordTimestamps</c> for the shape,
/// <c>20260508120000_MigrateRemovedDeepgramModels</c> for the data-only Up/Down).
/// </summary>
[DbContext(typeof(HyperWhisperDbContext))]
[Migration("20260827090000_MigrateGoogleChirp3TierToGeminiTranscribe")]
public sealed class MigrateGoogleChirp3TierToGeminiTranscribe : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Aliases mirror the geminiTranscribe entry's `migrateFrom` in
        // shared-app-classification/cloud-stt-catalog.json. `googlespeech` is in
        // that list too, but it only reaches the TIER slot via the separate
        // `legacyCloudProviderAliases` fold that 20260608120000 already ran, so
        // matching it here as a tier value is belt-and-braces, not a new rule.
        //
        // The model is remapped in the same statement: `chirp_3` is not a model
        // of the new tier, so leaving it would make the Mode editor fall back to
        // the tier default anyway — writing it makes the stored row honest.
        migrationBuilder.Sql(
            """
            UPDATE Modes
            SET CloudTranscriptionModel = CASE
                WHEN CloudTranscriptionModel IS NULL OR CloudTranscriptionModel = '' OR LOWER(CloudTranscriptionModel) = 'chirp_3'
                    THEN 'gemini-3.5-transcribe'
                ELSE CloudTranscriptionModel
            END,
                CloudAccuracyTier = 'geminiTranscribe'
            WHERE LOWER(CloudAccuracyTier) IN (
                'googlechirp3', 'googlechirp', 'google-chirp', 'chirp', 'chirp_3', 'googlespeech'
            );
            """
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentional no-op. Rolling back cannot tell a migrated Chirp user apart
        // from someone who chose Gemini 3.5 Transcribe deliberately, and Chirp 3
        // is no longer a selectable tier in any client, so restoring the old id
        // would strand the row on an unknown tier.
    }
}
