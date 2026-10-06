using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Consolidado.Api.Observabilidade;

/// <summary>
/// Traces e métricas próprios da aplicação. Usa as APIs nativas do .NET (ActivitySource e Meter),
/// que o OpenTelemetry coleta e exporta. O código de negócio não depende de nenhum fornecedor.
/// </summary>
public static class Telemetria
{
    public const string Nome = "Consolidado.Api";

    // Header W3C que traz o contexto do trace dentro da mensagem do RabbitMQ.
    public const string HeaderTraceParent = "traceparent";

    public static readonly ActivitySource Traces = new(Nome);

    private static readonly Meter Metricas = new(Nome);

    public static readonly Counter<long> EventosAplicados = Metricas.CreateCounter<long>(
        "consolidado.eventos_aplicados", "{evento}", "Eventos somados ao saldo do dia.");

    public static readonly Counter<long> EventosDuplicados = Metricas.CreateCounter<long>(
        "consolidado.eventos_duplicados", "{evento}", "Eventos recebidos de novo e ignorados pela idempotência.");

    public static readonly Counter<long> MensagensRejeitadas = Metricas.CreateCounter<long>(
        "consolidado.mensagens_rejeitadas", "{mensagem}",
        "Mensagens recusadas pelo consumidor. Motivo invalida vai direto para a DLQ, erro_inesperado volta para a fila até o limite.");

    public static readonly Counter<long> FalhasTransitorias = Metricas.CreateCounter<long>(
        "consolidado.falhas_transitorias", "{falha}", "Tentativas de aplicar um evento com o banco do consolidado fora.");

    // Do momento do lançamento até o saldo ser atualizado. É a medida do atraso da consistência eventual.
    public static readonly Histogram<double> AtrasoDoEvento = Metricas.CreateHistogram<double>(
        "consolidado.atraso_do_evento", "s", "Tempo entre o registro do lançamento e a atualização do saldo.");

    public static readonly Counter<long> Leituras = Metricas.CreateCounter<long>(
        "consolidado.leituras", "{leitura}",
        "Consultas ao saldo, pela origem da resposta: cache, banco, ultimo_valor_conhecido ou indisponivel.");
}
