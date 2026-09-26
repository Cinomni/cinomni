using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "available_episode_count",
                schema: "catalog",
                table: "works",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "episode_count",
                schema: "catalog",
                table: "works",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "structure_provider",
                schema: "catalog",
                table: "works",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "seasons",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    air_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expected_episode_count = table.Column<int>(type: "integer", nullable: true),
                    poster_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    metadata_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seasons", x => x.id);
                    table.ForeignKey(
                        name: "fk_seasons_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    season_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    season_number = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    absolute_number = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    air_date = table.Column<DateOnly>(type: "date", nullable: true),
                    air_date_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    runtime_minutes = table.Column<int>(type: "integer", nullable: true),
                    has_asset = table.Column<bool>(type: "boolean", nullable: false),
                    still_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    metadata_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_episodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_episodes_seasons_season_id",
                        column: x => x.season_id,
                        principalSchema: "catalog",
                        principalTable: "seasons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_season_id",
                schema: "catalog",
                table: "episodes",
                column: "season_id");

            migrationBuilder.CreateIndex(
                name: "ix_episodes_work_air_date",
                schema: "catalog",
                table: "episodes",
                columns: new[] { "work_id", "air_date" });

            migrationBuilder.CreateIndex(
                name: "ux_episodes_work_absolute_number",
                schema: "catalog",
                table: "episodes",
                columns: new[] { "work_id", "absolute_number" },
                unique: true,
                filter: "absolute_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_episodes_work_season_number",
                schema: "catalog",
                table: "episodes",
                columns: new[] { "work_id", "season_number", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_seasons_work_number",
                schema: "catalog",
                table: "seasons",
                columns: new[] { "work_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episodes",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seasons",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "available_episode_count",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "episode_count",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "structure_provider",
                schema: "catalog",
                table: "works");
        }
    }
}
