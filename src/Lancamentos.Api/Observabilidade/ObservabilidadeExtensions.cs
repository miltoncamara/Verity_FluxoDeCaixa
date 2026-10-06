using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Lancamentos.Api.Observabilidade;

/// <summary>
/// Liga os três pilares do OpenTelemetry: traces, métricas e logs estruturados.
/// A exportação usa OTLP, o protocolo padrão aceito por Datadog, Azure Monitor, Grafana, New Relic e
/// outros. O destino vem da variável padrão OTEL_EXPORTER_OTLP_ENDPOINT. Trocar de fornecedor não
/// muda o código: muda só para onde o OTLP é enviado.
/// </summary>
public static class ObservabilidadeExtensions
{
    public const string NomeDoServico = "lancamentos-api";

    public static void AddObservabilidade(this WebApplicationBuilder builder)
    {
        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(recurso => recurso.AddService(
                serviceName: NomeDoServico,
                serviceVersion: typeof(Telemetria).Assembly.GetName().Version?.ToString()))
            .WithTracing(traces => traces
                .SetSampler(new AmostradorSemRuidoDeBanco())
                .AddSource(Telemetria.Nome)
                // Health checks são chamados o tempo todo e não interessam como trace.
                .AddAspNetCoreInstrumentation(opcoes => opcoes.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddNpgsql())
            .WithMetrics(metricas => metricas
                .AddMeter(Telemetria.Nome)
                .AddAspNetCoreInstrumentation() // duração, status e rate limiting das requisições HTTP
                .AddRuntimeInstrumentation()    // GC, threads e memória do .NET
                .AddNpgsqlInstrumentation())    // pool de conexões e duração dos comandos
            .WithLogging(logs => { }, opcoes =>
            {
                // Os logs vão com a mensagem formatada, os campos estruturados e o TraceId e SpanId,
                // para cada linha de log ser ligada ao trace em que aconteceu.
                opcoes.IncludeFormattedMessage = true;
                opcoes.IncludeScopes = true;
            });

        // Sem destino configurado, a telemetria continua sendo coletada, mas não é enviada a lugar nenhum.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            openTelemetry.UseOtlpExporter();
    }
}
