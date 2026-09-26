using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Decision.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCutoffToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The scaffolder proposed 0, which reads as "every title already meets the cutoff" and would
            // silently disable upgrades on every existing profile. 50 is the seeded default (Bluray
            // 1080p), so an installation that turns upgrades on behaves like a freshly seeded one.
            migrationBuilder.AddColumn<int>(
                name: "cutoff_rank",
                schema: "decision",
                table: "acquisition_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            // Off for profiles that already exist, and deliberately so: an upgrade re-downloads a title
            // and replaces the file on disk, and doing that to a working library the moment someone
            // updates is not a decision this migration gets to make. A newly seeded profile has them on;
            // an existing one is switched on by its owner.
            migrationBuilder.AddColumn<bool>(
                name: "upgrades_allowed",
                schema: "decision",
                table: "acquisition_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cutoff_rank",
                schema: "decision",
                table: "acquisition_profiles");

            migrationBuilder.DropColumn(
                name: "upgrades_allowed",
                schema: "decision",
                table: "acquisition_profiles");
        }
    }
}
