using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCloudProviderValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Azure MAI and Google Chirp 3 are surfaced as HyperWhisper Cloud
            // accuracy tiers since PR #521 — modes that were never opened in
            // the editor still carry the legacy standalone provider values.
            // Fold them onto provider=hyperwhisper + the matching accuracy tier
            // so the editor, transcription routing, and Local API all agree.
            // Only overwrite CloudAccuracyTier when the user hasn't customised
            // it (null/empty or the default 'deepgramNova3'). Otherwise a user
            // who paired e.g. 'microsoftazurespeech' with a non-default tier
            // would silently lose their tier choice on next launch.
            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudAccuracyTier = CASE
                    WHEN LOWER(CloudProvider) = 'microsoftazurespeech'
                         AND (CloudAccuracyTier IS NULL OR CloudAccuracyTier = '' OR CloudAccuracyTier = 'deepgramNova3')
                        THEN 'azureMaiTranscribe'
                    WHEN LOWER(CloudProvider) = 'googlespeech'
                         AND (CloudAccuracyTier IS NULL OR CloudAccuracyTier = '' OR CloudAccuracyTier = 'deepgramNova3')
                        THEN 'googleChirp3'
                    ELSE CloudAccuracyTier
                END,
                    CloudProvider = 'hyperwhisper'
                WHERE LOWER(CloudProvider) IN ('microsoftazurespeech', 'googlespeech');
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudProvider = CASE CloudAccuracyTier
                    WHEN 'azureMaiTranscribe' THEN 'microsoftazurespeech'
                    WHEN 'googleChirp3' THEN 'googlespeech'
                    ELSE CloudProvider
                END
                WHERE CloudProvider = 'hyperwhisper'
                  AND CloudAccuracyTier IN ('azureMaiTranscribe', 'googleChirp3');
                """
            );
        }
    }
}
