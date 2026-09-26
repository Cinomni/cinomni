using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeadLetterOutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_unpublished",
                schema: "operations",
                table: "outbox");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "operations",
                table: "outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dead_lettered_at",
                schema: "operations",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "operations",
                table: "outbox",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "operations",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_unpublished",
                schema: "operations",
                table: "outbox",
                column: "occurred_at",
                filter: "published = false AND dead_lettered_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_unpublished",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "dead_lettered_at",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "operations",
                table: "outbox");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_unpublished",
                schema: "operations",
                table: "outbox",
                column: "occurred_at",
                filter: "published = false");
        }
    }
}
