using Consolidado.Api.Domain;

namespace Consolidado.Api.Endpoints;

/// <summary>
/// Saldo guardado em memória junto com o momento em que foi lido do banco.
/// </summary>
public sealed record SaldoEmCache(SaldoDiario Saldo, DateTimeOffset LidoEm);
