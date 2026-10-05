using System.Text;
using System.Text.Json;
using Consolidado.Api.Data;
using Consolidado.Api.Messaging;
using Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Consolidado.Tests.Integracao;

/// <summary>
/// Sobe a Consolidado.Api em memória com PostgreSQL e RabbitMQ reais em containers.
/// O consumidor roda de verdade, como em produção.
/// </summary>
public sealed class ConsolidadoApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithUsername("fluxo").WithPassword("teste").Build();

    private IConnection? _conexaoDoTeste;
    private IChannel? _canalDoTeste;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        _conexaoDoTeste = await new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) }.CreateConnectionAsync();
        _canalDoTeste = await _conexaoDoTeste.CreateChannelAsync();

        // Inicia a aplicação e espera o consumidor declarar a fila antes de liberar os testes.
        _ = Services;
        await Aguardar.AteQue(FilaExisteAsync, "consumidor declarar a fila");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Consolidado", _postgres.GetConnectionString());
        builder.UseSetting("RabbitMQ:Host", _rabbit.Hostname);
        builder.UseSetting("RabbitMQ:Port", _rabbit.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("RabbitMQ:User", "fluxo");
        builder.UseSetting("RabbitMQ:Password", "teste");
    }

    public ConsolidadoDbContext CriarDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<ConsolidadoDbContext>();

    public Task PublicarAsync(LancamentoRegistrado evento) =>
        PublicarAsync(JsonSerializer.Serialize(evento, JsonSerializerOptions.Web), evento.EventoId.ToString());

    public async Task PublicarAsync(string corpo, string messageId)
    {
        var propriedades = new BasicProperties { MessageId = messageId, Persistent = true };
        await _canalDoTeste!.BasicPublishAsync(LancamentoRegistradoConsumer.Exchange, LancamentoRegistradoConsumer.RoutingKey,
            mandatory: false, propriedades, Encoding.UTF8.GetBytes(corpo));
    }

    public async Task<uint> MensagensNaFilaAsync(string fila) => await _canalDoTeste!.MessageCountAsync(fila);

    public async Task<string?> LerDaDeadLetterAsync()
    {
        var resultado = await _canalDoTeste!.BasicGetAsync(LancamentoRegistradoConsumer.FilaDeadLetter, autoAck: true);
        return resultado is null ? null : Encoding.UTF8.GetString(resultado.Body.Span);
    }

    private async Task<bool> FilaExisteAsync()
    {
        // Declaração passiva falha e fecha o canal se a fila não existir, por isso usa um canal descartável.
        try
        {
            await using var canal = await _conexaoDoTeste!.CreateChannelAsync();
            await canal.QueueDeclarePassiveAsync(LancamentoRegistradoConsumer.Fila);
            return true;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return false;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_canalDoTeste is not null) await _canalDoTeste.DisposeAsync();
        if (_conexaoDoTeste is not null) await _conexaoDoTeste.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}
