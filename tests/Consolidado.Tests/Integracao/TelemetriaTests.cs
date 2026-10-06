using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Consolidado.Api.Observabilidade;
using Contracts;

namespace Consolidado.Tests.Integracao;

[Collection(ConsolidadoApiCollection.Nome)]
public class TelemetriaTests(ConsolidadoApiFactory factory)
{
    private static DateOnly DataUnica() => DateOnly.FromDayNumber(Random.Shared.Next(1_100_000, 1_200_000));

    [Fact]
    public async Task Consumidor_continua_o_trace_que_veio_dentro_da_mensagem()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var traceparent = $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";

        var atividades = new ConcurrentBag<Activity>();
        using var ouvinte = new ActivityListener
        {
            ShouldListenTo = fonte => fonte.Name == Telemetria.Nome,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = atividades.Add
        };
        ActivitySource.AddActivityListener(ouvinte);

        var evento = new LancamentoRegistrado(Guid.CreateVersion7(), Guid.CreateVersion7(), DataUnica(), "Credito", 10m, DateTimeOffset.UtcNow);
        await factory.PublicarAsync(JsonSerializer.Serialize(evento, JsonSerializerOptions.Web), evento.EventoId.ToString(), traceparent);

        await Aguardar.AteQue(() => Task.FromResult(atividades.Any(a => a.TraceId == traceId)), "consumidor registrar o span no trace recebido");

        var processamento = atividades.First(a => a.TraceId == traceId);
        Assert.Equal("processar LancamentoRegistrado", processamento.DisplayName);
        Assert.Equal(ActivityKind.Consumer, processamento.Kind);
        Assert.Equal(evento.EventoId.ToString(), processamento.GetTagItem("messaging.message.id"));
    }

    [Fact]
    public async Task Metricas_contam_eventos_aplicados_e_duplicados()
    {
        var medicoes = new ConcurrentDictionary<string, long>();
        using var ouvinte = new MeterListener
        {
            InstrumentPublished = (instrumento, l) =>
            {
                if (instrumento.Meter.Name == Telemetria.Nome)
                    l.EnableMeasurementEvents(instrumento);
            }
        };
        ouvinte.SetMeasurementEventCallback<long>((instrumento, valor, _, _) => medicoes.AddOrUpdate(instrumento.Name, valor, (_, total) => total + valor));
        ouvinte.Start();

        var evento = new LancamentoRegistrado(Guid.CreateVersion7(), Guid.CreateVersion7(), DataUnica(), "Credito", 10m, DateTimeOffset.UtcNow);
        await factory.PublicarAsync(evento);
        await factory.PublicarAsync(evento);

        // Outros testes podem rodar ao mesmo tempo, então o teste confere o mínimo esperado.
        await Aguardar.AteQue(() => Task.FromResult(
            medicoes.GetValueOrDefault("consolidado.eventos_aplicados") >= 1 &&
            medicoes.GetValueOrDefault("consolidado.eventos_duplicados") >= 1), "métricas registrarem o evento aplicado e o duplicado");
    }
}
