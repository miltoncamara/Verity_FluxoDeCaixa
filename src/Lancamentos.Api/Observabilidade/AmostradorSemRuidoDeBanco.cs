using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Lancamentos.Api.Observabilidade;

/// <summary>
/// Descarta spans de banco que não pertencem a nenhuma operação. O publicador da outbox consulta o
/// banco a cada 500 ms, e cada consulta viraria um trace solto, enterrando os traces que importam.
/// Consultas feitas dentro de uma requisição ou de uma publicação continuam no trace, como filhas.
/// </summary>
public sealed class AmostradorSemRuidoDeBanco : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters parametros)
    {
        var semOperacaoPai = parametros.ParentContext.TraceId == default;
        var chamadaAoBanco = parametros.Kind == ActivityKind.Client;

        return semOperacaoPai && chamadaAoBanco
            ? new SamplingResult(SamplingDecision.Drop)
            : new SamplingResult(SamplingDecision.RecordAndSample);
    }
}
