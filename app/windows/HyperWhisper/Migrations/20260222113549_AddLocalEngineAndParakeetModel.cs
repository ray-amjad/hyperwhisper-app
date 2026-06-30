using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalEngineAndParakeetModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalEngine",
                table: "Modes",
                type: "TEXT",
                nullable: false,
                defaultValue: "whisper");

            migrationBuilder.AddColumn<string>(
                name: "LocalParakeetModel",
                table: "Modes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalEngine",
                table: "Modes");

            migrationBuilder.DropColumn(
                name: "LocalParakeetModel",
                table: "Modes");
        }
    }
}
