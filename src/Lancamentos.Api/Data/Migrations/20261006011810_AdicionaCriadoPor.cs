using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lancamentos.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdicionaCriadoPor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lancamentos_idempotency_key",
                table: "lancamentos");

            migrationBuilder.AddColumn<string>(
                name: "criado_por",
                table: "lancamentos",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                // Lançamentos gravados antes da auditoria não têm autor conhecido.
                defaultValue: "desconhecido");

            migrationBuilder.CreateIndex(
                name: "ix_lancamentos_criado_por_idempotency_key",
                table: "lancamentos",
                columns: new[] { "criado_por", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lancamentos_criado_por_idempotency_key",
                table: "lancamentos");

            migrationBuilder.DropColumn(
                name: "criado_por",
                table: "lancamentos");

            migrationBuilder.CreateIndex(
                name: "ix_lancamentos_idempotency_key",
                table: "lancamentos",
                column: "idempotency_key",
                unique: true);
        }
    }
}
