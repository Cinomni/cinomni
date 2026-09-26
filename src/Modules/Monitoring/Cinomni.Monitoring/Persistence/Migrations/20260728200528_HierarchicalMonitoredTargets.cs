using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Monitoring.Persistence.Migrations
{
    /// <summary>
    /// Turns <c>monitored_targets</c> into a self-referencing tree (series → season → episode).
    /// <para>
    /// The only destructive step in the whole series slice lives here, and it is deliberately paired:
    /// <c>ux_monitored_targets_work_id</c> is dropped — one work now holds a root, N seasons and M
    /// episodes, so it is no longer satisfiable — and <c>ux_monitored_targets_work_ref</c> on
    /// <c>(work_id, kind, target_ref)</c> is created in the <b>same</b> migration. That constraint is the
    /// concurrency guard the module converges on; leaving the table unguarded even briefly would let two
    /// concurrent policy applies create duplicate roots, and the root lookup would then return an
    /// arbitrary one. Existing movie rows stay unique under it as <c>(work_id, 'Movie', work_id)</c>.
    /// </para>
    /// </summary>
    public partial class HierarchicalMonitoredTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_monitored_targets_monitored_missing",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropIndex(
                name: "ux_monitored_targets_work_id",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.AddColumn<int>(
                name: "absolute_number",
                schema: "monitoring",
                table: "monitored_targets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "air_date",
                schema: "monitoring",
                table: "monitored_targets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episode_number",
                schema: "monitoring",
                table: "monitored_targets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "episode_title",
                schema: "monitoring",
                table: "monitored_targets",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_target_id",
                schema: "monitoring",
                table: "monitored_targets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "season_number",
                schema: "monitoring",
                table: "monitored_targets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_monitored_missing",
                schema: "monitoring",
                table: "monitored_targets",
                columns: new[] { "monitored", "is_missing", "air_date" });

            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_parent",
                schema: "monitoring",
                table: "monitored_targets",
                column: "parent_target_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_work_id",
                schema: "monitoring",
                table: "monitored_targets",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "ux_monitored_targets_work_ref",
                schema: "monitoring",
                table: "monitored_targets",
                columns: new[] { "work_id", "kind", "target_ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_monitored_targets_monitored_missing",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropIndex(
                name: "ix_monitored_targets_parent",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropIndex(
                name: "ix_monitored_targets_work_id",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropIndex(
                name: "ux_monitored_targets_work_ref",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropColumn(
                name: "absolute_number",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropColumn(
                name: "air_date",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropColumn(
                name: "episode_number",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropColumn(
                name: "episode_title",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropColumn(
                name: "parent_target_id",
                schema: "monitoring",
                table: "monitored_targets");

            migrationBuilder.DropColumn(
                name: "season_number",
                schema: "monitoring",
                table: "monitored_targets");

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
    }
}
