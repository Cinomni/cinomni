using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters, and the scaffolder got it wrong: it emitted the works column first, with
            // defaultValue Guid.Empty. On a fresh database that applies cleanly and every test passes,
            // because there are no rows; on an installation with works in it, every row would point at the
            // all-zeros id and the foreign key below would reject it — a failure invisible in CI that
            // surfaces at a user's startup, half-way through MigrateCatalogAsync. So: tables first, then
            // the default collection, then the column whose DEFAULT *is* the backfill, then the key.
            migrationBuilder.CreateTable(
                name: "collections",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    access_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collection_grants",
                schema: "catalog",
                columns: table => new
                {
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_grants", x => new { x.collection_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_collection_grants_collections_collection_id",
                        column: x => x.collection_id,
                        principalSchema: "catalog",
                        principalTable: "collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_collections_name",
                schema: "catalog",
                table: "collections",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_collections_default",
                schema: "catalog",
                table: "collections",
                column: "is_default",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_collection_grants_user",
                schema: "catalog",
                table: "collection_grants",
                column: "user_id");

            // The shelf every installation starts with. Open, so an upgrade leaves every existing account
            // seeing exactly what it saw before: Catalog cannot enumerate accounts to write them grants,
            // because accounts belong to Identity. The id is interpolated from the constant so the seeded
            // row and the backfill below cannot drift apart.
            migrationBuilder.Sql($"""
                INSERT INTO catalog.collections (id, name, kind, access_mode, is_default, created_at)
                VALUES ('{DefaultCollection.Id}', '{DefaultCollection.Name}', 'Mixed', 'Open', true, now());
                """);

            // The DEFAULT is the backfill: Postgres fills every existing row as it adds the column, which
            // for a series covers its whole season/episode tree at once — they hang off the work, so
            // placing the work places everything under it. Dropping the default afterwards leaves the
            // application as the sole chooser of a collection.
            migrationBuilder.Sql(
                $"ALTER TABLE catalog.works ADD COLUMN collection_id uuid NOT NULL DEFAULT '{DefaultCollection.Id}';");
            migrationBuilder.Sql("ALTER TABLE catalog.works ALTER COLUMN collection_id DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "ix_works_collection_sort_title",
                schema: "catalog",
                table: "works",
                columns: new[] { "collection_id", "sort_title" });

            // A collection that still holds works cannot be deleted.
            migrationBuilder.AddForeignKey(
                name: "fk_works_collections_collection_id",
                schema: "catalog",
                table: "works",
                column: "collection_id",
                principalSchema: "catalog",
                principalTable: "collections",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_works_collections_collection_id",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropIndex(
                name: "ix_works_collection_sort_title",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "collection_id",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropTable(
                name: "collection_grants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "collections",
                schema: "catalog");
        }
    }
}
