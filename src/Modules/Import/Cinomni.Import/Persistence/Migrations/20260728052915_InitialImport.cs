using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Import.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "import");

            migrationBuilder.CreateTable(
                name: "import_jobs",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    download_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    matched_file_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    matched_file_size = table.Column<long>(type: "bigint", nullable: false),
                    matched_file_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    target_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_info_json = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "file_operations",
                schema: "import",
                columns: table => new
                {
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    to_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_operations", x => new { x.import_job_id, x.seq });
                    table.ForeignKey(
                        name: "fk_file_operations_import_jobs_import_job_id",
                        column: x => x.import_job_id,
                        principalSchema: "import",
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_history",
                schema: "import",
                columns: table => new
                {
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_history", x => new { x.import_job_id, x.seq });
                    table.ForeignKey(
                        name: "fk_import_history_import_jobs_import_job_id",
                        column: x => x.import_job_id,
                        principalSchema: "import",
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_download_task_id",
                schema: "import",
                table: "import_jobs",
                column: "download_task_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_intent_id",
                schema: "import",
                table: "import_jobs",
                column: "intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_state",
                schema: "import",
                table: "import_jobs",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_operations",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_history",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_jobs",
                schema: "import");
        }
    }
}
