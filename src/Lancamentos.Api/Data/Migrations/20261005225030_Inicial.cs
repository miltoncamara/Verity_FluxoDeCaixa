using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lancamentos.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lancamentos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<DateOnly>(type: "date", nullable: false),
                    tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    valor = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lancamentos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    publicado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lancamentos_data",
                table: "lancamentos",
                column: "data");

            migrationBuilder.CreateIndex(
                name: "ix_lancamentos_idempotency_key",
                table: "lancamentos",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_criado_em",
                table: "outbox",
                column: "criado_em",
                filter: "publicado_em IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lancamentos");

            migrationBuilder.DropTable(
                name: "outbox");
        }
    }
}
