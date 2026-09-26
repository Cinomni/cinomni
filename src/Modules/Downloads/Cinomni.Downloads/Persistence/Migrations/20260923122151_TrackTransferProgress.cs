using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackTransferProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "best_progress",
                schema: "downloads",
                table: "download_tasks",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_progress_at",
                schema: "downloads",
                table: "download_tasks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "best_progress",
                schema: "downloads",
                table: "download_tasks");

            migrationBuilder.DropColumn(
                name: "last_progress_at",
                schema: "downloads",
                table: "download_tasks");
        }
    }
}
