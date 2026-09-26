using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Library.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "library");

            migrationBuilder.CreateTable(
                name: "media_assets",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    primary_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asset_target_links",
                schema: "library",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_target_links", x => new { x.asset_id, x.target_id });
                    table.ForeignKey(
                        name: "fk_asset_target_links_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "library",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_versions",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relative_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    full_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    quality_json = table.Column<string>(type: "jsonb", nullable: false),
                    release_group = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_versions_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "library",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_streams",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_index = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    codec = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    channels = table.Column<int>(type: "integer", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    bit_depth = table.Column<int>(type: "integer", nullable: true),
                    video_range_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_forced = table.Column<bool>(type: "boolean", nullable: false),
                    is_external = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_streams", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_streams_media_versions_media_version_id",
                        column: x => x.media_version_id,
                        principalSchema: "library",
                        principalTable: "media_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_target_links_target_id",
                schema: "library",
                table: "asset_target_links",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_work_id",
                schema: "library",
                table: "media_assets",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_streams_type",
                schema: "library",
                table: "media_streams",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ux_media_streams_version_index",
                schema: "library",
                table: "media_streams",
                columns: new[] { "media_version_id", "stream_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_versions_asset_id",
                schema: "library",
                table: "media_versions",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ux_media_versions_full_path",
                schema: "library",
                table: "media_versions",
                column: "full_path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_target_links",
                schema: "library");

            migrationBuilder.DropTable(
                name: "media_streams",
                schema: "library");

            migrationBuilder.DropTable(
                name: "media_versions",
                schema: "library");

            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "library");
        }
    }
}
