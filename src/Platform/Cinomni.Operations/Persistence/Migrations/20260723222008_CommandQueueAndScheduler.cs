using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommandQueueAndScheduler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "command",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command", x => x.id);
                    table.CheckConstraint("ck_command_state", "state IN ('Queued','Running','Completed','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "scheduled_job",
                schema: "operations",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    command_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    last_run = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_due = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_job", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_command_queued",
                schema: "operations",
                table: "command",
                column: "queued_at",
                filter: "state = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ux_command_idempotency_key",
                schema: "operations",
                table: "command",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_job_due",
                schema: "operations",
                table: "scheduled_job",
                column: "next_due",
                filter: "enabled = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "command",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "scheduled_job",
                schema: "operations");
        }
    }
}
