using Lancamentos.Api.Data;
using Lancamentos.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lancamentos.Api.Endpoints;

public static class LancamentosEndpoints
{
    public static void MapLancamentosEndpoints(this WebApplication app)
    {
        app.MapPost("/lancamentos", RegistrarLancamento);
        app.MapGet("/lancamentos", ListarPorData);
        app.MapGet("/lancamentos/{id:guid}", ObterPorId);
    }

    private static async Task<IResult> RegistrarLancamento(
        NovoLancamentoRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        LancamentosDbContext db,
        CancellationToken ct)
    {
        var (lancamento, erros) = Lancamento.Criar(request.Data, request.Tipo, request.Valor, request.Descricao, idempotencyKey);
        if (lancamento is null)
            return Results.ValidationProblem(erros.ToDictionary(e => e.Key, e => new[] { e.Value }));

        // Requisição repetida com a mesma chave: devolve o lançamento original sem duplicar.
        if (idempotencyKey is not null)
        {
            var existente = await BuscarPorIdempotencyKey(db, idempotencyKey, ct);
            if (existente is not null)
                return Results.Ok(LancamentoResponse.De(existente));
        }

        // Invariante central: o lançamento e o seu evento são gravados no mesmo SaveChanges,
        // que o EF Core executa numa única transação. Ou os dois existem, ou nenhum existe.
        db.Lancamentos.Add(lancamento);
        db.Outbox.Add(OutboxMessage.LancamentoRegistrado(lancamento));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null
            && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Duas requisições simultâneas com a mesma chave: o índice único barrou a segunda.
            db.ChangeTracker.Clear();
            var original = await BuscarPorIdempotencyKey(db, idempotencyKey, ct);
            return Results.Ok(LancamentoResponse.De(original!));
        }

        return Results.Created($"/lancamentos/{lancamento.Id}", LancamentoResponse.De(lancamento));
    }

    private static async Task<IResult> ListarPorData([FromQuery] DateOnly data, LancamentosDbContext db, CancellationToken ct)
    {
        var lancamentos = await db.Lancamentos
            .AsNoTracking()
            .Where(l => l.Data == data)
            .OrderBy(l => l.CriadoEm)
            .ToListAsync(ct);

        return Results.Ok(lancamentos.Select(LancamentoResponse.De));
    }

    private static async Task<IResult> ObterPorId(Guid id, LancamentosDbContext db, CancellationToken ct)
    {
        var lancamento = await db.Lancamentos.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        return lancamento is null ? Results.NotFound() : Results.Ok(LancamentoResponse.De(lancamento));
    }

    private static Task<Lancamento?> BuscarPorIdempotencyKey(LancamentosDbContext db, string idempotencyKey, CancellationToken ct) =>
        db.Lancamentos.AsNoTracking().FirstOrDefaultAsync(l => l.IdempotencyKey == idempotencyKey, ct);
}
