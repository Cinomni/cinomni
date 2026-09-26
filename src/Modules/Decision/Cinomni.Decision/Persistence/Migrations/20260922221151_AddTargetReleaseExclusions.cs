using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Decision.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetReleaseExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "target_release_exclusions",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_target_release_exclusions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_target_release_exclusions_target_release",
                schema: "decision",
                table: "target_release_exclusions",
                columns: new[] { "target_id", "release_guid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "target_release_exclusions",
                schema: "decision");
        }
    }
}
