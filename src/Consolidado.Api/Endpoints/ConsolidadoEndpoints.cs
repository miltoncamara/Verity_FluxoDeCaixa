using Consolidado.Api.Data;
using Consolidado.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Api.Endpoints;

public static class ConsolidadoEndpoints
{
    public static void MapConsolidadoEndpoints(this WebApplication app)
    {
        app.MapGet("/consolidado/{data}", ObterSaldoDoDia);
    }

    private static async Task<IResult> ObterSaldoDoDia(DateOnly data, ConsolidadoDbContext db, CancellationToken ct)
    {
        // Leitura por chave na tabela pré-calculada. Um dia sem lançamentos tem saldo zero.
        var saldo = await db.SaldosDiarios.AsNoTracking().FirstOrDefaultAsync(s => s.Data == data, ct)
            ?? SaldoDiario.Vazio(data);

        return Results.Ok(ConsolidadoResponse.De(saldo));
    }
}
