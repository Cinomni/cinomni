using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_purge",
                schema: "operations",
                table: "outbox",
                column: "published_at",
                filter: "published = true");

            migrationBuilder.CreateIndex(
                name: "ix_command_purge",
                schema: "operations",
                table: "command",
                columns: new[] { "state", "queued_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_purge",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropIndex(
                name: "ix_command_purge",
                schema: "operations",
                table: "command");
        }
    }
}
