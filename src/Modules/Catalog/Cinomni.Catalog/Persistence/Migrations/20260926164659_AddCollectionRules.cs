using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "collection_pinned",
                schema: "catalog",
                table: "works",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "placed_by_rule_id",
                schema: "catalog",
                table: "works",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rule_priority",
                schema: "catalog",
                table: "collections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing collections are asked in the order they were created — a stable, explainable order
            // with no ties, which the operator can then change. Nothing is claimed by a rule until one exists.
            migrationBuilder.Sql("""
                UPDATE catalog.collections AS c
                SET rule_priority = ordered.priority
                FROM (
                    SELECT id, (row_number() OVER (ORDER BY created_at, id) - 1)::integer AS priority
                    FROM catalog.collections
                ) AS ordered
                WHERE c.id = ordered.id;
                """);

            // Before this migration the only way off the default collection was a deliberate placement by an
            // operator. Those works are pinned, so the first rule anyone saves cannot sweep a title off a
            // restricted shelf and onto the open default one — the upgrade must not change who sees what.
            migrationBuilder.Sql("""
                UPDATE catalog.works AS w
                SET collection_pinned = true
                FROM catalog.collections AS c
                WHERE c.id = w.collection_id AND NOT c.is_default;
                """);

            migrationBuilder.CreateTable(
                name: "collection_rules",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_collection_rules_collections_collection_id",
                        column: x => x.collection_id,
                        principalSchema: "catalog",
                        principalTable: "collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_works_placed_by_rule",
                schema: "catalog",
                table: "works",
                column: "placed_by_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_rules_collection_position",
                schema: "catalog",
                table: "collection_rules",
                columns: new[] { "collection_id", "position" });

            migrationBuilder.AddForeignKey(
                name: "fk_works_collection_rules_placed_by_rule_id",
                schema: "catalog",
                table: "works",
                column: "placed_by_rule_id",
                principalSchema: "catalog",
                principalTable: "collection_rules",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_works_collection_rules_placed_by_rule_id",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropTable(
                name: "collection_rules",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_works_placed_by_rule",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "collection_pinned",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "placed_by_rule_id",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "rule_priority",
                schema: "catalog",
                table: "collections");
        }
    }
}
