using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Library.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetUnitLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_unit_links",
                schema: "library",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_unit_links", x => new { x.asset_id, x.unit_id });
                    table.ForeignKey(
                        name: "fk_asset_unit_links_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "library",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_unit_links_unit_id",
                schema: "library",
                table: "asset_unit_links",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_unit_links",
                schema: "library");
        }
    }
}
