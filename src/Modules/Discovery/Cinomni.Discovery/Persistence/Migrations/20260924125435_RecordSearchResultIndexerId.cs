using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordSearchResultIndexerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "indexer_id",
                schema: "discovery",
                table: "search_results",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "indexer_id",
                schema: "discovery",
                table: "search_results");
        }
    }
}
