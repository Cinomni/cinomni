using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Decision.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "release_blocks",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    release_title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_blocks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_release_blocks_release_guid",
                schema: "decision",
                table: "release_blocks",
                column: "release_guid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_blocks",
                schema: "decision");
        }
    }
}
