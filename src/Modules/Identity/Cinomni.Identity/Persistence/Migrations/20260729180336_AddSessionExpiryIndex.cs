using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionExpiryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_sessions_expires_at",
                schema: "identity",
                table: "sessions",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_expires_at",
                schema: "identity",
                table: "sessions");
        }
    }
}
