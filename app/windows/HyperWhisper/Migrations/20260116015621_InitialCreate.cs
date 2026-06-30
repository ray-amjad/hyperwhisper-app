using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyperWhisper.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Modes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Preset = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSystemProvided = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    ModelType = table.Column<string>(type: "TEXT", nullable: true),
                    CloudProvider = table.Column<string>(type: "TEXT", nullable: true),
                    CloudTranscriptionModel = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderType = table.Column<string>(type: "TEXT", nullable: true),
                    Punctuation = table.Column<bool>(type: "INTEGER", nullable: false),
                    Capitalization = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProfanityFilter = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnglishSpelling = table.Column<string>(type: "TEXT", nullable: true),
                    PostProcessingMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PostProcessingProvider = table.Column<string>(type: "TEXT", nullable: true),
                    LanguageModel = table.Column<string>(type: "TEXT", nullable: true),
                    UserSystemPrompt = table.Column<string>(type: "TEXT", nullable: true),
                    CustomInstructions = table.Column<string>(type: "TEXT", nullable: true),
                    CustomVocabulary = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Modes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordingSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationInSeconds = table.Column<double>(type: "REAL", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    SampleRate = table.Column<double>(type: "REAL", nullable: false),
                    ChannelCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AudioFormat = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AudioFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageTracking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DailyTranscriptionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalModelsDownloaded = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstUsageDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastResetDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastValidationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LicenseActivatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LicenseStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerEmail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageTracking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Word = table.Column<string>(type: "TEXT", nullable: false),
                    Replacement = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transcripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TranscribedText = table.Column<string>(type: "TEXT", nullable: true),
                    PostProcessedText = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Duration = table.Column<double>(type: "REAL", nullable: false),
                    AudioFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    TrimmedAudioFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedReason = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptionProvider = table.Column<string>(type: "TEXT", nullable: true),
                    PostProcessingProvider = table.Column<string>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", nullable: true),
                    ModeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecordingSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRetryDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transcripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transcripts_RecordingSessions_RecordingSessionId",
                        column: x => x.RecordingSessionId,
                        principalTable: "RecordingSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Modes_IsDefault",
                table: "Modes",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_Modes_Name",
                table: "Modes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_RecordingSessions_StartTime",
                table: "RecordingSessions",
                column: "StartTime");

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_Date",
                table: "Transcripts",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_RecordingSessionId",
                table: "Transcripts",
                column: "RecordingSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_Status",
                table: "Transcripts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyItems_Word",
                table: "VocabularyItems",
                column: "Word");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Modes");

            migrationBuilder.DropTable(
                name: "Transcripts");

            migrationBuilder.DropTable(
                name: "UsageTracking");

            migrationBuilder.DropTable(
                name: "VocabularyItems");

            migrationBuilder.DropTable(
                name: "RecordingSessions");
        }
    }
}
