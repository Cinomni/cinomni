using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "totp_confirmed_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "totp_secret_cipher",
                schema: "identity",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "totp_secret_key_id",
                schema: "identity",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "totp_secret_nonce",
                schema: "identity",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "login_challenges",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_challenges", x => x.id);
                    table.ForeignKey(
                        name: "fk_login_challenges_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recovery_codes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_totp_secret_complete",
                schema: "identity",
                table: "users",
                sql: "(totp_secret_cipher IS NULL) = (totp_secret_nonce IS NULL) AND (totp_secret_cipher IS NULL) = (totp_secret_key_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_login_challenges_expires_at",
                schema: "identity",
                table: "login_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_login_challenges_user_id",
                schema: "identity",
                table: "login_challenges",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_login_challenges_hash",
                schema: "identity",
                table: "login_challenges",
                column: "challenge_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recovery_codes_user",
                schema: "identity",
                table: "recovery_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_recovery_codes_hash",
                schema: "identity",
                table: "recovery_codes",
                column: "code_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_challenges",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "recovery_codes",
                schema: "identity");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_totp_secret_complete",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_confirmed_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_secret_cipher",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_secret_key_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_secret_nonce",
                schema: "identity",
                table: "users");
        }
    }
}
