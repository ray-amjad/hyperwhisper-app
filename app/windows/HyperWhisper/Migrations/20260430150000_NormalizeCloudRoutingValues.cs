using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCloudRoutingValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudAccuracyTier = CASE CloudAccuracyTier
                    WHEN 'Medium' THEN 'groqWhisper'
                    WHEN 'medium' THEN 'groqWhisper'
                    WHEN 'High' THEN 'deepgramNova3'
                    WHEN 'high' THEN 'deepgramNova3'
                    WHEN 'Highest' THEN 'elevenLabsScribeV2'
                    WHEN 'highest' THEN 'elevenLabsScribeV2'
                    WHEN 'Grok' THEN 'grokStt'
                    WHEN 'grok' THEN 'grokStt'
                    ELSE CloudAccuracyTier
                END
                WHERE CloudAccuracyTier IN ('Medium', 'medium', 'High', 'high', 'Highest', 'highest', 'Grok', 'grok');
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudPostProcessingModel = CASE CloudPostProcessingModel
                    WHEN 'default' THEN 'cerebrasGptOss120B'
                    WHEN 'gpt-oss-120b' THEN 'cerebrasGptOss120B'
                    WHEN 'cerebras-gpt-oss-120b' THEN 'cerebrasGptOss120B'
                    WHEN 'grok-4-1-fast-non-reasoning' THEN 'grokFast'
                    WHEN 'grok-4.1-fast-non-reasoning' THEN 'grokFast'
                    WHEN 'claude-haiku-4-5' THEN 'claudeHaiku'
                    ELSE CloudPostProcessingModel
                END
                WHERE CloudPostProcessingModel IN (
                    'default',
                    'gpt-oss-120b',
                    'cerebras-gpt-oss-120b',
                    'grok-4-1-fast-non-reasoning',
                    'grok-4.1-fast-non-reasoning',
                    'claude-haiku-4-5'
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudAccuracyTier = CASE CloudAccuracyTier
                    WHEN 'groqWhisper' THEN 'Medium'
                    WHEN 'deepgramNova3' THEN 'High'
                    WHEN 'elevenLabsScribeV2' THEN 'Highest'
                    WHEN 'grokStt' THEN 'Grok'
                    ELSE CloudAccuracyTier
                END
                WHERE CloudAccuracyTier IN ('groqWhisper', 'deepgramNova3', 'elevenLabsScribeV2', 'grokStt');
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudPostProcessingModel = CASE CloudPostProcessingModel
                    WHEN 'cerebrasGptOss120B' THEN 'default'
                    WHEN 'grokFast' THEN 'grok-4-1-fast-non-reasoning'
                    WHEN 'claudeHaiku' THEN 'claude-haiku-4-5'
                    ELSE CloudPostProcessingModel
                END
                WHERE CloudPostProcessingModel IN ('cerebrasGptOss120B', 'grokFast', 'claudeHaiku');
                """
            );
        }
    }
}
