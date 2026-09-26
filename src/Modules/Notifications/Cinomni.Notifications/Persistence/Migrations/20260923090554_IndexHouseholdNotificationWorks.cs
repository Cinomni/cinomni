using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexHouseholdNotificationWorks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_notifications_household_work",
                schema: "notifications",
                table: "notifications",
                column: "work_id",
                filter: "work_id IS NOT NULL AND NOT admin_only");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_household_work",
                schema: "notifications",
                table: "notifications");
        }
    }
}
