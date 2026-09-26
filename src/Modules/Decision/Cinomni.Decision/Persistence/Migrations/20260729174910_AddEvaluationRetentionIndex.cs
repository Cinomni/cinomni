using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Decision.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluationRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_release_evaluations_purge",
                schema: "decision",
                table: "release_evaluations",
                columns: new[] { "verdict", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_release_evaluations_purge",
                schema: "decision",
                table: "release_evaluations");
        }
    }
}
