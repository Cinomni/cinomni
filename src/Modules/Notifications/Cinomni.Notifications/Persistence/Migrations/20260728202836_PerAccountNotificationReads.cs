using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerAccountNotificationReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Read state moves from a column on the notification to a receipt per (notification, account),
            // so one member acknowledging something no longer clears everyone else's badge. There is no
            // way to attribute the old column — it records that *somebody* read it, not who — and this
            // module must not read identity's tables to guess, so anything already read comes back unread
            // once. The partial index over read_at from the previous migration goes with the column.
            migrationBuilder.DropIndex(
                name: "ix_notifications_audience_read_created",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "read_at",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.CreateTable(
                name: "notification_reads",
                schema: "notifications",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_reads", x => new { x.notification_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_notification_reads_notifications_notification_id",
                        column: x => x.notification_id,
                        principalSchema: "notifications",
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_audience_created",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "admin_only", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_reads_user",
                schema: "notifications",
                table: "notification_reads",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_reads",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notifications_audience_created",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "read_at",
                schema: "notifications",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_audience_read_created",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "admin_only", "read_at", "created_at" });

            // Restore the badge index the audience migration added alongside the column.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_notifications_unread
                ON notifications.notifications (created_at)
                WHERE read_at IS NULL;
                """);
        }
    }
}
