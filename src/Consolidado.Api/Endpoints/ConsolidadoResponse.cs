using Consolidado.Api.Domain;

namespace Consolidado.Api.Endpoints;

public sealed record ConsolidadoResponse(DateOnly Data, decimal TotalCreditos, decimal TotalDebitos, decimal Saldo)
{
    public static ConsolidadoResponse De(SaldoDiario s) => new(s.Data, s.TotalCreditos, s.TotalDebitos, s.Saldo);
}
