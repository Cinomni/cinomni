using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionWorkId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable with no backfill on purpose: which work a session was for lives in Library and
            // Catalog, and this module may not read their tables to find out. A session that predates the
            // column therefore has no work to re-check access against, and the streamer serves it nothing
            // — fail closed. In practice they are already dead: the restart that applies this migration
            // kills every transcode and open stream anyway.
            migrationBuilder.AddColumn<Guid>(
                name: "work_id",
                schema: "playback",
                table: "playback_sessions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "work_id",
                schema: "playback",
                table: "playback_sessions");
        }
    }
}
