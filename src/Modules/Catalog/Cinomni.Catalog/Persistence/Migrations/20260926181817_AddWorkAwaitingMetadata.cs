using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkAwaitingMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "awaiting_metadata",
                schema: "catalog",
                table: "works",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "awaiting_metadata",
                schema: "catalog",
                table: "works");
        }
    }
}
