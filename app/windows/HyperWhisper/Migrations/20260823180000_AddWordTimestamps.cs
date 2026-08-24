using HyperWhisper.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations;

[DbContext(typeof(HyperWhisperDbContext))]
[Migration("20260823180000_AddWordTimestamps")]
public sealed class AddWordTimestamps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WordTimestampsJson",
            table: "Transcripts",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "WordTimestampsJson",
            table: "Transcripts");
    }
}
