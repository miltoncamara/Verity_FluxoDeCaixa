using System.Net;

namespace Consolidado.Tests.Integracao;

[Collection(ConsolidadoApiCollection.Nome)]
public class SegurancaTests(ConsolidadoApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/consolidado/2026-10-05")]
    [InlineData("/consolidado?inicio=2026-10-01&fim=2026-10-05")]
    public async Task Cliente_sem_permissao_de_leitura_do_consolidado_recebe_403(string url)
    {
        // O PDV só tem permissão para registrar lançamentos.
        var resposta = await factory.CriarClienteAutenticado(ClientesDeTeste.ChavePdv).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Theory]
    [InlineData("/consolidado/2026-10-05")]
    [InlineData("/consolidado?inicio=2026-10-01&fim=2026-10-05")]
    public async Task Cliente_com_permissao_de_leitura_do_consolidado_e_atendido(string url)
    {
        var resposta = await factory.CriarClienteAutenticado(ClientesDeTeste.ChaveBi).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Cliente_acima_do_seu_limite_recebe_429()
    {
        var limitado = factory.CriarClienteAutenticado(ClientesDeTeste.ChaveLimitada);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => limitado.GetAsync("/consolidado/2026-10-05", Ct)));

        Assert.Contains(respostas, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Contains(respostas, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Respostas_trazem_os_headers_de_seguranca()
    {
        var resposta = await factory.CriarClienteAutenticado().GetAsync("/consolidado/2026-10-05", Ct);

        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(resposta.Headers.CacheControl?.NoStore);
    }
}
