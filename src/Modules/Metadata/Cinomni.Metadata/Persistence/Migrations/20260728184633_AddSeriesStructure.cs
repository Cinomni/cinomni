using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Metadata.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "first_aired",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "imdb_id",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "last_aired",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "season_order",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "series_status",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tmdb_id",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tvdb_id",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episode_number",
                schema: "metadata",
                table: "metadata_artwork",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "season_number",
                schema: "metadata",
                table: "metadata_artwork",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "metadata_episodes",
                schema: "metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    season_number = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    absolute_number = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    overview = table.Column<string>(type: "text", nullable: true),
                    air_date = table.Column<DateOnly>(type: "date", nullable: true),
                    air_date_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    runtime_minutes = table.Column<int>(type: "integer", nullable: true),
                    still_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_special = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_episodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_metadata_episodes_metadata_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "metadata",
                        principalTable: "metadata_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_seasons",
                schema: "metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    overview = table.Column<string>(type: "text", nullable: true),
                    episode_count = table.Column<int>(type: "integer", nullable: true),
                    air_date = table.Column<DateOnly>(type: "date", nullable: true),
                    poster_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_seasons", x => x.id);
                    table.ForeignKey(
                        name: "fk_metadata_seasons_metadata_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "metadata",
                        principalTable: "metadata_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_episodes_air_date",
                schema: "metadata",
                table: "metadata_episodes",
                column: "air_date");

            migrationBuilder.CreateIndex(
                name: "ix_metadata_episodes_snapshot_absolute",
                schema: "metadata",
                table: "metadata_episodes",
                columns: new[] { "snapshot_id", "absolute_number" },
                filter: "absolute_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_metadata_episodes_snapshot_season_number",
                schema: "metadata",
                table: "metadata_episodes",
                columns: new[] { "snapshot_id", "season_number", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_metadata_seasons_snapshot_number",
                schema: "metadata",
                table: "metadata_seasons",
                columns: new[] { "snapshot_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metadata_episodes",
                schema: "metadata");

            migrationBuilder.DropTable(
                name: "metadata_seasons",
                schema: "metadata");

            migrationBuilder.DropColumn(
                name: "first_aired",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "imdb_id",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "last_aired",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "season_order",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "series_status",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "tmdb_id",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "tvdb_id",
                schema: "metadata",
                table: "metadata_snapshots");

            migrationBuilder.DropColumn(
                name: "episode_number",
                schema: "metadata",
                table: "metadata_artwork");

            migrationBuilder.DropColumn(
                name: "season_number",
                schema: "metadata",
                table: "metadata_artwork");
        }
    }
}
