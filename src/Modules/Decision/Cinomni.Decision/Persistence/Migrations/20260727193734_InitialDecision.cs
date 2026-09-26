using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Decision.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "decision");

            migrationBuilder.CreateTable(
                name: "acquisition_profiles",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    min_format_score = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acquisition_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "release_evaluations",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    release_title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    canonical_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    custom_format_score = table.Column<int>(type: "integer", nullable: false),
                    evaluator_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_evaluations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "allowed_qualities",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    min_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    max_size_bytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allowed_qualities", x => x.id);
                    table.ForeignKey(
                        name: "fk_allowed_qualities_acquisition_profiles_profile_id",
                        column: x => x.profile_id,
                        principalSchema: "decision",
                        principalTable: "acquisition_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "format_rules",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    negate = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_format_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_format_rules_acquisition_profiles_profile_id",
                        column: x => x.profile_id,
                        principalSchema: "decision",
                        principalTable: "acquisition_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_reasons",
                schema: "decision",
                columns: table => new
                {
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    rule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    property = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    profile_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    actual_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    outcome = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    rejection = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_reasons", x => new { x.evaluation_id, x.seq });
                    table.ForeignKey(
                        name: "fk_decision_reasons_release_evaluations_evaluation_id",
                        column: x => x.evaluation_id,
                        principalSchema: "decision",
                        principalTable: "release_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "format_conditions",
                schema: "decision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    negate = table.Column<bool>(type: "boolean", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_format_conditions", x => x.id);
                    table.ForeignKey(
                        name: "fk_format_conditions_format_rules_rule_id",
                        column: x => x.rule_id,
                        principalSchema: "decision",
                        principalTable: "format_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allowed_qualities_profile_id",
                schema: "decision",
                table: "allowed_qualities",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_format_conditions_rule_id",
                schema: "decision",
                table: "format_conditions",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_format_rules_profile_id",
                schema: "decision",
                table: "format_rules",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_release_evaluations_search_id",
                schema: "decision",
                table: "release_evaluations",
                column: "search_id");

            migrationBuilder.CreateIndex(
                name: "ix_release_evaluations_target_id",
                schema: "decision",
                table: "release_evaluations",
                column: "target_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allowed_qualities",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "decision_reasons",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "format_conditions",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "release_evaluations",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "format_rules",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "acquisition_profiles",
                schema: "decision");
        }
    }
}
