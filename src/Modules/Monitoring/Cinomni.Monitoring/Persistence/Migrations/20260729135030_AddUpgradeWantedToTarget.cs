using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Monitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradeWantedToTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // False is the intended value for every existing row, not just the scaffolder's habit: a
            // target nobody has judged is left alone, so the first sweep after an update does not set
            // off an upgrade hunt across the whole library. Decision sets it after an import.
            migrationBuilder.AddColumn<bool>(
                name: "upgrade_wanted",
                schema: "monitoring",
                table: "monitored_targets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "upgrade_wanted",
                schema: "monitoring",
                table: "monitored_targets");
        }
    }
}
