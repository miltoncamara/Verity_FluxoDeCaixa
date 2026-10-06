using System.Globalization;
using System.Security.Claims;
using Lancamentos.Api.Data;
using Lancamentos.Api.Domain;
using Lancamentos.Api.Observabilidade;
using Lancamentos.Api.Seguranca;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lancamentos.Api.Endpoints;

public static class LancamentosEndpoints
{
    public static void MapLancamentosEndpoints(this WebApplication app)
    {
        app.MapPost("/lancamentos", RegistrarLancamento).RequireAuthorization(Permissoes.LancamentosEscrita);
        app.MapGet("/lancamentos", ListarPorData).RequireAuthorization(Permissoes.LancamentosLeitura);
        app.MapGet("/lancamentos/{id:guid}", ObterPorId).RequireAuthorization(Permissoes.LancamentosLeitura);
    }

    private static async Task<IResult> RegistrarLancamento(
        NovoLancamentoRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal usuario,
        LancamentosDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // O cliente vem da API Key autenticada, nunca do corpo da requisição.
        var cliente = usuario.Identity!.Name!;

        var (lancamento, erros) = Lancamento.Criar(request.Data, request.Tipo, request.Valor, request.Descricao, cliente, idempotencyKey);
        if (lancamento is null)
            return Results.ValidationProblem(erros.ToDictionary(e => e.Key, e => new[] { e.Value }));

        // Requisição repetida com a mesma chave: devolve o lançamento original sem duplicar.
        if (idempotencyKey is not null)
        {
            var existente = await BuscarPorIdempotencyKey(db, cliente, idempotencyKey, ct);
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
            var original = await BuscarPorIdempotencyKey(db, cliente, idempotencyKey, ct);
            return Results.Ok(LancamentoResponse.De(original!));
        }

        Telemetria.LancamentosRegistrados.Add(1, new KeyValuePair<string, object?>("tipo", lancamento.Tipo.ToString()));
        loggerFactory.CreateLogger(nameof(LancamentosEndpoints)).LogInformation(
            "Lançamento {LancamentoId} de {Data} registrado pelo cliente {Cliente}", lancamento.Id, lancamento.Data, cliente);

        return Results.Created($"/lancamentos/{lancamento.Id}", LancamentoResponse.De(lancamento));
    }

    private static async Task<IResult> ListarPorData([FromQuery] string? data, LancamentosDbContext db, CancellationToken ct)
    {
        // Aceita somente yyyy-MM-dd. Sem isso o ASP.NET aceitaria "07-10-2026" e leria como 10 de julho.
        if (!DateOnly.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["data"] = ["Informe a data no formato yyyy-MM-dd."] });

        var lancamentos = await db.Lancamentos
            .AsNoTracking()
            .Where(l => l.Data == dia)
            .OrderBy(l => l.CriadoEm)
            .ToListAsync(ct);

        return Results.Ok(lancamentos.Select(LancamentoResponse.De));
    }

    private static async Task<IResult> ObterPorId(Guid id, LancamentosDbContext db, CancellationToken ct)
    {
        var lancamento = await db.Lancamentos.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        return lancamento is null ? Results.NotFound() : Results.Ok(LancamentoResponse.De(lancamento));
    }

    private static Task<Lancamento?> BuscarPorIdempotencyKey(LancamentosDbContext db, string cliente, string idempotencyKey, CancellationToken ct) =>
        db.Lancamentos.AsNoTracking().FirstOrDefaultAsync(l => l.CriadoPor == cliente && l.IdempotencyKey == idempotencyKey, ct);
}
