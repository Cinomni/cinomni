using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "works",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sort_title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    original_language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    runtime_minutes = table.Column<int>(type: "integer", nullable: true),
                    has_asset = table.Column<bool>(type: "boolean", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_works", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_identifiers",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_identifiers", x => x.id);
                    table.ForeignKey(
                        name: "fk_external_identifiers_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_external_identifiers_work_id",
                schema: "catalog",
                table: "external_identifiers",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "ux_external_identifiers_provider_value",
                schema: "catalog",
                table: "external_identifiers",
                columns: new[] { "provider", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_works_sort_title",
                schema: "catalog",
                table: "works",
                column: "sort_title");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_identifiers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "works",
                schema: "catalog");
        }
    }
}
