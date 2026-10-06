using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Lancamentos.Api.Endpoints;

namespace Lancamentos.Tests.Integracao;

[Collection(LancamentosBancoForaCollection.Nome)]
public class BancoDeLancamentosForaTests(LancamentosApiFactory factory)
{
    private readonly HttpClient _client = factory.CriarClienteAutenticado();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Com_o_banco_fora_o_post_falha_rapido_com_503_e_retry_after()
    {
        var corpo = new { data = "2026-10-06", tipo = "Credito", valor = 42m, descricao = "Banco fora" };
        var antes = await _client.PostAsJsonAsync("/lancamentos", corpo, Ct);
        Assert.Equal(HttpStatusCode.Created, antes.StatusCode);

        await factory.PararBancoAsync();

        var cronometro = Stopwatch.StartNew();
        var resposta = await _client.PostAsJsonAsync("/lancamentos", corpo, Ct);
        cronometro.Stop();

        // Sem o limite de tempo, as retentativas do EF Core segurariam o pedido por cerca de 1 minuto.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.NotNull(resposta.Headers.RetryAfter);
        Assert.True(cronometro.Elapsed < LancamentosEndpoints.TempoMaximoDeGravacao + TimeSpan.FromSeconds(3),
            $"O POST demorou {cronometro.Elapsed} para falhar");

        var health = await factory.CreateClient().GetAsync("/health", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }
}
