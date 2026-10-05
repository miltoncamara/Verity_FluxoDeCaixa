namespace Contracts;

/// <summary>
/// Evento publicado pela Lancamentos.Api quando um lançamento é confirmado.
/// EventoId é o Id da linha na outbox e serve como chave de idempotência no consumidor.
/// Tipo é "Credito" ou "Debito".
/// </summary>
public sealed record LancamentoRegistrado(
    Guid EventoId,
    Guid LancamentoId,
    DateOnly Data,
    string Tipo,
    decimal Valor,
    DateTimeOffset OcorridoEm);
