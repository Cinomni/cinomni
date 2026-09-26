using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Downloads.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckpointTrimIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_download_tasks_checkpoint_trim",
                schema: "downloads",
                table: "download_tasks",
                columns: new[] { "state", "updated_at" },
                filter: "resume_data IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_download_tasks_checkpoint_trim",
                schema: "downloads",
                table: "download_tasks");
        }
    }
}
