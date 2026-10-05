using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Consolidado.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Tests.Integracao;

[Collection(ConsolidadoBancoForaCollection.Nome)]
public class BancoForaDoArTests(ConsolidadoApiFactory factory)
{
    private readonly HttpClient _client = factory.CriarClienteAutenticado();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Com_o_banco_fora_responde_o_ultimo_valor_conhecido_marcado_como_desatualizado()
    {
        var diaConhecido = new DateOnly(2026, 10, 5);
        var diaDesconhecido = new DateOnly(2026, 10, 6);

        await using (var db = factory.CriarDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO saldo_diario (data, total_creditos, total_debitos, atualizado_em) VALUES ({diaConhecido}, 300, 120, now())", Ct);
        }

        // Primeira leitura com o banco no ar: o valor fica guardado em memória.
        var antes = await _client.GetFromJsonAsync<ConsolidadoResponse>($"/consolidado/{diaConhecido:yyyy-MM-dd}", Ct);
        Assert.Equal(180m, antes!.Saldo);

        await factory.PararBancoAsync();
        await Task.Delay(ConsolidadoEndpoints.TempoDeCache + TimeSpan.FromMilliseconds(500), Ct);

        // Dia já lido: responde 200 com o último valor conhecido, rápido, e avisa que pode estar desatualizado.
        var cronometro = Stopwatch.StartNew();
        var resposta = await _client.GetAsync($"/consolidado/{diaConhecido:yyyy-MM-dd}", Ct);
        cronometro.Stop();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("true", resposta.Headers.GetValues(ConsolidadoEndpoints.HeaderDadoDesatualizado).Single());
        Assert.NotNull(resposta.Headers.Age);
        Assert.Equal(180m, (await resposta.Content.ReadFromJsonAsync<ConsolidadoResponse>(Ct))!.Saldo);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(4), $"Fallback demorou {cronometro.Elapsed}");

        // Dia nunca lido: não há o que responder, então 503 com Retry-After.
        var semCache = await _client.GetAsync($"/consolidado/{diaDesconhecido:yyyy-MM-dd}", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, semCache.StatusCode);
        Assert.NotNull(semCache.Headers.RetryAfter);

        // O health check reflete o próprio banco fora do ar.
        var health = await factory.CreateClient().GetAsync("/health", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }
}
