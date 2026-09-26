using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                schema: "notifications",
                table: "notifications",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_created_at",
                schema: "notifications",
                table: "notifications");
        }
    }
}
