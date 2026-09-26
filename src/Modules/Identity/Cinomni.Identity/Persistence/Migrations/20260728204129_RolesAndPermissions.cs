using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written rather than scaffolded: EF saw one boolean leave and another arrive and inferred
            // a rename of is_administrator into requests_auto_approved, which would have turned every
            // administrator into an auto-approved member and lost the role entirely. Instead the new column
            // is added, backfilled from the old flag, and only then is the flag dropped.
            migrationBuilder.AddColumn<string>(
                name: "role",
                schema: "identity",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Member");

            migrationBuilder.Sql(
                "UPDATE identity.users SET role = 'Administrator' WHERE is_administrator;");

            // Existing accounts keep what they could do before: an administrator's permissions are implicit,
            // and any member this installation already had was free to request.
            migrationBuilder.AddColumn<bool>(
                name: "can_request",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "requests_auto_approved",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.DropColumn(
                name: "is_administrator",
                schema: "identity",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_administrator",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE identity.users SET is_administrator = true WHERE role = 'Administrator';");

            migrationBuilder.DropColumn(
                name: "can_request",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "requests_auto_approved",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "identity",
                table: "users");
        }
    }
}
