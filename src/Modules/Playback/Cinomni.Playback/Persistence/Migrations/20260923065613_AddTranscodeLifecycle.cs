using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscodeLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "process_id",
                schema: "playback",
                table: "transcode_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "process_started_at",
                schema: "playback",
                table: "transcode_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                schema: "playback",
                table: "playback_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_transcode_jobs_state",
                schema: "playback",
                table: "transcode_jobs",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transcode_jobs_state",
                schema: "playback",
                table: "transcode_jobs");

            migrationBuilder.DropColumn(
                name: "process_id",
                schema: "playback",
                table: "transcode_jobs");

            migrationBuilder.DropColumn(
                name: "process_started_at",
                schema: "playback",
                table: "transcode_jobs");

            migrationBuilder.DropColumn(
                name: "end_reason",
                schema: "playback",
                table: "playback_sessions");
        }
    }
}
