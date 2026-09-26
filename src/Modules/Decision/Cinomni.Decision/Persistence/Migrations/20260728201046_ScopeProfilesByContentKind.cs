using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Decision.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeProfilesByContentKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Movie", not the scaffolded empty string: every profile that exists today was written
            // for the movie slice and is the movie profile. Backfilling it to "" would leave it
            // matching no scope at all, so the seeder would mint a second movie profile beside it and
            // the engine would only ever reach the original through its untyped fallback.
            migrationBuilder.AddColumn<string>(
                name: "applies_to",
                schema: "decision",
                table: "acquisition_profiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Movie");

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_profiles_applies_to",
                schema: "decision",
                table: "acquisition_profiles",
                column: "applies_to");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_acquisition_profiles_applies_to",
                schema: "decision",
                table: "acquisition_profiles");

            migrationBuilder.DropColumn(
                name: "applies_to",
                schema: "decision",
                table: "acquisition_profiles");
        }
    }
}
