using System.Net;
using System.Net.Http.Json;
using Lancamentos.Api.Endpoints;
using Lancamentos.Api.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace Lancamentos.Tests.Integracao;

[Collection(LancamentosApiCollection.Nome)]
public class SegurancaTests(LancamentosApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NovoLancamento(string descricao = "Venda") =>
        new { data = "2026-10-05", tipo = "Credito", valor = 10m, descricao };

    [Fact]
    public async Task Cliente_so_com_permissao_de_escrita_registra_mas_nao_consulta()
    {
        var pdv = factory.CriarClienteAutenticado(ClientesDeTeste.ChavePdv);

        var post = await pdv.PostAsJsonAsync("/lancamentos", NovoLancamento(), Ct);
        var get = await pdv.GetAsync("/lancamentos?data=2026-10-05", Ct);

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task Cliente_so_com_permissao_de_leitura_consulta_mas_nao_registra()
    {
        var bi = factory.CriarClienteAutenticado(ClientesDeTeste.ChaveBi);

        var get = await bi.GetAsync("/lancamentos?data=2026-10-05", Ct);
        var post = await bi.PostAsJsonAsync("/lancamentos", NovoLancamento(), Ct);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Lancamento_guarda_qual_cliente_o_registrou()
    {
        var pdv = factory.CriarClienteAutenticado(ClientesDeTeste.ChavePdv);

        var resposta = await pdv.PostAsJsonAsync("/lancamentos", NovoLancamento(), Ct);

        var criado = await resposta.Content.ReadFromJsonAsync<LancamentoResponse>(Ct);
        Assert.Equal("pdv", criado!.CriadoPor);
        await using var db = factory.CriarDbContext();
        Assert.Equal("pdv", (await db.Lancamentos.SingleAsync(l => l.Id == criado.Id, Ct)).CriadoPor);
    }

    [Fact]
    public async Task Mesma_idempotency_key_em_clientes_diferentes_gera_lancamentos_diferentes()
    {
        var chave = Guid.NewGuid().ToString();

        var doPdv = await PostComChave(factory.CriarClienteAutenticado(ClientesDeTeste.ChavePdv), chave);
        var doTeste = await PostComChave(factory.CriarClienteAutenticado(), chave);

        // Cada cliente tem o seu espaço de chaves: um nunca recebe o lançamento do outro.
        Assert.Equal(HttpStatusCode.Created, doPdv.StatusCode);
        Assert.Equal(HttpStatusCode.Created, doTeste.StatusCode);
        var idPdv = (await doPdv.Content.ReadFromJsonAsync<LancamentoResponse>(Ct))!.Id;
        var idTeste = (await doTeste.Content.ReadFromJsonAsync<LancamentoResponse>(Ct))!.Id;
        Assert.NotEqual(idPdv, idTeste);
    }

    [Fact]
    public async Task Cliente_acima_do_seu_limite_recebe_429_com_retry_after()
    {
        var limitado = factory.CriarClienteAutenticado(ClientesDeTeste.ChaveLimitada);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => limitado.GetAsync("/lancamentos?data=2026-10-05", Ct)));

        var rejeitadas = respostas.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        Assert.NotEmpty(rejeitadas);
        Assert.True(respostas.Count(r => r.IsSuccessStatusCode) <= ClientesDeTeste.LimiteDoClienteLimitado * 2);
        Assert.All(rejeitadas, r => Assert.NotNull(r.Headers.RetryAfter));

        // O limite é por cliente: os outros clientes continuam sendo atendidos normalmente.
        var outro = await factory.CriarClienteAutenticado().GetAsync("/lancamentos?data=2026-10-05", Ct);
        Assert.Equal(HttpStatusCode.OK, outro.StatusCode);
    }

    [Fact]
    public async Task Tentativas_com_chave_invalida_sao_limitadas()
    {
        var atacante = factory.CriarClienteAutenticado("chave-chutada");

        var respostas = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => atacante.GetAsync("/lancamentos?data=2026-10-05", Ct)));

        Assert.Contains(respostas, r => r.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Contains(respostas, r => r.StatusCode == HttpStatusCode.TooManyRequests);

        // Clientes legítimos não são afetados pelo limite de quem não tem chave válida.
        var legitimo = await factory.CriarClienteAutenticado().GetAsync("/lancamentos?data=2026-10-05", Ct);
        Assert.Equal(HttpStatusCode.OK, legitimo.StatusCode);

        // Espera a janela do limite passar para não atrapalhar os próximos testes da collection.
        await Task.Delay(TimeSpan.FromSeconds(1.1), Ct);
    }

    [Fact]
    public async Task Corpo_acima_do_limite_retorna_413()
    {
        var descricaoGigante = new string('x', (int)SegurancaExtensions.TamanhoMaximoDoCorpo);

        // StringContent declara o Content-Length, e a API recusa antes de ler o corpo. Corpos sem tamanho
        // declarado são barrados pelo limite do Kestrel e do nginx, que não existem no servidor de teste.
        var corpo = new StringContent(System.Text.Json.JsonSerializer.Serialize(NovoLancamento(descricaoGigante)),
            System.Text.Encoding.UTF8, "application/json");
        var resposta = await factory.CriarClienteAutenticado().PostAsync("/lancamentos", corpo, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resposta.StatusCode);
    }

    [Fact]
    public async Task Respostas_trazem_os_headers_de_seguranca()
    {
        var resposta = await factory.CriarClienteAutenticado().GetAsync("/lancamentos?data=2026-10-05", Ct);

        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resposta.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", resposta.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(resposta.Headers.CacheControl?.NoStore);
    }

    private Task<HttpResponseMessage> PostComChave(HttpClient client, string chave)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/lancamentos") { Content = JsonContent.Create(NovoLancamento()) };
        request.Headers.Add("Idempotency-Key", chave);
        return client.SendAsync(request, Ct);
    }
}
