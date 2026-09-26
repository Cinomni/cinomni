using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Acquisition.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "acquisition");

            migrationBuilder.CreateTable(
                name: "acquisition_intents",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    selected_release_guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acquisition_intents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "acquisition_attempts",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    download_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acquisition_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_acquisition_attempts_acquisition_intents_intent_id",
                        column: x => x.intent_id,
                        principalSchema: "acquisition",
                        principalTable: "acquisition_intents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "acquisition_history",
                schema: "acquisition",
                columns: table => new
                {
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acquisition_history", x => new { x.intent_id, x.seq });
                    table.ForeignKey(
                        name: "fk_acquisition_history_acquisition_intents_intent_id",
                        column: x => x.intent_id,
                        principalSchema: "acquisition",
                        principalTable: "acquisition_intents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_attempts_intent_evaluation",
                schema: "acquisition",
                table: "acquisition_attempts",
                columns: new[] { "intent_id", "evaluation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_attempts_intent_id",
                schema: "acquisition",
                table: "acquisition_attempts",
                column: "intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_intents_target_id",
                schema: "acquisition",
                table: "acquisition_intents",
                column: "target_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_acquisition_intents_work_id",
                schema: "acquisition",
                table: "acquisition_intents",
                column: "work_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acquisition_attempts",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "acquisition_history",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "acquisition_intents",
                schema: "acquisition");
        }
    }
}
