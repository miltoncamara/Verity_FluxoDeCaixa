using System.Text;
using System.Diagnostics;
using Lancamentos.Api.Data;
using Lancamentos.Api.Observabilidade;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;

namespace Lancamentos.Api.Messaging;

/// <summary>
/// Lê a outbox em loop e publica os eventos pendentes no RabbitMQ, em ordem.
/// Com várias réplicas, um advisory lock do PostgreSQL garante que só uma publica por vez.
/// Se o RabbitMQ estiver fora, apenas loga e tenta de novo no próximo ciclo.
/// A API continua aceitando lançamentos normalmente, porque eles só dependem do banco.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    // Os mesmos nomes são declarados pelo consumidor na Consolidado.Api.
    public const string Exchange = "lancamentos";
    public const string RoutingKey = "lancamento.registrado";

    // Identificador do advisory lock do PostgreSQL que elege qual réplica publica (qualquer número fixo serve).
    private const long ChaveDoLock = 7_001;

    private const int TamanhoDoLote = 100;
    private static readonly TimeSpan IntervaloEntreCiclos = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IntervaloAposFalha = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IntervaloDaLimpeza = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetencaoDosPublicados = TimeSpan.FromDays(7);

    private IConnection? _connection;
    private IChannel? _channel;
    private DateTimeOffset _ultimaLimpeza = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = IntervaloEntreCiclos;
            try
            {
                await PublicarPendentesAsync(stoppingToken);
                await LimparPublicadosAntigosAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // RabbitMQ ou banco indisponível. Os eventos continuam pendentes na outbox.
                Telemetria.FalhasDePublicacao.Add(1);
                logger.LogWarning("Falha ao publicar a outbox, nova tentativa em {Segundos}s: {Erro}",
                    IntervaloAposFalha.TotalSeconds, ex.Message);
                await FecharConexaoAsync();
                espera = IntervaloAposFalha;
            }

            try { await Task.Delay(espera, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        await FecharConexaoAsync();
    }

    private async Task PublicarPendentesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LancamentosDbContext>();

        await AtualizarMetricasDaOutboxAsync(db, ct);

        // Com várias réplicas da API, cada uma roda o seu publicador. Só a que conseguir o lock publica
        // neste ciclo. As outras ficam de reserva e assumem se a ativa cair. Isso evita publicar o mesmo
        // evento duas vezes.
        // O lock fica preso a uma transação numa conexão própria, então é liberado sozinho no fim do
        // método ou se o processo morrer. As marcações de publicado continuam sendo gravadas uma a uma.
        await using var conexaoDoLock = new NpgsqlConnection(configuration.GetConnectionString("Lancamentos"));
        await conexaoDoLock.OpenAsync(ct);
        await using var transacaoDoLock = await conexaoDoLock.BeginTransactionAsync(ct);
        await using var tentarLock = new NpgsqlCommand($"SELECT pg_try_advisory_xact_lock({ChaveDoLock})", conexaoDoLock, transacaoDoLock);
        if (await tentarLock.ExecuteScalarAsync(ct) is not true)
            return;

        var pendentes = await db.Outbox
            .Where(o => o.PublicadoEm == null)
            .OrderBy(o => o.CriadoEm).ThenBy(o => o.Id)
            .Take(TamanhoDoLote)
            .ToListAsync(ct);

        if (pendentes.Count == 0)
            return;

        var channel = await ObterCanalAsync(ct);

        foreach (var mensagem in pendentes)
        {
            // Continua o trace da requisição que gerou o evento, mesmo que ela tenha acontecido há tempo.
            using var atividade = Telemetria.Traces.StartActivity(
                $"publicar {mensagem.Tipo}", ActivityKind.Producer, mensagem.TraceParent);
            atividade?.SetTag("messaging.system", "rabbitmq");
            atividade?.SetTag("messaging.destination.name", Exchange);
            atividade?.SetTag("messaging.message.id", mensagem.Id.ToString());

            var propriedades = new BasicProperties
            {
                MessageId = mensagem.Id.ToString(),
                Type = mensagem.Tipo,
                ContentType = "application/json",
                Persistent = true, // a mensagem sobrevive a um restart do RabbitMQ
                // Leva o contexto do trace até o consumidor, no header padrão W3C.
                Headers = new Dictionary<string, object?> { [Telemetria.HeaderTraceParent] = atividade?.Id ?? mensagem.TraceParent }
            };

            // Com publisher confirms, este await só termina quando o broker confirma que guardou a mensagem.
            // mandatory: true faz o broker devolver a mensagem se nenhuma fila estiver ligada ao exchange.
            // Nos dois casos de falha é lançada exceção e o evento continua pendente para o próximo ciclo.
            await channel.BasicPublishAsync(Exchange, RoutingKey, mandatory: true, propriedades,
                Encoding.UTF8.GetBytes(mensagem.Payload), ct);

            // Se a aplicação cair entre o publish e esta linha, o evento será publicado de novo.
            // Isso é aceitável porque o consumidor é idempotente (entrega at-least-once).
            mensagem.PublicadoEm = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            Telemetria.EventosPublicados.Add(1);
        }

        logger.LogInformation("{Quantidade} evento(s) publicado(s) da outbox", pendentes.Count);
    }

    private static async Task AtualizarMetricasDaOutboxAsync(LancamentosDbContext db, CancellationToken ct)
    {
        var pendentes = db.Outbox.Where(o => o.PublicadoEm == null);
        var quantidade = await pendentes.LongCountAsync(ct);
        var maisAntigo = await pendentes.OrderBy(o => o.CriadoEm).Select(o => (DateTimeOffset?)o.CriadoEm).FirstOrDefaultAsync(ct);
        var idade = maisAntigo is null ? 0 : (DateTimeOffset.UtcNow - maisAntigo.Value).TotalSeconds;
        Telemetria.AtualizarOutbox(quantidade, idade);
    }

    private async Task LimparPublicadosAntigosAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _ultimaLimpeza < IntervaloDaLimpeza)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LancamentosDbContext>();

        var limite = DateTimeOffset.UtcNow - RetencaoDosPublicados;
        var removidos = await db.Outbox
            .Where(o => o.PublicadoEm != null && o.PublicadoEm < limite)
            .ExecuteDeleteAsync(ct);

        _ultimaLimpeza = DateTimeOffset.UtcNow;
        if (removidos > 0)
            logger.LogInformation("{Quantidade} evento(s) antigo(s) removido(s) da outbox", removidos);
    }

    private async Task<IChannel> ObterCanalAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await FecharConexaoAsync();

        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMQ:Host"] ?? "localhost",
            Port = configuration.GetValue("RabbitMQ:Port", 5672),
            UserName = configuration["RabbitMQ:User"] ?? "guest",
            Password = configuration["RabbitMQ:Password"] ?? "guest",
            ClientProvidedName = "lancamentos-api-outbox",
            // A reconexão é feita por este próprio loop, criando uma conexão nova no próximo ciclo.
            AutomaticRecoveryEnabled = false
        };

        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        await _channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        logger.LogInformation("Conectado ao RabbitMQ em {Host}", factory.HostName);
        return _channel;
    }

    private async Task FecharConexaoAsync()
    {
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Erro ao fechar a conexão com o RabbitMQ");
        }
        _channel = null;
        _connection = null;
    }
}
