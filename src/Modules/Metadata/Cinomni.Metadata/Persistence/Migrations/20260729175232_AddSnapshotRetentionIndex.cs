using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Metadata.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_metadata_snapshots_fetched_at",
                schema: "metadata",
                table: "metadata_snapshots",
                column: "fetched_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_metadata_snapshots_fetched_at",
                schema: "metadata",
                table: "metadata_snapshots");
        }
    }
}
