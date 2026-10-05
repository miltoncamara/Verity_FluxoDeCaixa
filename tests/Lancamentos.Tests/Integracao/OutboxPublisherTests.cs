using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Lancamentos.Api.Endpoints;
using Lancamentos.Api.Messaging;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Lancamentos.Tests.Integracao;

[Collection(LancamentosComRabbitMqCollection.Nome)]
public class OutboxPublisherTests(LancamentosComRabbitMqFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CriarClienteAutenticado();
    private IConnection _conexao = null!;
    private IChannel _canal = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly DataUnica() => DateOnly.FromDayNumber(Random.Shared.Next(700_000, 800_000));

    public async ValueTask InitializeAsync()
    {
        _conexao = await new ConnectionFactory { Uri = new Uri(factory.RabbitMqConnectionString) }.CreateConnectionAsync(Ct);
        _canal = await _conexao.CreateChannelAsync(cancellationToken: Ct);
        await _canal.ExchangeDeclareAsync(OutboxPublisher.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: Ct);
    }

    [Fact]
    public async Task Evento_e_publicado_como_mensagem_persistente_e_marcado_como_publicado()
    {
        var fila = await CriarFilaLigadaAoExchangeAsync();

        var criado = await PostAsync(new { data = DataUnica(), tipo = "Credito", valor = 42.10m, descricao = "Venda" });

        var mensagens = await LerMensagensAsync(fila, quantidade: 1);
        var (propriedades, evento) = Assert.Single(mensagens);
        Assert.Equal(criado.Id, evento.LancamentoId);
        Assert.Equal(42.10m, evento.Valor);
        Assert.Equal("Credito", evento.Tipo);
        Assert.True(propriedades.Persistent);
        Assert.Equal(evento.EventoId.ToString(), propriedades.MessageId);

        await Aguardar.AteQue(async () =>
        {
            await using var db = factory.CriarDbContext();
            var outbox = await db.Outbox.AsNoTracking().SingleAsync(o => o.Id == evento.EventoId, Ct);
            return outbox.PublicadoEm is not null;
        }, "outbox marcar o evento como publicado");
    }

    [Fact]
    public async Task Eventos_sao_publicados_na_ordem_em_que_foram_criados()
    {
        var fila = await CriarFilaLigadaAoExchangeAsync();
        var dia = DataUnica();

        var idsCriados = new List<Guid>();
        for (var i = 1; i <= 5; i++)
            idsCriados.Add((await PostAsync(new { data = dia, tipo = "Debito", valor = (decimal)i, descricao = $"Item {i}" })).Id);

        var mensagens = await LerMensagensAsync(fila, quantidade: 5);
        Assert.Equal(idsCriados, mensagens.Select(m => m.Evento.LancamentoId));
    }

    [Fact]
    public async Task Sem_fila_para_receber_o_evento_continua_pendente_e_e_publicado_depois()
    {
        // Ninguém ligado ao exchange: o broker devolve a mensagem (mandatory) e o publicador não a marca como publicada.
        var criado = await PostAsync(new { data = DataUnica(), tipo = "Credito", valor = 9m, descricao = "Sem consumidor" });
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Null(await PublicadoEmAsync(criado.Id));

        // Quando uma fila passa a existir, o próximo ciclo publica o evento pendente.
        var fila = await CriarFilaLigadaAoExchangeAsync();
        var mensagens = await LerMensagensAsync(fila, quantidade: 1, lancamentoId: criado.Id, limite: TimeSpan.FromSeconds(20));
        Assert.Single(mensagens);
        await Aguardar.AteQue(async () => await PublicadoEmAsync(criado.Id) is not null, "outbox marcar o evento como publicado");
    }

    private async Task<LancamentoResponse> PostAsync(object corpo)
    {
        var resposta = await _client.PostAsJsonAsync("/lancamentos", corpo, Ct);
        resposta.EnsureSuccessStatusCode();
        return (await resposta.Content.ReadFromJsonAsync<LancamentoResponse>(Ct))!;
    }

    private async Task<string> CriarFilaLigadaAoExchangeAsync()
    {
        // Fila exclusiva: some quando a conexão do teste fecha.
        var fila = await _canal.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true, cancellationToken: Ct);
        await _canal.QueueBindAsync(fila.QueueName, OutboxPublisher.Exchange, OutboxPublisher.RoutingKey, cancellationToken: Ct);
        return fila.QueueName;
    }

    private async Task<List<(IReadOnlyBasicProperties Propriedades, LancamentoRegistrado Evento)>> LerMensagensAsync(
        string fila, int quantidade, Guid? lancamentoId = null, TimeSpan? limite = null)
    {
        var mensagens = new List<(IReadOnlyBasicProperties, LancamentoRegistrado)>();
        await Aguardar.AteQue(async () =>
        {
            var resultado = await _canal.BasicGetAsync(fila, autoAck: true, Ct);
            if (resultado is not null)
            {
                var evento = JsonSerializer.Deserialize<LancamentoRegistrado>(resultado.Body.Span, JsonSerializerOptions.Web)!;
                if (lancamentoId is null || evento.LancamentoId == lancamentoId)
                    mensagens.Add((resultado.BasicProperties, evento));
            }
            return mensagens.Count >= quantidade;
        }, $"{quantidade} mensagem(ns) na fila", limite);
        return mensagens;
    }

    private async Task<DateTimeOffset?> PublicadoEmAsync(Guid lancamentoId)
    {
        await using var db = factory.CriarDbContext();
        var mensagens = await db.Outbox.AsNoTracking().ToListAsync(Ct);
        return mensagens
            .Single(m => JsonSerializer.Deserialize<LancamentoRegistrado>(m.Payload, JsonSerializerOptions.Web)!.LancamentoId == lancamentoId)
            .PublicadoEm;
    }

    public async ValueTask DisposeAsync()
    {
        await _canal.DisposeAsync();
        await _conexao.DisposeAsync();
    }
}
