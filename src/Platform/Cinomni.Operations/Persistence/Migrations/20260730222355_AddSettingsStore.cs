using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setting",
                schema: "operations",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    secret_cipher = table.Column<byte[]>(type: "bytea", nullable: true),
                    secret_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setting", x => x.key);
                    table.CheckConstraint("ck_setting_value_xor_secret", "(value IS NULL) <> (secret_cipher IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "setting_audit",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    old_display = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    new_display = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setting_audit", x => x.id);
                    table.CheckConstraint("ck_setting_audit_action", "action IN ('Set','Cleared')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_setting_audit_key_changed_at",
                schema: "operations",
                table: "setting_audit",
                columns: new[] { "key", "changed_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setting",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "setting_audit",
                schema: "operations");
        }
    }
}
