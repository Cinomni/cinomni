using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Playback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackWorkCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "unit_id",
                schema: "playback",
                table: "playback_progress",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "work_id",
                schema: "playback",
                table: "playback_progress",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_playback_progress_user_work",
                schema: "playback",
                table: "playback_progress",
                columns: new[] { "user_id", "work_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_playback_progress_user_work",
                schema: "playback",
                table: "playback_progress");

            migrationBuilder.DropColumn(
                name: "unit_id",
                schema: "playback",
                table: "playback_progress");

            migrationBuilder.DropColumn(
                name: "work_id",
                schema: "playback",
                table: "playback_progress");
        }
    }
}
