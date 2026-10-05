using Consolidado.Api.Domain;

namespace Consolidado.Tests.Domain;

public class SaldoDiarioTests
{
    [Fact]
    public void Credito_soma_somente_nos_creditos()
    {
        var (creditos, debitos) = SaldoDiario.ContribuicaoDe("Credito", 100.50m);

        Assert.Equal(100.50m, creditos);
        Assert.Equal(0m, debitos);
    }

    [Fact]
    public void Debito_soma_somente_nos_debitos()
    {
        var (creditos, debitos) = SaldoDiario.ContribuicaoDe("Debito", 40m);

        Assert.Equal(0m, creditos);
        Assert.Equal(40m, debitos);
    }

    [Theory]
    [InlineData("Transferencia")]
    [InlineData("credito")]
    [InlineData("")]
    public void Tipo_desconhecido_e_rejeitado(string tipo)
    {
        Assert.Throws<ArgumentException>(() => SaldoDiario.ContribuicaoDe(tipo, 10m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Valor_nao_positivo_e_rejeitado(decimal valor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SaldoDiario.ContribuicaoDe("Credito", valor));
    }

    [Theory]
    [InlineData(500, 120.30, 379.70)]
    [InlineData(100, 140.25, -40.25)]
    [InlineData(0, 0, 0)]
    public void Saldo_e_creditos_menos_debitos(decimal creditos, decimal debitos, decimal saldoEsperado)
    {
        var saldo = new SaldoDiario(new DateOnly(2026, 10, 5), creditos, debitos);

        Assert.Equal(saldoEsperado, saldo.Saldo);
    }

    [Fact]
    public void Contribuicoes_de_varios_lancamentos_compoem_o_saldo_do_dia()
    {
        var lancamentos = new[] { ("Credito", 100m), ("Debito", 30m), ("Debito", 120.25m), ("Credito", 10m) };

        var totalCreditos = 0m;
        var totalDebitos = 0m;
        foreach (var (tipo, valor) in lancamentos)
        {
            var (c, d) = SaldoDiario.ContribuicaoDe(tipo, valor);
            totalCreditos += c;
            totalDebitos += d;
        }
        var saldo = new SaldoDiario(new DateOnly(2026, 10, 5), totalCreditos, totalDebitos);

        Assert.Equal(110m, saldo.TotalCreditos);
        Assert.Equal(150.25m, saldo.TotalDebitos);
        Assert.Equal(-40.25m, saldo.Saldo);
    }

    [Fact]
    public void Dia_sem_lancamentos_tem_saldo_zero()
    {
        var saldo = SaldoDiario.Vazio(new DateOnly(2026, 10, 5));

        Assert.Equal(0m, saldo.TotalCreditos);
        Assert.Equal(0m, saldo.TotalDebitos);
        Assert.Equal(0m, saldo.Saldo);
    }
}
