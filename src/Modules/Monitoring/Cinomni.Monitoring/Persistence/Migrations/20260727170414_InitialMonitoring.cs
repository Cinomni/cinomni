using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Monitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "monitoring");

            migrationBuilder.CreateTable(
                name: "monitored_targets",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    monitored = table.Column<bool>(type: "boolean", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_missing = table.Column<bool>(type: "boolean", nullable: false),
                    acquisition_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_search_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitored_targets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_monitored_missing",
                schema: "monitoring",
                table: "monitored_targets",
                columns: new[] { "monitored", "is_missing" });

            migrationBuilder.CreateIndex(
                name: "ux_monitored_targets_work_id",
                schema: "monitoring",
                table: "monitored_targets",
                column: "work_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitored_targets",
                schema: "monitoring");
        }
    }
}
