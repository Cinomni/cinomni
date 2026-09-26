using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexerCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "credential_username",
                schema: "discovery",
                table: "indexers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "secret_cipher",
                schema: "discovery",
                table: "indexers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secret_key_id",
                schema: "discovery",
                table: "indexers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "secret_nonce",
                schema: "discovery",
                table: "indexers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_indexers_secret_cipher_with_nonce",
                schema: "discovery",
                table: "indexers",
                sql: "(secret_cipher IS NULL) = (secret_nonce IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_indexers_secret_cipher_with_nonce",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "credential_username",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "secret_cipher",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "secret_key_id",
                schema: "discovery",
                table: "indexers");

            migrationBuilder.DropColumn(
                name: "secret_nonce",
                schema: "discovery",
                table: "indexers");
        }
    }
}
