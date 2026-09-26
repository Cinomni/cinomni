using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Requests.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "requests");

            migrationBuilder.CreateTable(
                name: "media_requests",
                schema: "requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: true),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_requests_requested_by",
                schema: "requests",
                table: "media_requests",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_requests_status_requested",
                schema: "requests",
                table: "media_requests",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_requests_work",
                schema: "requests",
                table: "media_requests",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "ux_media_requests_active_provider_external_id",
                schema: "requests",
                table: "media_requests",
                columns: new[] { "provider", "external_id" },
                unique: true,
                filter: "status <> 'Rejected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_requests",
                schema: "requests");
        }
    }
}
