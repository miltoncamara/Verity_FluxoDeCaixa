using System.Globalization;
using Consolidado.Api.Data;
using Consolidado.Api.Domain;
using Consolidado.Api.Observabilidade;
using Consolidado.Api.Seguranca;
using Microsoft.AspNetCore.Mvc;
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
        app.MapGet("/consolidado/{data}", ObterSaldoDoDia).RequireAuthorization(Permissoes.ConsolidadoLeitura);
        app.MapGet("/consolidado", ObterRelatorioDoPeriodo).RequireAuthorization(Permissoes.ConsolidadoLeitura);
    }

    private static async Task<IResult> ObterSaldoDoDia(
        string data,
        ConsolidadoDbContext db,
        IMemoryCache cache,
        HttpResponse response,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TentarLerData(data, out var dia))
            return ErroDeData("data");

        return await LerComCacheAsync($"saldo:{dia:yyyy-MM-dd}", async limite =>
        {
            // Leitura por chave na tabela pré-calculada. Um dia sem lançamentos tem saldo zero.
            var saldo = await db.SaldosDiarios.AsNoTracking().FirstOrDefaultAsync(s => s.Data == dia, limite)
                ?? SaldoDiario.Vazio(dia);
            return ConsolidadoResponse.De(saldo);
        }, cache, response, loggerFactory, ct);
    }

    private static async Task<IResult> ObterRelatorioDoPeriodo(
        [FromQuery(Name = "inicio")] string? textoInicio,
        [FromQuery(Name = "fim")] string? textoFim,
        ConsolidadoDbContext db,
        IMemoryCache cache,
        HttpResponse response,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TentarLerData(textoInicio, out var inicio))
            return ErroDeData("inicio");
        if (!TentarLerData(textoFim, out var fim))
            return ErroDeData("fim");
        if (RelatorioDoPeriodo.ValidarPeriodo(inicio, fim) is { } erro)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["periodo"] = [erro] });

        return await LerComCacheAsync($"relatorio:{inicio:yyyy-MM-dd}:{fim:yyyy-MM-dd}", async limite =>
        {
            // As duas consultas leem a tabela saldo_diario, que tem uma linha por dia.
            // Nunca é feita soma sobre lançamentos.
            var saldoInicial = await db.SaldosDiarios
                .Where(s => s.Data < inicio)
                .SumAsync(s => s.TotalCreditos - s.TotalDebitos, limite);

            var saldosDoPeriodo = await db.SaldosDiarios.AsNoTracking()
                .Where(s => s.Data >= inicio && s.Data <= fim)
                .ToListAsync(limite);

            return RelatorioDoPeriodo.Montar(inicio, fim, saldoInicial, saldosDoPeriodo);
        }, cache, response, loggerFactory, ct);
    }

    private static void RegistrarLeitura(string origem) =>
        Telemetria.Leituras.Add(1, new KeyValuePair<string, object?>("origem", origem));

    // Aceita somente yyyy-MM-dd. Sem isso o ASP.NET aceitaria "07-10-2026" e leria como 10 de julho.
    private static bool TentarLerData(string? texto, out DateOnly data) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out data);

    private static IResult ErroDeData(string campo) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [campo] = ["Informe a data no formato yyyy-MM-dd."] });

    /// <summary>
    /// Serve a leitura da memória por até TempoDeCache. Depois disso relê do banco, com limite de tempo.
    /// Se o banco falhar, responde o último valor conhecido marcado como desatualizado, ou 503 se não houver nenhum.
    /// </summary>
    private static async Task<IResult> LerComCacheAsync<T>(
        string chave,
        Func<CancellationToken, Task<T>> lerDoBanco,
        IMemoryCache cache,
        HttpResponse response,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        cache.TryGetValue(chave, out LeituraEmCache<T>? emCache);

        if (emCache is not null && DateTimeOffset.UtcNow - emCache.LidoEm < TempoDeCache)
        {
            RegistrarLeitura("cache");
            return Results.Ok(emCache.Valor);
        }

        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TempoMaximoDeLeitura);

            var valor = await lerDoBanco(limite.Token);

            cache.Set(chave, new LeituraEmCache<T>(valor, DateTimeOffset.UtcNow), TempoDoUltimoValorConhecido);
            RegistrarLeitura("banco");
            return Results.Ok(valor);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            loggerFactory.CreateLogger(nameof(ConsolidadoEndpoints))
                .LogWarning("Falha ao ler {Chave} no banco: {Erro}", chave, ex.Message);

            if (emCache is null)
            {
                RegistrarLeitura("indisponivel");
                response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Consolidado temporariamente indisponível.");
            }

            // Banco fora, mas existe um valor conhecido: responde com ele e avisa que pode estar desatualizado.
            RegistrarLeitura("ultimo_valor_conhecido");
            response.Headers[HeaderDadoDesatualizado] = "true";
            response.Headers.Age = ((int)(DateTimeOffset.UtcNow - emCache.LidoEm).TotalSeconds).ToString();
            return Results.Ok(emCache.Valor);
        }
    }
}
