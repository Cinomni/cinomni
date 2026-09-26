using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexerDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "definition_id",
                schema: "discovery",
                table: "indexers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "indexer_definitions",
                schema: "discovery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_content = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexer_definitions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_indexers_definition_id",
                schema: "discovery",
                table: "indexers",
                column: "definition_id");

            migrationBuilder.AddForeignKey(
                name: "fk_indexers_indexer_definitions_definition_id",
                schema: "discovery",
                table: "indexers",
                column: "definition_id",
                principalSchema: "discovery",
                principalTable: "indexer_definitions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_indexers_indexer_definitions_definition_id",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropTable(
                name: "indexer_definitions",
                schema: "discovery");

            migrationBuilder.DropIndex(
                name: "ix_indexers_definition_id",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "definition_id",
                schema: "discovery",
                table: "indexers");
        }
    }
}
