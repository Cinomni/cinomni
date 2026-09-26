using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Subtitles.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubtitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "subtitles");

            migrationBuilder.CreateTable(
                name: "subtitle_assets",
                schema: "subtitles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    forced = table.Column<bool>(type: "boolean", nullable: false),
                    hearing_impaired = table.Column<bool>(type: "boolean", nullable: false),
                    format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subtitle_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subtitle_searches",
                schema: "subtitles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    forced = table.Column<bool>(type: "boolean", nullable: false),
                    hearing_impaired = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    subtitle_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subtitle_searches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subtitle_candidates",
                schema: "subtitles",
                columns: table => new
                {
                    search_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    release = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    hearing_impaired = table.Column<bool>(type: "boolean", nullable: false),
                    download_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subtitle_candidates", x => new { x.search_id, x.seq });
                    table.ForeignKey(
                        name: "fk_subtitle_candidates_subtitle_searches_search_id",
                        column: x => x.search_id,
                        principalSchema: "subtitles",
                        principalTable: "subtitle_searches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subtitle_assets_asset_id",
                schema: "subtitles",
                table: "subtitle_assets",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ux_subtitle_searches_asset_language",
                schema: "subtitles",
                table: "subtitle_searches",
                columns: new[] { "asset_id", "language", "forced", "hearing_impaired" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subtitle_assets",
                schema: "subtitles");

            migrationBuilder.DropTable(
                name: "subtitle_candidates",
                schema: "subtitles");

            migrationBuilder.DropTable(
                name: "subtitle_searches",
                schema: "subtitles");
        }
    }
}
