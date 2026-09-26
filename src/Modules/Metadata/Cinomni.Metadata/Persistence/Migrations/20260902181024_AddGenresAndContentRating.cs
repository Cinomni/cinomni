using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Metadata.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenresAndContentRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_rating",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "genres",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_rating",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "genres",
                schema: "metadata",
                table: "metadata_snapshots");
        }
    }
}
