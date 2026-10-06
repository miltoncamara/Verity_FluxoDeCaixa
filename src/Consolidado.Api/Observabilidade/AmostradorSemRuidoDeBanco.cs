using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Consolidado.Api.Observabilidade;

/// <summary>
/// Descarta spans de banco que não pertencem a nenhuma operação, como as consultas feitas fora de
/// uma requisição ou de um evento. Cada uma viraria um trace solto, enterrando os que importam.
/// Consultas feitas dentro de uma requisição ou do processamento de um evento continuam no trace.
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
