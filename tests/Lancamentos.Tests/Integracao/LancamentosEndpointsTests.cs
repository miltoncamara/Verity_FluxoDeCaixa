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
    private readonly HttpClient _client = factory.CriarClienteAutenticado();
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
    public async Task Post_guarda_o_contexto_do_trace_da_requisicao_junto_com_o_evento()
    {
        // O cliente chega com um trace já iniciado, como faria um sistema instrumentado com OpenTelemetry.
        var traceId = System.Diagnostics.ActivityTraceId.CreateRandom().ToHexString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/lancamentos")
        {
            Content = JsonContent.Create(new { data = DataUnica(), tipo = "Credito", valor = 5m, descricao = "Com trace" })
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{System.Diagnostics.ActivitySpanId.CreateRandom().ToHexString()}-01");

        var resposta = await _client.SendAsync(request, Ct);

        var criado = await resposta.Content.ReadFromJsonAsync<LancamentoResponse>(Ct);
        await using var db = factory.CriarDbContext();
        var (mensagem, _) = Assert.Single(await EventosDoLancamento(db, criado!.Id));
        Assert.NotNull(mensagem.TraceParent);
        Assert.Contains(traceId, mensagem.TraceParent);
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
    public async Task Post_sem_idempotency_key_retorna_400()
    {
        // Cliente sem a geração automática de chave usada nos outros testes.
        var semChave = factory.CreateClient();
        semChave.DefaultRequestHeaders.Add("X-Api-Key", ClientesDeTeste.ChaveCompleta);

        var resposta = await semChave.PostAsJsonAsync("/lancamentos",
            new { data = DataUnica(), tipo = "Credito", valor = 5m, descricao = "Café" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(problema.GetProperty("errors").TryGetProperty("idempotencyKey", out _));
    }

    [Fact]
    public async Task Mesma_idempotency_key_com_conteudo_diferente_retorna_422_e_nao_grava()
    {
        var chave = Guid.NewGuid().ToString();
        var dia = DataUnica();

        var original = await PostComChave(new { data = dia, tipo = "Credito", valor = 5m, descricao = "Café" }, chave);
        var reaproveitada = await PostComChave(new { data = dia, tipo = "Credito", valor = 7m, descricao = "Café" }, chave);

        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reaproveitada.StatusCode);
        await using var db = factory.CriarDbContext();
        var lancamento = await db.Lancamentos.SingleAsync(l => l.IdempotencyKey == chave, Ct);
        Assert.Equal(5m, lancamento.Valor);
    }

    [Fact]
    public async Task Duas_vendas_iguais_com_chaves_diferentes_sao_dois_lancamentos()
    {
        // O motivo de a chave vir do cliente: duas vendas idênticas no mesmo dia são legítimas.
        var dia = DataUnica();
        var venda = new { data = dia, tipo = "Credito", valor = 5m, descricao = "Café" };

        var primeira = await PostComChave(venda, "cupom-001");
        var segunda = await PostComChave(venda, "cupom-002");

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        var lancamentos = await _client.GetFromJsonAsync<List<LancamentoResponse>>($"/lancamentos?data={dia:yyyy-MM-dd}", Ct);
        Assert.Equal(2, lancamentos!.Count);
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

    [Theory]
    [InlineData("""{"data":"07/10/2026","tipo":"Credito","valor":10,"descricao":"Data no formato errado"}""")]
    [InlineData("""{"data":"2026-10-07","tipo":"Credito","valor":"dez","descricao":"Valor como texto"}""")]
    [InlineData("""{"data":"2026-10-07",""")]
    public async Task Corpo_que_nao_pode_ser_lido_retorna_400(string corpo)
    {
        var resposta = await _client.PostAsync("/lancamentos",
            new StringContent(corpo, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Get_sem_data_ou_com_data_invalida_retorna_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/lancamentos", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/lancamentos?data=07-10-2026", Ct)).StatusCode);
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
