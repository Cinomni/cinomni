using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDedupKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dedup_key",
                schema: "notifications",
                table: "notifications",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_dedup_key",
                schema: "notifications",
                table: "notifications",
                column: "dedup_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_notifications_dedup_key",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "dedup_key",
                schema: "notifications",
                table: "notifications");
        }
    }
}
