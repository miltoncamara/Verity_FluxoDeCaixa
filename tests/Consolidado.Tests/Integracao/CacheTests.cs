using System.Net.Http.Json;
using Consolidado.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Tests.Integracao;

[Collection(ConsolidadoApiCollection.Nome)]
public class CacheTests(ConsolidadoApiFactory factory)
{
    private readonly HttpClient _client = factory.CriarClienteAutenticado();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Leitura_e_servida_da_memoria_durante_o_tempo_de_cache_e_depois_relida_do_banco()
    {
        var dia = DateOnly.FromDayNumber(Random.Shared.Next(900_000, 950_000));
        var url = $"/consolidado/{dia:yyyy-MM-dd}";

        var primeira = await _client.GetFromJsonAsync<ConsolidadoResponse>(url, Ct);
        Assert.Equal(0m, primeira!.Saldo);

        // Altera o banco por fora. Dentro do tempo de cache a API ainda responde o valor anterior.
        await using var db = factory.CriarDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO saldo_diario (data, total_creditos, total_debitos, atualizado_em) VALUES ({dia}, 75, 0, now())", Ct);

        var emCache = await _client.GetFromJsonAsync<ConsolidadoResponse>(url, Ct);
        Assert.Equal(0m, emCache!.Saldo);

        await Task.Delay(ConsolidadoEndpoints.TempoDeCache + TimeSpan.FromMilliseconds(500), Ct);

        var atualizada = await _client.GetFromJsonAsync<ConsolidadoResponse>(url, Ct);
        Assert.Equal(75m, atualizada!.Saldo);
    }
}
