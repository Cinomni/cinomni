using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "operations",
                table: "outbox",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_state",
                schema: "operations",
                table: "outbox",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "operations",
                table: "command",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_state",
                schema: "operations",
                table: "command",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "trace_state",
                schema: "operations",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "operations",
                table: "command");

            migrationBuilder.DropColumn(
                name: "trace_state",
                schema: "operations",
                table: "command");
        }
    }
}
