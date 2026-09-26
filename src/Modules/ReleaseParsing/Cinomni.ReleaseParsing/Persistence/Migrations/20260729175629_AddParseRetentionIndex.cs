using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.ReleaseParsing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddParseRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_parsed_releases_created_at",
                schema: "parsing",
                table: "parsed_releases",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_parsed_releases_created_at",
                schema: "parsing",
                table: "parsed_releases");
        }
    }
}
