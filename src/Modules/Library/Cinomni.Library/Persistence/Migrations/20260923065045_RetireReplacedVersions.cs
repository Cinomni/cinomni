using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Library.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireReplacedVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_media_versions_full_path",
                schema: "library",
                table: "media_versions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retired_at",
                schema: "library",
                table: "media_versions",
                type: "timestamp with time zone",
                nullable: true);

            // Versions of assets upgraded away before the column existed are history too — only those:
            // the code retires on an upgrade and nothing else. Without this
            // they would still count as live, and the replacement that landed on the same path could
            // never have been registered — which is exactly the defect this migration exists to end.
            migrationBuilder.Sql(
                """
                UPDATE library.media_versions AS v
                SET retired_at = now()
                FROM library.media_assets AS a
                WHERE a.id = v.asset_id AND a.state = 'Upgraded';
                """);

            migrationBuilder.CreateIndex(
                name: "ux_media_versions_full_path",
                schema: "library",
                table: "media_versions",
                column: "full_path",
                unique: true,
                filter: "retired_at IS NULL");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Fails once an upgrade has landed on the path of the version it replaced: the unrestricted index
        /// it recreates cannot hold both rows. That is the state this migration makes possible, so there
        /// is nothing earlier to return to without deciding which of the two rows to lose.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_media_versions_full_path",
                schema: "library",
                table: "media_versions");

            migrationBuilder.DropColumn(
                name: "retired_at",
                schema: "library",
                table: "media_versions");

            migrationBuilder.CreateIndex(
                name: "ux_media_versions_full_path",
                schema: "library",
                table: "media_versions",
                column: "full_path",
                unique: true);
        }
    }
}
