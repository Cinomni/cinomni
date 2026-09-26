using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBuiltInIndexerCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "catalog_key",
                schema: "discovery",
                table: "indexers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "catalog_version",
                schema: "discovery",
                table: "indexers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "grab_limit",
                schema: "discovery",
                table: "indexers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_test_code",
                schema: "discovery",
                table: "indexers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_test_message",
                schema: "discovery",
                table: "indexers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "last_test_succeeded",
                schema: "discovery",
                table: "indexers",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_tested_at",
                schema: "discovery",
                table: "indexers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "limits_unit",
                schema: "discovery",
                table: "indexers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Day");

            migrationBuilder.AddColumn<int>(
                name: "minimum_seeders",
                schema: "discovery",
                table: "indexers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "prefer_magnet",
                schema: "discovery",
                table: "indexers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "query_limit",
                schema: "discovery",
                table: "indexers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "use_flare_solverr",
                schema: "discovery",
                table: "indexers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "indexer_usage",
                schema: "discovery",
                columns: table => new
                {
                    indexer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    usage_date = table.Column<DateOnly>(type: "date", nullable: false),
                    query_count = table.Column<int>(type: "integer", nullable: false),
                    grab_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexer_usage", x => new { x.indexer_id, x.usage_date });
                    table.ForeignKey(
                        name: "fk_indexer_usage_indexers_indexer_id",
                        column: x => x.indexer_id,
                        principalSchema: "discovery",
                        principalTable: "indexers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_indexers_catalog_key",
                schema: "discovery",
                table: "indexers",
                column: "catalog_key",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_indexers_grab_limit",
                schema: "discovery",
                table: "indexers",
                sql: "grab_limit IS NULL OR grab_limit > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_indexers_minimum_seeders",
                schema: "discovery",
                table: "indexers",
                sql: "minimum_seeders IS NULL OR minimum_seeders >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_indexers_query_limit",
                schema: "discovery",
                table: "indexers",
                sql: "query_limit IS NULL OR query_limit > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "indexer_usage",
                schema: "discovery");

            migrationBuilder.DropIndex(
                name: "ix_indexers_catalog_key",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_indexers_grab_limit",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_indexers_minimum_seeders",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_indexers_query_limit",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "catalog_key",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "catalog_version",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "grab_limit",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "last_test_code",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "last_test_message",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "last_test_succeeded",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "last_tested_at",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "limits_unit",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "minimum_seeders",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "prefer_magnet",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "query_limit",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "use_flare_solverr",
                schema: "discovery",
                table: "indexers");
        }
    }
}
