using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeriveAbsoluteEpisodeNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "absolute_number_is_derived",
                schema: "catalog",
                table: "episodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "absolute_number_is_derived",
                schema: "catalog",
                table: "episodes");
        }
    }
}
