using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                schema: "downloads",
                table: "download_tasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "work_id",
                schema: "downloads",
                table: "download_tasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "target_id",
                schema: "downloads",
                table: "download_tasks");

            migrationBuilder.DropColumn(
                name: "work_id",
                schema: "downloads",
                table: "download_tasks");
        }
    }
}
