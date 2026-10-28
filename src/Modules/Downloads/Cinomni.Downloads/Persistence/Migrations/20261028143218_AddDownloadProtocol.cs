using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "protocol",
                schema: "downloads",
                table: "download_tasks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Torrent");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "protocol",
                schema: "downloads",
                table: "download_tasks");
        }
    }
}
