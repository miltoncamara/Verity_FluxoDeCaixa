using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lancamentos.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdicionaTraceParentNaOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                table: "outbox",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                table: "outbox");
        }
    }
}
