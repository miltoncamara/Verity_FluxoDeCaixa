using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lancamentos.Api.Observabilidade;

/// <summary>
/// Traces e métricas próprios da aplicação. Usa as APIs nativas do .NET (ActivitySource e Meter),
/// que o OpenTelemetry coleta e exporta. O código de negócio não depende de nenhum fornecedor.
/// </summary>
public static class Telemetria
{
    public const string Nome = "Lancamentos.Api";

    // Header W3C que leva o contexto do trace dentro da mensagem do RabbitMQ.
    public const string HeaderTraceParent = "traceparent";

    public static readonly ActivitySource Traces = new(Nome);

    private static readonly Meter Metricas = new(Nome);

    public static readonly Counter<long> LancamentosRegistrados = Metricas.CreateCounter<long>(
        "lancamentos.registrados", "{lancamento}", "Lançamentos confirmados ao cliente.");

    public static readonly Counter<long> EventosPublicados = Metricas.CreateCounter<long>(
        "outbox.eventos_publicados", "{evento}", "Eventos publicados no RabbitMQ e confirmados pelo broker.");

    public static readonly Counter<long> FalhasDePublicacao = Metricas.CreateCounter<long>(
        "outbox.falhas_de_publicacao", "{falha}", "Ciclos do publicador que falharam, por RabbitMQ ou banco fora.");

    // Atualizados pelo publicador a cada ciclo e lidos no momento em que as métricas são exportadas.
    // São os sinais mais importantes para alerta: se a idade do evento mais antigo cresce, o
    // consolidado está ficando para trás, mesmo que nenhuma requisição esteja falhando.
    private static long _eventosPendentes;
    private static double _idadeDoMaisAntigoEmSegundos;

    private static readonly ObservableGauge<long> MedidorDePendentes = Metricas.CreateObservableGauge(
        "outbox.eventos_pendentes", () => Interlocked.Read(ref _eventosPendentes), "{evento}",
        "Eventos gravados na outbox que ainda não foram publicados.");

    private static readonly ObservableGauge<double> MedidorDeIdade = Metricas.CreateObservableGauge(
        "outbox.idade_do_evento_mais_antigo", () => Volatile.Read(ref _idadeDoMaisAntigoEmSegundos), "s",
        "Há quanto tempo o evento pendente mais antigo espera para ser publicado.");

    public static void AtualizarOutbox(long eventosPendentes, double idadeDoMaisAntigoEmSegundos)
    {
        Interlocked.Exchange(ref _eventosPendentes, eventosPendentes);
        Volatile.Write(ref _idadeDoMaisAntigoEmSegundos, idadeDoMaisAntigoEmSegundos);
    }
}
