using System.Net;

namespace Consolidado.Tests.Integracao;

[Collection(ConsolidadoApiCollection.Nome)]
public class ApiKeyTests(ConsolidadoApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requisicao_sem_api_key_retorna_401()
    {
        var resposta = await factory.CreateClient().GetAsync("/consolidado/2026-10-05", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Requisicao_com_api_key_errada_retorna_401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "chave-errada");

        var resposta = await client.GetAsync("/consolidado/2026-10-05", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Health_nao_exige_api_key_e_fica_saudavel_com_o_banco_no_ar()
    {
        var resposta = await factory.CreateClient().GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }
}
