using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlayback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "playback");

            migrationBuilder.CreateTable(
                name: "playback_progress",
                schema: "playback",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_ticks = table.Column<long>(type: "bigint", nullable: false),
                    played = table.Column<bool>(type: "boolean", nullable: false),
                    play_count = table.Column<int>(type: "integer", nullable: false),
                    audio_stream_index = table.Column<int>(type: "integer", nullable: true),
                    subtitle_stream_index = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_progress", x => new { x.user_id, x.asset_id });
                });

            migrationBuilder.CreateTable(
                name: "playback_sessions",
                schema: "playback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    container = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    play_session_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    plan_json = table.Column<string>(type: "jsonb", nullable: false),
                    audio_stream_index = table.Column<int>(type: "integer", nullable: true),
                    subtitle_stream_index = table.Column<int>(type: "integer", nullable: true),
                    position_ticks = table.Column<long>(type: "bigint", nullable: false),
                    duration_ticks = table.Column<long>(type: "bigint", nullable: false),
                    progress_seq = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transcode_jobs",
                schema: "playback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_video_codec = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    target_audio_codec = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    hls = table.Column<bool>(type: "boolean", nullable: false),
                    output_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcode_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_transcode_jobs_playback_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "playback",
                        principalTable: "playback_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_user_asset",
                schema: "playback",
                table: "playback_sessions",
                columns: new[] { "user_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transcode_jobs_session_id",
                schema: "playback",
                table: "transcode_jobs",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playback_progress",
                schema: "playback");

            migrationBuilder.DropTable(
                name: "transcode_jobs",
                schema: "playback");

            migrationBuilder.DropTable(
                name: "playback_sessions",
                schema: "playback");
        }
    }
}
