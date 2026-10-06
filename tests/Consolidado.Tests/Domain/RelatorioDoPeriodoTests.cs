using Consolidado.Api.Domain;

namespace Consolidado.Tests.Domain;

public class RelatorioDoPeriodoTests
{
    private static readonly DateOnly Dia1 = new(2026, 10, 1);
    private static readonly DateOnly Dia2 = new(2026, 10, 2);
    private static readonly DateOnly Dia3 = new(2026, 10, 3);

    [Fact]
    public void Saldo_do_dia_pode_ser_negativo_enquanto_o_acumulado_continua_positivo()
    {
        var saldos = new[] { new SaldoDiario(Dia1, 1000m, 200m), new SaldoDiario(Dia2, 300m, 500m) };

        var relatorio = RelatorioDoPeriodo.Montar(Dia1, Dia3, saldoInicial: 0m, saldos);

        Assert.Equal([800m, -200m, 0m], relatorio.Dias.Select(d => d.SaldoDoDia));
        Assert.Equal([800m, 600m, 600m], relatorio.Dias.Select(d => d.SaldoAcumulado));
    }

    [Fact]
    public void Dias_sem_movimento_entram_zerados_e_repetem_o_acumulado()
    {
        var saldos = new[] { new SaldoDiario(Dia2, 50m, 0m) };

        var relatorio = RelatorioDoPeriodo.Montar(Dia1, Dia3, saldoInicial: 100m, saldos);

        Assert.Equal([Dia1, Dia2, Dia3], relatorio.Dias.Select(d => d.Data));
        var dia1 = relatorio.Dias[0];
        Assert.Equal(0m, dia1.TotalCreditos);
        Assert.Equal(0m, dia1.TotalDebitos);
        Assert.Equal(0m, dia1.SaldoDoDia);
        Assert.Equal([100m, 150m, 150m], relatorio.Dias.Select(d => d.SaldoAcumulado));
    }

    [Fact]
    public void Acumulado_parte_do_saldo_inicial_e_o_saldo_final_fecha_com_os_totais()
    {
        var saldos = new[] { new SaldoDiario(Dia1, 500m, 120.30m), new SaldoDiario(Dia3, 10m, 300m) };

        var relatorio = RelatorioDoPeriodo.Montar(Dia1, Dia3, saldoInicial: 1000m, saldos);

        Assert.Equal(1000m, relatorio.SaldoInicial);
        Assert.Equal(510m, relatorio.TotalCreditos);
        Assert.Equal(420.30m, relatorio.TotalDebitos);
        Assert.Equal(1089.70m, relatorio.SaldoFinal);
        Assert.Equal(relatorio.SaldoInicial + relatorio.TotalCreditos - relatorio.TotalDebitos, relatorio.SaldoFinal);
        Assert.Equal(relatorio.SaldoFinal, relatorio.Dias[^1].SaldoAcumulado);
    }

    [Fact]
    public void Periodo_de_um_unico_dia()
    {
        var relatorio = RelatorioDoPeriodo.Montar(Dia1, Dia1, saldoInicial: -50m, [new SaldoDiario(Dia1, 20m, 0m)]);

        var dia = Assert.Single(relatorio.Dias);
        Assert.Equal(-30m, dia.SaldoAcumulado);
        Assert.Equal(-30m, relatorio.SaldoFinal);
    }

    [Fact]
    public void Periodo_com_fim_antes_do_inicio_e_invalido()
    {
        Assert.NotNull(RelatorioDoPeriodo.ValidarPeriodo(Dia2, Dia1));
        Assert.Throws<ArgumentException>(() => RelatorioDoPeriodo.Montar(Dia2, Dia1, 0m, []));
    }

    [Fact]
    public void Periodo_acima_do_limite_de_dias_e_invalido()
    {
        var inicio = new DateOnly(2026, 1, 1);

        Assert.Null(RelatorioDoPeriodo.ValidarPeriodo(inicio, inicio.AddDays(RelatorioDoPeriodo.DiasNoMaximo - 1)));
        Assert.NotNull(RelatorioDoPeriodo.ValidarPeriodo(inicio, inicio.AddDays(RelatorioDoPeriodo.DiasNoMaximo)));
    }
}
