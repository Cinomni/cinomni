using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Import.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportJobRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "recovery_attempts",
                schema: "import",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "requested_units_json",
                schema: "import",
                table: "import_jobs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "recovery_attempts",
                schema: "import",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "requested_units_json",
                schema: "import",
                table: "import_jobs");
        }
    }
}
