using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Acquisition.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordAttemptRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "indexer_name",
                schema: "acquisition",
                table: "acquisition_attempts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "leechers",
                schema: "acquisition",
                table: "acquisition_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_title",
                schema: "acquisition",
                table: "acquisition_attempts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "seeders",
                schema: "acquisition",
                table: "acquisition_attempts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "indexer_name",
                schema: "acquisition",
                table: "acquisition_attempts");

            migrationBuilder.DropColumn(
                name: "leechers",
                schema: "acquisition",
                table: "acquisition_attempts");

            migrationBuilder.DropColumn(
                name: "release_title",
                schema: "acquisition",
                table: "acquisition_attempts");

            migrationBuilder.DropColumn(
                name: "seeders",
                schema: "acquisition",
                table: "acquisition_attempts");
        }
    }
}
