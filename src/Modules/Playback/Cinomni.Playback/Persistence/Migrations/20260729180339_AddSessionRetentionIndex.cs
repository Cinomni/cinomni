using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_purge",
                schema: "playback",
                table: "playback_sessions",
                columns: new[] { "state", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_playback_sessions_purge",
                schema: "playback",
                table: "playback_sessions");
        }
    }
}
