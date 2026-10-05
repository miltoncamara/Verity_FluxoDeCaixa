using System.Net;
using System.Net.Http.Json;

namespace Lancamentos.Tests.Integracao;

[Collection(LancamentosApiCollection.Nome)]
public class ApiKeyTests(LancamentosApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly object CorpoValido = new { data = "2026-10-05", tipo = "Credito", valor = 10m, descricao = "Venda" };

    [Fact]
    public async Task Requisicao_sem_api_key_retorna_401()
    {
        var client = factory.CreateClient();

        var resposta = await client.PostAsJsonAsync("/lancamentos", CorpoValido, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Requisicao_com_api_key_errada_retorna_401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "chave-errada");

        var resposta = await client.GetAsync("/lancamentos?data=2026-10-05", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Requisicao_com_api_key_correta_e_aceita()
    {
        var client = factory.CriarClienteAutenticado();

        var resposta = await client.GetAsync("/lancamentos?data=2026-10-05", Ct);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }
}
