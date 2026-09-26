using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTunnelEgressGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "network_hold_reason",
                schema: "downloads",
                table: "download_tasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "network_hold_since",
                schema: "downloads",
                table: "download_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tunnel_state",
                schema: "downloads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    verified = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tunnel_device = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unverified_streak = table.Column<int>(type: "integer", nullable: false),
                    verified_streak = table.Column<int>(type: "integer", nullable: false),
                    transition_sequence = table.Column<int>(type: "integer", nullable: false),
                    holding = table.Column<bool>(type: "boolean", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tunnel_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_tasks_network_hold",
                schema: "downloads",
                table: "download_tasks",
                column: "network_hold_since",
                filter: "network_hold_since IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tunnel_state",
                schema: "downloads");

            migrationBuilder.DropIndex(
                name: "ix_download_tasks_network_hold",
                schema: "downloads",
                table: "download_tasks");

            migrationBuilder.DropColumn(
                name: "network_hold_reason",
                schema: "downloads",
                table: "download_tasks");

            migrationBuilder.DropColumn(
                name: "network_hold_since",
                schema: "downloads",
                table: "download_tasks");
        }
    }
}
