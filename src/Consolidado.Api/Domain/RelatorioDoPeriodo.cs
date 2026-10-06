namespace Consolidado.Api.Domain;

/// <summary>
/// Relatório do saldo diário consolidado de um período, com uma linha por dia.
/// </summary>
/// <param name="SaldoInicial">Saldo do caixa antes do primeiro dia do período.</param>
/// <param name="SaldoFinal">Saldo do caixa no fim do último dia do período.</param>
public sealed record RelatorioDoPeriodo(
    DateOnly Inicio,
    DateOnly Fim,
    decimal SaldoInicial,
    decimal TotalCreditos,
    decimal TotalDebitos,
    decimal SaldoFinal,
    IReadOnlyList<DiaDoRelatorio> Dias)
{
    public const int DiasNoMaximo = 366;

    /// <returns>A mensagem de erro, ou null se o período for válido.</returns>
    public static string? ValidarPeriodo(DateOnly inicio, DateOnly fim)
    {
        if (fim < inicio)
            return "A data final deve ser igual ou posterior à data inicial.";
        if (fim.DayNumber - inicio.DayNumber + 1 > DiasNoMaximo)
            return $"O período deve ter no máximo {DiasNoMaximo} dias.";
        return null;
    }

    /// <summary>
    /// Monta o relatório a partir do saldo anterior ao período e dos saldos diários já consolidados.
    /// Dias sem movimento entram com zero, para o relatório não ter buracos.
    /// O acumulado é calculado aqui, na leitura, e não gravado no banco. Assim um lançamento
    /// registrado com data passada corrige automaticamente o acumulado de todos os dias seguintes.
    /// </summary>
    public static RelatorioDoPeriodo Montar(
        DateOnly inicio, DateOnly fim, decimal saldoInicial, IEnumerable<SaldoDiario> saldosDoPeriodo)
    {
        if (ValidarPeriodo(inicio, fim) is { } erro)
            throw new ArgumentException(erro);

        var saldoPorData = saldosDoPeriodo.ToDictionary(s => s.Data);
        var dias = new List<DiaDoRelatorio>();
        var acumulado = saldoInicial;

        for (var numeroDoDia = inicio.DayNumber; numeroDoDia <= fim.DayNumber; numeroDoDia++)
        {
            var data = DateOnly.FromDayNumber(numeroDoDia);
            var saldo = saldoPorData.GetValueOrDefault(data) ?? SaldoDiario.Vazio(data);
            acumulado += saldo.Saldo;
            dias.Add(new DiaDoRelatorio(data, saldo.TotalCreditos, saldo.TotalDebitos, saldo.Saldo, acumulado));
        }

        return new RelatorioDoPeriodo(
            inicio,
            fim,
            saldoInicial,
            TotalCreditos: dias.Sum(d => d.TotalCreditos),
            TotalDebitos: dias.Sum(d => d.TotalDebitos),
            SaldoFinal: acumulado,
            dias);
    }
}
