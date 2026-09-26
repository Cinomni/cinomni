using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexerSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "indexer_sessions",
                schema: "discovery",
                columns: table => new
                {
                    indexer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cookies_cipher = table.Column<byte[]>(type: "bytea", nullable: true),
                    cookies_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    cookies_key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_ok = table.Column<bool>(type: "boolean", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexer_sessions", x => x.indexer_id);
                    table.CheckConstraint("ck_indexer_sessions_cookies_cipher_with_nonce", "(cookies_cipher IS NULL) = (cookies_nonce IS NULL) AND (cookies_cipher IS NULL) = (cookies_key_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_indexer_sessions_indexers_indexer_id",
                        column: x => x.indexer_id,
                        principalSchema: "discovery",
                        principalTable: "indexers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "indexer_sessions",
                schema: "discovery");
        }
    }
}
