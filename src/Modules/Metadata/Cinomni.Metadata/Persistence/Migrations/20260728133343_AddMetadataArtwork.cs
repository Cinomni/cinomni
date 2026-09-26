using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Metadata.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataArtwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "metadata",
                table: "metadata_snapshots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Existing snapshots predate multi-kind support and are all movies.
                defaultValue: "Movie");

            migrationBuilder.CreateTable(
                name: "metadata_artwork",
                schema: "metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    vote_average = table.Column<double>(type: "double precision", nullable: true),
                    vote_count = table.Column<int>(type: "integer", nullable: true),
                    is_selected = table.Column<bool>(type: "boolean", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_artwork", x => x.id);
                    table.ForeignKey(
                        name: "fk_metadata_artwork_metadata_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "metadata",
                        principalTable: "metadata_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_artwork_snapshot_id",
                schema: "metadata",
                table: "metadata_artwork",
                column: "snapshot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metadata_artwork",
                schema: "metadata");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "metadata",
                table: "metadata_snapshots");
        }
    }
}
