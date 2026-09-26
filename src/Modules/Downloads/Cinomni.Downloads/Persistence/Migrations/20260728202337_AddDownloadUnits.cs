using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "download_task_claims",
                schema: "downloads",
                columns: table => new
                {
                    download_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_originating = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_task_claims", x => new { x.download_task_id, x.attempt_id });
                    table.ForeignKey(
                        name: "fk_download_task_claims_download_tasks_download_task_id",
                        column: x => x.download_task_id,
                        principalSchema: "downloads",
                        principalTable: "download_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "download_task_units",
                schema: "downloads",
                columns: table => new
                {
                    download_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_task_units", x => new { x.download_task_id, x.unit_id });
                    table.ForeignKey(
                        name: "fk_download_task_units_download_tasks_download_task_id",
                        column: x => x.download_task_id,
                        principalSchema: "downloads",
                        principalTable: "download_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_task_claims_intent_id",
                schema: "downloads",
                table: "download_task_claims",
                column: "intent_id");

            migrationBuilder.CreateIndex(
                name: "ux_download_task_claims_attempt_id",
                schema: "downloads",
                table: "download_task_claims",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_download_task_units_unit_id",
                schema: "downloads",
                table: "download_task_units",
                column: "unit_id");

            // Backfill both children from the columns the task already carries, so every task has a
            // claim and a unit regardless of when it was created. Without this an existing task would
            // look claim-less, and the add-dedup — which now keys on claims — could create a second
            // task for an attempt that already has one. A movie's unit IS its work id, so the
            // backfilled correlation is exactly right rather than an approximation.
            migrationBuilder.Sql(
                """
                INSERT INTO downloads.download_task_claims
                    (download_task_id, attempt_id, intent_id, target_id, is_originating, created_at)
                SELECT id, attempt_id, intent_id, target_id, TRUE, created_at
                FROM downloads.download_tasks
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO downloads.download_task_units (download_task_id, unit_id)
                SELECT id, work_id FROM downloads.download_tasks
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "download_task_claims",
                schema: "downloads");

            migrationBuilder.DropTable(
                name: "download_task_units",
                schema: "downloads");
        }
    }
}
