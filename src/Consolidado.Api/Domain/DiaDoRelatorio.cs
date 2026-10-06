namespace Consolidado.Api.Domain;

/// <summary>
/// Uma linha do relatório: o movimento do dia e quanto há no caixa ao fim dele.
/// </summary>
/// <param name="SaldoDoDia">Créditos menos débitos do próprio dia.</param>
/// <param name="SaldoAcumulado">Saldo do caixa no fim do dia: o saldo anterior mais o saldo do dia.</param>
public sealed record DiaDoRelatorio(
    DateOnly Data,
    decimal TotalCreditos,
    decimal TotalDebitos,
    decimal SaldoDoDia,
    decimal SaldoAcumulado);
