using Consolidado.Api.Data;
using Consolidado.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Consolidado.Api.Endpoints;

public static class ConsolidadoEndpoints
{
    public const string HeaderDadoDesatualizado = "X-Stale-Data";

    // Por quanto tempo uma leitura do banco é servida direto da memória.
    public static readonly TimeSpan TempoDeCache = TimeSpan.FromSeconds(5);

    // Por quanto tempo o último valor conhecido fica guardado para ser usado se o banco cair.
    private static readonly TimeSpan TempoDoUltimoValorConhecido = TimeSpan.FromHours(24);

    // A leitura não espera o banco mais que isso. Sem esse limite, as retentativas do EF Core
    // segurariam a requisição por quase um minuto quando o banco está fora.
    private static readonly TimeSpan TempoMaximoDeLeitura = TimeSpan.FromSeconds(2);

    public static void MapConsolidadoEndpoints(this WebApplication app)
    {
        app.MapGet("/consolidado/{data}", ObterSaldoDoDia);
    }

    private static async Task<IResult> ObterSaldoDoDia(
        DateOnly data,
        ConsolidadoDbContext db,
        IMemoryCache cache,
        HttpResponse response,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var chave = $"saldo:{data:yyyy-MM-dd}";
        cache.TryGetValue(chave, out SaldoEmCache? emCache);

        if (emCache is not null && DateTimeOffset.UtcNow - emCache.LidoEm < TempoDeCache)
            return Results.Ok(ConsolidadoResponse.De(emCache.Saldo));

        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TempoMaximoDeLeitura);

            // Leitura por chave na tabela pré-calculada. Um dia sem lançamentos tem saldo zero.
            var saldo = await db.SaldosDiarios.AsNoTracking().FirstOrDefaultAsync(s => s.Data == data, limite.Token)
                ?? SaldoDiario.Vazio(data);

            cache.Set(chave, new SaldoEmCache(saldo, DateTimeOffset.UtcNow), TempoDoUltimoValorConhecido);
            return Results.Ok(ConsolidadoResponse.De(saldo));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            loggerFactory.CreateLogger(nameof(ConsolidadoEndpoints))
                .LogWarning("Falha ao ler o saldo de {Data} no banco: {Erro}", data, ex.Message);

            if (emCache is null)
            {
                response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Consolidado temporariamente indisponível.");
            }

            // Banco fora, mas existe um valor conhecido: responde com ele e avisa que pode estar desatualizado.
            response.Headers[HeaderDadoDesatualizado] = "true";
            response.Headers.Age = ((int)(DateTimeOffset.UtcNow - emCache.LidoEm).TotalSeconds).ToString();
            return Results.Ok(ConsolidadoResponse.De(emCache.Saldo));
        }
    }
}
