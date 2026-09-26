using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_read_created",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.AddColumn<bool>(
                name: "admin_only",
                schema: "notifications",
                table: "notifications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_audience_read_created",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "admin_only", "read_at", "created_at" });

            // Rows written before this column existed default to false. Provider degradation was already
            // an operator concern then, so an account invited after the upgrade must not inherit it.
            migrationBuilder.Sql(
                "UPDATE notifications.notifications SET admin_only = true WHERE type = 'provider-degraded';");

            // An administrator sees every audience, so their badge query filters on read_at alone and the
            // audience index cannot serve it: keep a partial index for the unread count.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_notifications_unread
                ON notifications.notifications (created_at)
                WHERE read_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS notifications.ix_notifications_unread;");

            migrationBuilder.DropIndex(
                name: "ix_notifications_audience_read_created",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "admin_only",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_read_created",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "read_at", "created_at" });
        }
    }
}
