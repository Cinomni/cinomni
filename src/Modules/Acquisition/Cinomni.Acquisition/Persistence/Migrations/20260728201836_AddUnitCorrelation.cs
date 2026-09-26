using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Acquisition.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "unit_id",
                schema: "acquisition",
                table: "acquisition_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "acquisition_attempt_units",
                schema: "acquisition",
                columns: table => new
                {
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acquisition_attempt_units", x => new { x.attempt_id, x.unit_id });
                    table.ForeignKey(
                        name: "fk_acquisition_attempt_units_acquisition_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalSchema: "acquisition",
                        principalTable: "acquisition_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_intents_unit_id",
                schema: "acquisition",
                table: "acquisition_intents",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_attempt_units_unit_id",
                schema: "acquisition",
                table: "acquisition_attempt_units",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acquisition_attempt_units",
                schema: "acquisition");

            migrationBuilder.DropIndex(
                name: "ix_acquisition_intents_unit_id",
                schema: "acquisition",
                table: "acquisition_intents");

            migrationBuilder.DropColumn(
                name: "unit_id",
                schema: "acquisition",
                table: "acquisition_intents");
        }
    }
}
