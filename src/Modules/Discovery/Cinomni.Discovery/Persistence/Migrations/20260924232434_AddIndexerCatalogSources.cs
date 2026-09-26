using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <summary>
    /// Replaces the built-in catalog with administrator-subscribed sources. Additive for existing
    /// data: an indexer installed from the old built-in catalog keeps its catalog_key and its
    /// snapshotted definition, gains a null catalog_source_id, and runs exactly as before. The unique
    /// index moves from catalog_key alone to (catalog_source_id, catalog_key), because two sources may
    /// publish the same key. Rolling back recreates the catalog_key index, which fails if two sources'
    /// entries with the same key were installed in between; remove one of those indexers first.
    /// </summary>
    public partial class AddIndexerCatalogSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_indexers_catalog_key",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.AddColumn<Guid>(
                name: "catalog_source_id",
                schema: "discovery",
                table: "indexers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "indexer_catalog_sources",
                schema: "discovery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_refresh_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_refresh_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_refresh_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexer_catalog_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "indexer_catalog_entries",
                schema: "discovery",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    release_protocol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_urls = table.Column<List<string>>(type: "text[]", nullable: false),
                    requires_flare_solverr = table.Column<bool>(type: "boolean", nullable: false),
                    default_priority = table.Column<int>(type: "integer", nullable: false),
                    minimum_seeders = table.Column<int>(type: "integer", nullable: true),
                    prefer_magnet = table.Column<bool>(type: "boolean", nullable: false),
                    query_limit = table.Column<int>(type: "integer", nullable: true),
                    grab_limit = table.Column<int>(type: "integer", nullable: true),
                    limits_unit = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    use_flare_solverr = table.Column<bool>(type: "boolean", nullable: false),
                    raw_definition = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexer_catalog_entries", x => new { x.source_id, x.key });
                    table.ForeignKey(
                        name: "fk_indexer_catalog_entries_indexer_catalog_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "discovery",
                        principalTable: "indexer_catalog_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_indexers_catalog_source_id_catalog_key",
                schema: "discovery",
                table: "indexers",
                columns: new[] { "catalog_source_id", "catalog_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_indexer_catalog_sources_url",
                schema: "discovery",
                table: "indexer_catalog_sources",
                column: "url",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_indexers_indexer_catalog_sources_catalog_source_id",
                schema: "discovery",
                table: "indexers",
                column: "catalog_source_id",
                principalSchema: "discovery",
                principalTable: "indexer_catalog_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_indexers_indexer_catalog_sources_catalog_source_id",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropTable(
                name: "indexer_catalog_entries",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "indexer_catalog_sources",
                schema: "discovery");

            migrationBuilder.DropIndex(
                name: "ix_indexers_catalog_source_id_catalog_key",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "catalog_source_id",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.CreateIndex(
                name: "ix_indexers_catalog_key",
                schema: "discovery",
                table: "indexers",
                column: "catalog_key",
                unique: true);
        }
    }
}
