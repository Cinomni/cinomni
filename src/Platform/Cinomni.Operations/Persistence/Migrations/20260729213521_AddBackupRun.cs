using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_run",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    dump_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    dump_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_run", x => x.id);
                    table.CheckConstraint("ck_backup_run_outcome", "outcome IN ('Running','Succeeded','Failed','Skipped','Interrupted')");
                    table.CheckConstraint("ck_backup_run_trigger", "triggered_by IN ('Scheduled','Manual')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_run_started",
                schema: "operations",
                table: "backup_run",
                column: "started_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_run",
                schema: "operations");
        }
    }
}
