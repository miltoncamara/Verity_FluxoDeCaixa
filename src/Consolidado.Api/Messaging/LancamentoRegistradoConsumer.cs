using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Consolidado.Api.Data;
using Consolidado.Api.Domain;
using Consolidado.Api.Observabilidade;
using Contracts;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Consolidado.Api.Messaging;

/// <summary>
/// Consome os eventos LancamentoRegistrado e atualiza o saldo diário.
/// Ack manual somente depois do commit no banco. Se a conexão cair, reconecta do zero.
/// </summary>
public sealed class LancamentoRegistradoConsumer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<LancamentoRegistradoConsumer> logger) : BackgroundService
{
    // Exchange e routing key são os mesmos usados pelo publicador da Lancamentos.Api.
    public const string Exchange = "lancamentos";
    public const string RoutingKey = "lancamento.registrado";
    public const string Fila = "consolidado.lancamentos";
    public const string DeadLetterExchange = "consolidado.dlx";
    public const string FilaDeadLetter = "consolidado.lancamentos.dlq";

    // Uma mensagem que falha com erro inesperado volta para a fila no máximo esse número de vezes.
    // Depois disso o próprio RabbitMQ a move para a dead letter queue.
    public const int LimiteDeEntregas = 5;

    private const ushort Prefetch = 20;
    private static readonly TimeSpan IntervaloAposFalha = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumirAteDesconectarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ indisponível, nova tentativa em {Segundos}s: {Erro}",
                    IntervaloAposFalha.TotalSeconds, ex.Message);
            }

            try { await Task.Delay(IntervaloAposFalha, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConsumirAteDesconectarAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMQ:Host"] ?? "localhost",
            Port = configuration.GetValue("RabbitMQ:Port", 5672),
            UserName = configuration["RabbitMQ:User"] ?? "guest",
            Password = configuration["RabbitMQ:Password"] ?? "guest",
            ClientProvidedName = "consolidado-api-consumer",
            AutomaticRecoveryEnabled = false // a reconexão é feita pelo loop em ExecuteAsync
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await DeclararTopologiaAsync(channel, ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: Prefetch, global: false, ct);

        var desconectou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            desconectou.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, mensagem) => ProcessarMensagemAsync(channel, mensagem, ct);
        await channel.BasicConsumeAsync(Fila, autoAck: false, consumer, ct);

        logger.LogInformation("Consumindo a fila {Fila} em {Host}", Fila, factory.HostName);
        await desconectou.Task.WaitAsync(ct);
        throw new InvalidOperationException("Conexão com o RabbitMQ foi encerrada.");
    }

    private static async Task DeclararTopologiaAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(FilaDeadLetter, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(FilaDeadLetter, DeadLetterExchange, routingKey: "", cancellationToken: ct);

        // Quorum queue: replicada e durável, e conta as entregas de cada mensagem (x-delivery-limit).
        var argumentos = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = LimiteDeEntregas,
            ["x-dead-letter-exchange"] = DeadLetterExchange
        };
        await channel.QueueDeclareAsync(Fila, durable: true, exclusive: false, autoDelete: false, argumentos, cancellationToken: ct);
        await channel.QueueBindAsync(Fila, Exchange, RoutingKey, cancellationToken: ct);
    }

    private async Task ProcessarMensagemAsync(IChannel channel, BasicDeliverEventArgs mensagem, CancellationToken ct)
    {
        // Continua o trace que veio dentro da mensagem: o mesmo do POST que registrou o lançamento.
        using var atividade = Telemetria.Traces.StartActivity(
            "processar LancamentoRegistrado", ActivityKind.Consumer, LerTraceParent(mensagem));
        atividade?.SetTag("messaging.system", "rabbitmq");
        atividade?.SetTag("messaging.destination.name", Fila);
        atividade?.SetTag("messaging.message.id", mensagem.BasicProperties.MessageId);

        try
        {
            var evento = LerEvento(mensagem);
            if (evento is null)
            {
                // Mensagem que nunca vai ser processada com sucesso: vai direto para a dead letter queue.
                Telemetria.MensagensRejeitadas.Add(1, new KeyValuePair<string, object?>("motivo", "invalida"));
                atividade?.SetStatus(ActivityStatusCode.Error, "Mensagem inválida, enviada para a DLQ");
                await channel.BasicRejectAsync(mensagem.DeliveryTag, requeue: false, ct);
                return;
            }

            while (true)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var atualizador = scope.ServiceProvider.GetRequiredService<AtualizadorDeSaldo>();
                    var aplicado = await atualizador.AplicarAsync(evento, ct);

                    // Ack somente depois do commit. Se cair antes disso, a mensagem é entregue de novo
                    // e a tabela eventos_processados impede que ela seja somada duas vezes.
                    await channel.BasicAckAsync(mensagem.DeliveryTag, multiple: false, ct);

                    if (aplicado)
                    {
                        Telemetria.EventosAplicados.Add(1);
                        Telemetria.AtrasoDoEvento.Record((DateTimeOffset.UtcNow - evento.OcorridoEm).TotalSeconds);
                        logger.LogInformation("Evento {EventoId} aplicado ao saldo de {Data}", evento.EventoId, evento.Data);
                    }
                    else
                    {
                        Telemetria.EventosDuplicados.Add(1);
                        logger.LogInformation("Evento {EventoId} já processado, ignorado", evento.EventoId);
                    }
                    return;
                }
                catch (Exception ex) when (EhFalhaTransitoria(ex) && !ct.IsCancellationRequested && channel.IsOpen)
                {
                    // Banco fora do ar não é culpa da mensagem. Ela fica com o consumidor, sem ack,
                    // e é processada de novo quando o banco voltar. Não conta para o limite de entregas.
                    Telemetria.FalhasTransitorias.Add(1);
                    logger.LogWarning("Banco do consolidado indisponível, nova tentativa em {Segundos}s: {Erro}",
                        IntervaloAposFalha.TotalSeconds, ex.Message);
                    await Task.Delay(IntervaloAposFalha, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Aplicação parando. A mensagem sem ack volta para a fila quando o canal fechar.
        }
        catch (Exception ex)
        {
            // Erro inesperado: devolve para a fila. Depois de LimiteDeEntregas tentativas, vai para a DLQ.
            // Usa basic.reject e não basic.nack: desde o RabbitMQ 4.3 a quorum queue só conta para o
            // x-delivery-limit as devoluções feitas com reject. Com nack a mensagem voltaria para sempre.
            Telemetria.MensagensRejeitadas.Add(1, new KeyValuePair<string, object?>("motivo", "erro_inesperado"));
            atividade?.SetStatus(ActivityStatusCode.Error, ex.Message);
            atividade?.AddException(ex);
            logger.LogError(ex, "Erro ao processar a mensagem {MessageId}, devolvendo para a fila", mensagem.BasicProperties.MessageId);
            if (channel.IsOpen)
                await channel.BasicRejectAsync(mensagem.DeliveryTag, requeue: true, CancellationToken.None);
        }
    }

    // O RabbitMQ entrega os headers de texto como bytes.
    private static string? LerTraceParent(BasicDeliverEventArgs mensagem) =>
        mensagem.BasicProperties.Headers?.TryGetValue(Telemetria.HeaderTraceParent, out var valor) == true && valor is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private LancamentoRegistrado? LerEvento(BasicDeliverEventArgs mensagem)
    {
        try
        {
            var evento = JsonSerializer.Deserialize<LancamentoRegistrado>(mensagem.Body.Span, JsonSerializerOptions.Web);
            if (evento is null || evento.EventoId == Guid.Empty)
                throw new JsonException("Evento vazio ou sem EventoId.");

            SaldoDiario.ContribuicaoDe(evento.Tipo, evento.Valor); // valida tipo e valor
            return evento;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            logger.LogError("Mensagem {MessageId} inválida, enviada para a dead letter queue: {Erro}",
                mensagem.BasicProperties.MessageId, ex.Message);
            return null;
        }
    }

    private static bool EhFalhaTransitoria(Exception ex) => ex switch
    {
        RetryLimitExceededException => true, // o EF já tentou de novo várias vezes e o banco continua fora
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        _ => ex.InnerException is not null && EhFalhaTransitoria(ex.InnerException)
    };
}
