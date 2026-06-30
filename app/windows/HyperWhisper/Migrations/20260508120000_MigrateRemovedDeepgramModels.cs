using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class MigrateRemovedDeepgramModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Collapse the 25 Deepgram model IDs removed in the 2026-05 catalog
            // cleanup onto Nova 3 General. Modes selecting any of these IDs would
            // otherwise fail to resolve in the picker after launch.
            migrationBuilder.Sql(
                """
                UPDATE Modes
                SET CloudTranscriptionModel = 'nova-3-general'
                WHERE ProviderType = 'cloud'
                AND CloudProvider = 'deepgram'
                AND CloudTranscriptionModel IN (
                    'nova-2-meeting', 'nova-2-phonecall', 'nova-2-voicemail',
                    'nova-2-finance', 'nova-2-conversationalai', 'nova-2-automotive',
                    'nova-2-video', 'nova', 'nova-phonecall',
                    'enhanced-general', 'enhanced-meeting', 'enhanced-phonecall', 'enhanced-finance',
                    'base-general', 'base-meeting', 'base-phonecall', 'base-voicemail',
                    'base-finance', 'base-conversationalai', 'base-video',
                    'whisper-tiny', 'whisper-base', 'whisper-small', 'whisper-medium', 'whisper-large'
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentional no-op. The original choice can't be recovered once
            // collapsed; rolling back leaves modes on Nova 3 General.
        }
    }
}
