using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Import.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportFileMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_file_matches",
                schema: "import",
                columns: table => new
                {
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    source_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    target_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    operation_seq = table.Column<int>(type: "integer", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    season_number = table.Column<int>(type: "integer", nullable: true),
                    episode_numbers = table.Column<int[]>(type: "integer[]", nullable: false),
                    media_info_json = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_file_matches", x => new { x.import_job_id, x.seq });
                    table.ForeignKey(
                        name: "fk_import_file_matches_import_jobs_import_job_id",
                        column: x => x.import_job_id,
                        principalSchema: "import",
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_import_file_matches_asset_id",
                schema: "import",
                table: "import_file_matches",
                column: "asset_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_file_matches",
                schema: "import");
        }
    }
}
