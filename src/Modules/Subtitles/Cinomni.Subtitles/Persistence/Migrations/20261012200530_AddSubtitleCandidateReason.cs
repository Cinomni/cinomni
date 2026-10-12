using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Subtitles.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubtitleCandidateReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "subtitles",
                table: "subtitle_candidates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason",
                schema: "subtitles",
                table: "subtitle_candidates");
        }
    }
}
