using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Requests.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_media_requests_active_provider_external_id",
                schema: "requests",
                table: "media_requests");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "requests",
                table: "media_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Movie");

            migrationBuilder.CreateIndex(
                name: "ux_media_requests_active_provider_external_id_kind",
                schema: "requests",
                table: "media_requests",
                columns: new[] { "provider", "external_id", "kind" },
                unique: true,
                filter: "status <> 'Rejected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_media_requests_active_provider_external_id_kind",
                schema: "requests",
                table: "media_requests");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "requests",
                table: "media_requests");

            migrationBuilder.CreateIndex(
                name: "ux_media_requests_active_provider_external_id",
                schema: "requests",
                table: "media_requests",
                columns: new[] { "provider", "external_id" },
                unique: true,
                filter: "status <> 'Rejected'");
        }
    }
}
