using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Subtitles.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubtitleSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "original_path",
                schema: "subtitles",
                table: "subtitle_assets",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            // Every subtitle landed before synchronization existed plays its download, so that file is
            // its original. Without this the column would read empty and a revert would point at nothing.
            migrationBuilder.Sql("UPDATE subtitles.subtitle_assets SET original_path = path;");

            migrationBuilder.CreateTable(
                name: "subtitle_sync_decisions",
                schema: "subtitles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subtitle_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    offset_ms = table.Column<int>(type: "integer", nullable: false),
                    ratio = table.Column<double>(type: "double precision", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    margin = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subtitle_sync_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subtitle_sync_work",
                schema: "subtitles",
                columns: table => new
                {
                    subtitle_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subtitle_sync_work", x => x.subtitle_asset_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subtitle_sync_decisions_subtitle_created",
                schema: "subtitles",
                table: "subtitle_sync_decisions",
                columns: new[] { "subtitle_asset_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subtitle_sync_work_requested_at",
                schema: "subtitles",
                table: "subtitle_sync_work",
                column: "requested_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subtitle_sync_decisions",
                schema: "subtitles");

            migrationBuilder.DropTable(
                name: "subtitle_sync_work",
                schema: "subtitles");

            migrationBuilder.DropColumn(
                name: "original_path",
                schema: "subtitles",
                table: "subtitle_assets");
        }
    }
}
