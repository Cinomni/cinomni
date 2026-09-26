using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RememberAcceptedTotpStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "totp_last_accepted_step",
                schema: "identity",
                table: "users",
                type: "bigint",
                nullable: true);

            // A negative open-request limit is refused from now on, but one could already be stored, and
            // Requests read it as "no limit". Zero is the explicit spelling of that, so nothing an account
            // may do changes — and the next permission edit, which sends the stored limit back, is not
            // refused for a value the administrator never meant to choose.
            migrationBuilder.Sql(
                "UPDATE identity.users SET open_request_limit = 0 WHERE open_request_limit < 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "totp_last_accepted_step",
                schema: "identity",
                table: "users");
        }
    }
}
