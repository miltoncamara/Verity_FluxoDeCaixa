using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Lancamentos.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Lancamentos.Tests.Integracao;

[Collection(LancamentosApiCollection.Nome)]
public class LancamentosEndpointsTests(LancamentosApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Cada teste usa uma data própria para não enxergar dados dos outros testes.
    private static DateOnly DataUnica() => DateOnly.FromDayNumber(Random.Shared.Next(700_000, 800_000));

    [Fact]
    public async Task Post_grava_lancamento_e_evento_na_outbox_na_mesma_operacao()
    {
        var data = DataUnica();

        var resposta = await _client.PostAsJsonAsync("/lancamentos",
            new { data, tipo = "Credito", valor = 100.50m, descricao = "Venda à vista" }, Ct);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = await resposta.Content.ReadFromJsonAsync<LancamentoResponse>(Ct);
        Assert.NotNull(criado);
        Assert.Equal($"/lancamentos/{criado.Id}", resposta.Headers.Location?.ToString());

        await using var db = factory.CriarDbContext();
        var lancamento = await db.Lancamentos.SingleAsync(l => l.Id == criado.Id, Ct);
        Assert.Equal(100.50m, lancamento.Valor);

        var eventos = await EventosDoLancamento(db, criado.Id);
        var (mensagem, evento) = Assert.Single(eventos);
        Assert.Null(mensagem.PublicadoEm);
        Assert.Equal(nameof(LancamentoRegistrado), mensagem.Tipo);
        Assert.Equal(mensagem.Id, evento.EventoId);
        Assert.Equal(data, evento.Data);
        Assert.Equal("Credito", evento.Tipo);
        Assert.Equal(100.50m, evento.Valor);
    }

    [Fact]
    public async Task Post_e_confirmado_mesmo_com_rabbitmq_fora_do_ar_e_evento_fica_pendente()
    {
        // Nesta factory o RabbitMQ aponta para uma porta sem nada escutando.
        var resposta = await _client.PostAsJsonAsync("/lancamentos",
            new { data = DataUnica(), tipo = "Debito", valor = 15m, descricao = "Taxa" }, Ct);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = await resposta.Content.ReadFromJsonAsync<LancamentoResponse>(Ct);

        // Dá tempo para o publicador tentar alguns ciclos. O evento deve continuar pendente, sem se perder.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        await using var db = factory.CriarDbContext();
        var (mensagem, _) = Assert.Single(await EventosDoLancamento(db, criado!.Id));
        Assert.Null(mensagem.PublicadoEm);
    }

    [Fact]
    public async Task Post_repetido_com_mesma_idempotency_key_nao_duplica()
    {
        var chave = Guid.NewGuid().ToString();
        var corpo = new { data = DataUnica(), tipo = "Debito", valor = 30m, descricao = "Conta de luz" };

        var primeira = await PostComChave(corpo, chave);
        var segunda = await PostComChave(corpo, chave);

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        var original = await primeira.Content.ReadFromJsonAsync<LancamentoResponse>(Ct);
        var repetido = await segunda.Content.ReadFromJsonAsync<LancamentoResponse>(Ct);
        Assert.Equal(original, repetido);

        await using var db = factory.CriarDbContext();
        Assert.Equal(1, await db.Lancamentos.CountAsync(l => l.IdempotencyKey == chave, Ct));
        Assert.Single(await EventosDoLancamento(db, original!.Id));
    }

    [Fact]
    public async Task Posts_simultaneos_com_mesma_idempotency_key_geram_um_unico_lancamento()
    {
        var chave = Guid.NewGuid().ToString();
        var corpo = new { data = DataUnica(), tipo = "Credito", valor = 10m, descricao = "Venda" };

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostComChave(corpo, chave)));

        Assert.All(respostas, r => Assert.True(r.IsSuccessStatusCode, $"Status inesperado: {r.StatusCode}"));
        var ids = await Task.WhenAll(respostas.Select(async r => (await r.Content.ReadFromJsonAsync<LancamentoResponse>(Ct))!.Id));
        Assert.Single(ids.Distinct());

        await using var db = factory.CriarDbContext();
        Assert.Equal(1, await db.Lancamentos.CountAsync(l => l.IdempotencyKey == chave, Ct));
        Assert.Single(await EventosDoLancamento(db, ids[0]));
    }

    [Fact]
    public async Task Post_invalido_retorna_400_e_nao_grava_nada()
    {
        await using var db = factory.CriarDbContext();
        var lancamentosAntes = await db.Lancamentos.CountAsync(Ct);
        var eventosAntes = await db.Outbox.CountAsync(Ct);

        var resposta = await _client.PostAsJsonAsync("/lancamentos",
            new { data = DataUnica(), tipo = "Credito", valor = -5m, descricao = "" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var erros = problema.GetProperty("errors");
        Assert.True(erros.TryGetProperty("valor", out _));
        Assert.True(erros.TryGetProperty("descricao", out _));

        Assert.Equal(lancamentosAntes, await db.Lancamentos.CountAsync(Ct));
        Assert.Equal(eventosAntes, await db.Outbox.CountAsync(Ct));
    }

    [Fact]
    public async Task Get_por_data_retorna_somente_lancamentos_do_dia()
    {
        var dia = DataUnica();
        var outroDia = dia.AddDays(1);
        await _client.PostAsJsonAsync("/lancamentos", new { data = dia, tipo = "Credito", valor = 50m, descricao = "A" }, Ct);
        await _client.PostAsJsonAsync("/lancamentos", new { data = dia, tipo = "Debito", valor = 20m, descricao = "B" }, Ct);
        await _client.PostAsJsonAsync("/lancamentos", new { data = outroDia, tipo = "Credito", valor = 99m, descricao = "C" }, Ct);

        var lancamentos = await _client.GetFromJsonAsync<List<LancamentoResponse>>($"/lancamentos?data={dia:yyyy-MM-dd}", Ct);

        Assert.NotNull(lancamentos);
        Assert.Equal(["A", "B"], lancamentos.Select(l => l.Descricao));
        Assert.All(lancamentos, l => Assert.Equal(dia, l.Data));
    }

    [Fact]
    public async Task Get_por_id_inexistente_retorna_404()
    {
        var resposta = await _client.GetAsync($"/lancamentos/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    private Task<HttpResponseMessage> PostComChave(object corpo, string chave)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/lancamentos") { Content = JsonContent.Create(corpo) };
        request.Headers.Add("Idempotency-Key", chave);
        return _client.SendAsync(request, Ct);
    }

    private static async Task<List<(Lancamentos.Api.Data.OutboxMessage Mensagem, LancamentoRegistrado Evento)>> EventosDoLancamento(
        Lancamentos.Api.Data.LancamentosDbContext db, Guid lancamentoId)
    {
        var mensagens = await db.Outbox.AsNoTracking().ToListAsync(Ct);
        return mensagens
            .Select(m => (m, JsonSerializer.Deserialize<LancamentoRegistrado>(m.Payload, JsonSerializerOptions.Web)!))
            .Where(x => x.Item2.LancamentoId == lancamentoId)
            .ToList();
    }
}
