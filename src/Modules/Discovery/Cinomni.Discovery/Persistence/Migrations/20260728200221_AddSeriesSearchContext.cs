using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesSearchContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "discovery",
                table: "search_results",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episode_number",
                schema: "discovery",
                table: "search_results",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "season_number",
                schema: "discovery",
                table: "search_results",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tvdb_id",
                schema: "discovery",
                table: "search_results",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "absolute_number",
                schema: "discovery",
                table: "search_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "air_date",
                schema: "discovery",
                table: "search_executions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episode_number",
                schema: "discovery",
                table: "search_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "imdb_id",
                schema: "discovery",
                table: "search_executions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid[]>(
                name: "requested_unit_ids",
                schema: "discovery",
                table: "search_executions",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<int>(
                name: "season_number",
                schema: "discovery",
                table: "search_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                schema: "discovery",
                table: "search_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tvdb_id",
                schema: "discovery",
                table: "search_executions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "work_id",
                schema: "discovery",
                table: "search_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "movie_categories",
                schema: "discovery",
                table: "indexers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "movie_search_params",
                schema: "discovery",
                table: "indexers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // TRUE, not the scaffolded FALSE: an indexer registered before capabilities existed was
            // queried with t=movie and must keep being queried with t=movie. Defaulting these to
            // false would silently downgrade every existing install to the t=search fallback.
            migrationBuilder.AddColumn<bool>(
                name: "supports_movie_search",
                schema: "discovery",
                table: "indexers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "supports_tv_search",
                schema: "discovery",
                table: "indexers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "tv_categories",
                schema: "discovery",
                table: "indexers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tv_search_params",
                schema: "discovery",
                table: "indexers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category",
                schema: "discovery",
                table: "search_results");

            migrationBuilder.DropColumn(
                name: "episode_number",
                schema: "discovery",
                table: "search_results");

            migrationBuilder.DropColumn(
                name: "season_number",
                schema: "discovery",
                table: "search_results");

            migrationBuilder.DropColumn(
                name: "tvdb_id",
                schema: "discovery",
                table: "search_results");

            migrationBuilder.DropColumn(
                name: "absolute_number",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "air_date",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "episode_number",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "imdb_id",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "requested_unit_ids",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "season_number",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "target_id",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "tvdb_id",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "work_id",
                schema: "discovery",
                table: "search_executions");

            migrationBuilder.DropColumn(
                name: "movie_categories",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "movie_search_params",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "supports_movie_search",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "supports_tv_search",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "tv_categories",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "tv_search_params",
                schema: "discovery",
                table: "indexers");
        }
    }
}
