namespace Consolidado.Api.Domain;

/// <summary>
/// Saldo consolidado de um dia, pré-calculado a partir dos eventos de lançamento.
/// A leitura é sempre por chave (data), nunca uma soma sobre os lançamentos.
/// </summary>
public sealed class SaldoDiario
{
    public DateOnly Data { get; private init; }
    public decimal TotalCreditos { get; private init; }
    public decimal TotalDebitos { get; private init; }
    public DateTimeOffset AtualizadoEm { get; private init; }

    public decimal Saldo => TotalCreditos - TotalDebitos;

    // Usado pelo EF Core para materializar a entidade.
    private SaldoDiario() { }

    public SaldoDiario(DateOnly data, decimal totalCreditos, decimal totalDebitos)
    {
        Data = data;
        TotalCreditos = totalCreditos;
        TotalDebitos = totalDebitos;
    }

    /// <summary>Saldo de um dia sem nenhum lançamento.</summary>
    public static SaldoDiario Vazio(DateOnly data) => new(data, 0m, 0m);

    /// <summary>
    /// Quanto um lançamento soma em créditos e em débitos do dia.
    /// Um tipo desconhecido indica evento inválido e não deve ser aplicado.
    /// </summary>
    public static (decimal Creditos, decimal Debitos) ContribuicaoDe(string tipo, decimal valor)
    {
        if (valor <= 0)
            throw new ArgumentOutOfRangeException(nameof(valor), valor, "Valor do lançamento deve ser positivo.");

        return tipo switch
        {
            "Credito" => (valor, 0m),
            "Debito" => (0m, valor),
            _ => throw new ArgumentException($"Tipo de lançamento desconhecido: '{tipo}'.", nameof(tipo))
        };
    }
}
