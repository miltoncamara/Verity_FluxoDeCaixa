using System.Globalization;
using System.Security.Claims;
using Lancamentos.Api.Data;
using Lancamentos.Api.Domain;
using Lancamentos.Api.Observabilidade;
using Lancamentos.Api.Seguranca;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    // O POST não espera o banco mais que isso. Sem esse limite, as retentativas do EF Core segurariam a
    // requisição por cerca de 1 minuto com o banco fora, e os pedidos se acumulariam na API.
    public static readonly TimeSpan TempoMaximoDeGravacao = TimeSpan.FromSeconds(5);

    private static async Task<IResult> RegistrarLancamento(
        NovoLancamentoRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal usuario,
        LancamentosDbContext db,
        HttpResponse response,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(LancamentosEndpoints));

        // O cliente vem da API Key autenticada, nunca do corpo da requisição.
        var cliente = usuario.Identity!.Name!;

        var (lancamento, erros) = Lancamento.Criar(request.Data, request.Tipo, request.Valor, request.Descricao, cliente, idempotencyKey);
        if (lancamento is null)
            return Results.ValidationProblem(erros.ToDictionary(e => e.Key, e => new[] { e.Value }));

        var chave = lancamento.IdempotencyKey!;

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TempoMaximoDeGravacao);

        try
        {
            // Pedido com uma chave já usada: é uma repetição, ou a chave foi reaproveitada por engano.
            var existente = await BuscarPorIdempotencyKey(db, cliente, chave, limite.Token);
            if (existente is not null)
                return ResponderRepeticao(existente, lancamento);

            // Invariante central: o lançamento e o seu evento são gravados no mesmo SaveChanges,
            // que o EF Core executa numa única transação. Ou os dois existem, ou nenhum existe.
            db.Lancamentos.Add(lancamento);
            db.Outbox.Add(OutboxMessage.LancamentoRegistrado(lancamento));

            try
            {
                await db.SaveChangesAsync(limite.Token);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Dois pedidos simultâneos com a mesma chave: o índice único barrou o segundo.
                db.ChangeTracker.Clear();
                var original = await BuscarPorIdempotencyKey(db, cliente, chave, limite.Token);
                return ResponderRepeticao(original!, lancamento);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && EhIndisponibilidadeDoBanco(ex))
        {
            // Banco fora ou lento demais: falha rápido e diz ao cliente que pode repetir com segurança.
            // Se o commit chegou a acontecer antes do limite, a repetição com a mesma chave devolve o
            // lançamento original. É por isso que a Idempotency-Key é obrigatória.
            logger.LogWarning("Banco de lançamentos indisponível ao registrar o pedido {IdempotencyKey} do cliente {Cliente}: {Erro}",
                chave, cliente, ex.Message);
            response.Headers.RetryAfter = "5";
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Não foi possível registrar o lançamento agora.",
                detail: "Repita o pedido com a mesma Idempotency-Key. Se ele chegou a ser gravado, o lançamento original será devolvido, sem duplicar.");
        }

        Telemetria.LancamentosRegistrados.Add(1, new KeyValuePair<string, object?>("tipo", lancamento.Tipo.ToString()));
        logger.LogInformation("Lançamento {LancamentoId} de {Data} registrado pelo cliente {Cliente}",
            lancamento.Id, lancamento.Data.ToString("yyyy-MM-dd"), cliente);

        return Results.Created($"/lancamentos/{lancamento.Id}", LancamentoResponse.De(lancamento));
    }

    // Mesma chave e mesmo conteúdo: repetição legítima, devolve o original.
    // Mesma chave e conteúdo diferente: o cliente reaproveitou a chave por engano, e isso não pode passar em silêncio.
    private static IResult ResponderRepeticao(Lancamento original, Lancamento pedido) =>
        original.TemOMesmoConteudoQue(pedido)
            ? Results.Ok(LancamentoResponse.De(original))
            : Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency-Key já usada em um lançamento com outro conteúdo.",
                detail: $"A chave identifica o lançamento {original.Id}. Para registrar um lançamento novo, use uma chave nova.");

    private static bool EhIndisponibilidadeDoBanco(Exception ex) => ex switch
    {
        OperationCanceledException => true, // o limite de tempo da gravação estourou
        RetryLimitExceededException => true, // o EF Core já tentou de novo várias vezes
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        _ => ex.InnerException is not null && EhIndisponibilidadeDoBanco(ex.InnerException)
    };

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
