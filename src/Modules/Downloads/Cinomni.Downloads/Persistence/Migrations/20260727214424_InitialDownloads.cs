using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "downloads");

            migrationBuilder.CreateTable(
                name: "download_tasks",
                schema: "downloads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_guid = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    download_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    save_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    info_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    progress = table.Column<double>(type: "double precision", nullable: false),
                    download_rate = table.Column<long>(type: "bigint", nullable: false),
                    upload_rate = table.Column<long>(type: "bigint", nullable: false),
                    num_peers = table.Column<int>(type: "integer", nullable: false),
                    num_seeds = table.Column<int>(type: "integer", nullable: false),
                    all_time_upload = table.Column<long>(type: "bigint", nullable: false),
                    all_time_download = table.Column<long>(type: "bigint", nullable: false),
                    seeding_seconds = table.Column<int>(type: "integer", nullable: false),
                    content_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resume_data = table.Column<byte[]>(type: "bytea", nullable: true),
                    checkpoint_seq = table.Column<int>(type: "integer", nullable: false),
                    seeding_policy_json = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "download_history",
                schema: "downloads",
                columns: table => new
                {
                    download_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_history", x => new { x.download_task_id, x.seq });
                    table.ForeignKey(
                        name: "fk_download_history_download_tasks_download_task_id",
                        column: x => x.download_task_id,
                        principalSchema: "downloads",
                        principalTable: "download_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "torrent_files",
                schema: "downloads",
                columns: table => new
                {
                    download_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_torrent_files", x => new { x.download_task_id, x.index });
                    table.ForeignKey(
                        name: "fk_torrent_files_download_tasks_download_task_id",
                        column: x => x.download_task_id,
                        principalSchema: "downloads",
                        principalTable: "download_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_tasks_attempt_id",
                schema: "downloads",
                table: "download_tasks",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_download_tasks_info_hash",
                schema: "downloads",
                table: "download_tasks",
                column: "info_hash");

            migrationBuilder.CreateIndex(
                name: "ix_download_tasks_intent_id",
                schema: "downloads",
                table: "download_tasks",
                column: "intent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "download_history",
                schema: "downloads");

            migrationBuilder.DropTable(
                name: "torrent_files",
                schema: "downloads");

            migrationBuilder.DropTable(
                name: "download_tasks",
                schema: "downloads");
        }
    }
}
