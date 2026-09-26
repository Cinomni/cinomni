using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscodeJobFallback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fallback_reason",
                schema: "playback",
                table: "transcode_jobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "fell_back_to_software",
                schema: "playback",
                table: "transcode_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fallback_reason",
                schema: "playback",
                table: "transcode_jobs");

            migrationBuilder.DropColumn(
                name: "fell_back_to_software",
                schema: "playback",
                table: "transcode_jobs");
        }
    }
}
