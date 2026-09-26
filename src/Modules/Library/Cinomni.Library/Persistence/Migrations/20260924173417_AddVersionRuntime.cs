using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Library.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bitrate",
                schema: "library",
                table: "media_versions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "duration_seconds",
                schema: "library",
                table: "media_versions",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bitrate",
                schema: "library",
                table: "media_versions");

            migrationBuilder.DropColumn(
                name: "duration_seconds",
                schema: "library",
                table: "media_versions");
        }
    }
}
