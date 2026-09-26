using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentCeiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_ceiling",
                schema: "identity",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_ceiling_region",
                schema: "identity",
                table: "users",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_content_ceiling_complete",
                schema: "identity",
                table: "users",
                sql: "(content_ceiling IS NULL) = (content_ceiling_region IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_content_ceiling_complete",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "content_ceiling",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "content_ceiling_region",
                schema: "identity",
                table: "users");
        }
    }
}
