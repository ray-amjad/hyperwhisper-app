using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudTranscriptionDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive nullable column for the HyperWhisper Cloud X-STT-Domain
            // header (currently "medical" or null). Existing rows default to
            // null — no domain — which matches the pre-multi-provider behaviour.
            migrationBuilder.AddColumn<string>(
                name: "CloudTranscriptionDomain",
                table: "Modes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloudTranscriptionDomain",
                table: "Modes");
        }
    }
}
