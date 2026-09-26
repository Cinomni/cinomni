using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.ReleaseParsing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReleaseParsing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "parsing");

            migrationBuilder.CreateTable(
                name: "parse_rule_versions",
                schema: "parsing",
                columns: table => new
                {
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parse_rule_versions", x => x.version);
                });

            migrationBuilder.CreateTable(
                name: "parsed_releases",
                schema: "parsing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    release_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quality_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision_json = table.Column<string>(type: "jsonb", nullable: false),
                    languages_json = table.Column<string>(type: "jsonb", nullable: false),
                    release_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    edition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    canonical_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    info_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    parser_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parsed_releases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_parsed_releases_canonical_key",
                schema: "parsing",
                table: "parsed_releases",
                column: "canonical_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "parse_rule_versions",
                schema: "parsing");

            migrationBuilder.DropTable(
                name: "parsed_releases",
                schema: "parsing");
        }
    }
}
