using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Monitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishedAirDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_published_air_date",
                schema: "monitoring",
                table: "monitored_targets",
                column: "published_air_date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_monitored_targets_published_air_date",
                schema: "monitoring",
                table: "monitored_targets");
        }
    }
}
