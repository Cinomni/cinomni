using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProgressDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "duration_ticks",
                schema: "playback",
                table: "playback_progress",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Best-effort backfill from the sessions that are still retained (same schema): the longest
            // runtime any of the pair's sessions reported. Rows with none keep 0 until the next report.
            migrationBuilder.Sql("""
                UPDATE playback.playback_progress AS p
                SET duration_ticks = s.duration_ticks
                FROM (
                    SELECT user_id, asset_id, MAX(duration_ticks) AS duration_ticks
                    FROM playback.playback_sessions
                    WHERE duration_ticks > 0
                    GROUP BY user_id, asset_id
                ) AS s
                WHERE p.user_id = s.user_id AND p.asset_id = s.asset_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "duration_ticks",
                schema: "playback",
                table: "playback_progress");
        }
    }
}
