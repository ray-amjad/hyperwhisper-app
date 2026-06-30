using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignPlatformExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive nullable column holding the raw JSON of a mode's NON-Windows
            // platformExtensions slices (e.g. the macos blob), captured on
            // universal-v2 import and re-emitted on export so foreign per-mode data
            // survives a Windows round-trip (H4). Existing rows default to null.
            migrationBuilder.AddColumn<string>(
                name: "ForeignPlatformExtensions",
                table: "Modes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForeignPlatformExtensions",
                table: "Modes");
        }
    }
}
