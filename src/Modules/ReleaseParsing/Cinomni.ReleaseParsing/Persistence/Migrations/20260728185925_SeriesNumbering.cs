using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.ReleaseParsing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeriesNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "absolute_episode",
                schema: "parsing",
                table: "parsed_releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "air_date",
                schema: "parsing",
                table: "parsed_releases",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episode_from",
                schema: "parsing",
                table: "parsed_releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episode_to",
                schema: "parsing",
                table: "parsed_releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "numbering_json",
                schema: "parsing",
                table: "parsed_releases",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "part",
                schema: "parsing",
                table: "parsed_releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "season",
                schema: "parsing",
                table: "parsed_releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "season_to",
                schema: "parsing",
                table: "parsed_releases",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_parsed_releases_season_episode",
                schema: "parsing",
                table: "parsed_releases",
                columns: new[] { "season", "episode_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_parsed_releases_season_episode",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "absolute_episode",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "air_date",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "episode_from",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "episode_to",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "numbering_json",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "part",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "season",
                schema: "parsing",
                table: "parsed_releases");

            migrationBuilder.DropColumn(
                name: "season_to",
                schema: "parsing",
                table: "parsed_releases");
        }
    }
}
