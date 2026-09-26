using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalIdentifierKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_external_identifiers_provider_value",
                schema: "catalog",
                table: "external_identifiers");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "catalog",
                table: "external_identifiers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            // Every existing id takes its work's kind, and the placeholder default goes: a row with no
            // kind would be one no lookup could ever match, and the application always writes one.
            migrationBuilder.Sql(
                """
                UPDATE catalog.external_identifiers AS e
                SET kind = w.kind
                FROM catalog.works AS w
                WHERE w.id = e.work_id;

                ALTER TABLE catalog.external_identifiers ALTER COLUMN kind DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_external_identifiers_provider_value_kind",
                schema: "catalog",
                table: "external_identifiers",
                columns: new[] { "provider", "value", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Fails, deliberately, once a film and a show share a provider id: the old index cannot hold
        /// both, and dropping one of them to make it fit would lose a work's identity.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_external_identifiers_provider_value_kind",
                schema: "catalog",
                table: "external_identifiers");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "catalog",
                table: "external_identifiers");

            migrationBuilder.CreateIndex(
                name: "ux_external_identifiers_provider_value",
                schema: "catalog",
                table: "external_identifiers",
                columns: new[] { "provider", "value" },
                unique: true);
        }
    }
}
