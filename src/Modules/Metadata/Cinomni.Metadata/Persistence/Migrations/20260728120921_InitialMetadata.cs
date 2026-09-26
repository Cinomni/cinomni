using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Metadata.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "metadata");

            migrationBuilder.CreateTable(
                name: "metadata_snapshots",
                schema: "metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    original_title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    overview = table.Column<string>(type: "text", nullable: true),
                    runtime_minutes = table.Column<int>(type: "integer", nullable: true),
                    original_language = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    poster_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    backdrop_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    raw_response = table.Column<string>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_states",
                schema: "metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_snapshots_work_id",
                schema: "metadata",
                table: "metadata_snapshots",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_states_work_provider",
                schema: "metadata",
                table: "refresh_states",
                columns: new[] { "work_id", "provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metadata_snapshots",
                schema: "metadata");

            migrationBuilder.DropTable(
                name: "refresh_states",
                schema: "metadata");
        }
    }
}
