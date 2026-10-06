using System.Net;
using System.Net.Http.Json;
using Consolidado.Api.Domain;
using Contracts;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Tests.Integracao;

[Collection(ConsolidadoApiCollection.Nome)]
public class RelatorioTests(ConsolidadoApiFactory factory)
{
    private readonly HttpClient _client = factory.CriarClienteAutenticado();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Datas acima das usadas pelos outros testes, para cada teste ter o seu próprio período.
    private static DateOnly DataUnica() => DateOnly.FromDayNumber(Random.Shared.Next(1_000_000, 1_100_000));

    [Fact]
    public async Task Relatorio_traz_um_dia_por_linha_com_saldo_do_dia_e_saldo_acumulado()
    {
        var dia1 = DataUnica();
        var dia2 = dia1.AddDays(1);
        var dia3 = dia1.AddDays(2);
        await InserirSaldoAsync(dia1, creditos: 1000m, debitos: 200m);
        await InserirSaldoAsync(dia2, creditos: 300m, debitos: 500m);
        var saldoInicial = await SaldoAntesDeAsync(dia1);

        var relatorio = await ObterRelatorioAsync(dia1, dia3);

        Assert.Equal(saldoInicial, relatorio.SaldoInicial);
        Assert.Equal([dia1, dia2, dia3], relatorio.Dias.Select(d => d.Data));
        Assert.Equal([800m, -200m, 0m], relatorio.Dias.Select(d => d.SaldoDoDia));
        Assert.Equal([saldoInicial + 800m, saldoInicial + 600m, saldoInicial + 600m], relatorio.Dias.Select(d => d.SaldoAcumulado));
        Assert.Equal(1300m, relatorio.TotalCreditos);
        Assert.Equal(700m, relatorio.TotalDebitos);
        Assert.Equal(saldoInicial + 600m, relatorio.SaldoFinal);
    }

    [Fact]
    public async Task Lancamento_com_data_passada_corrige_o_acumulado_de_todos_os_dias_seguintes()
    {
        var dia1 = DataUnica();
        var dia2 = dia1.AddDays(1);
        await InserirSaldoAsync(dia1, creditos: 100m, debitos: 0m);
        var antes = await ObterRelatorioAsync(dia1, dia2);

        // Chega um lançamento de um dia anterior ao período, pelo fluxo normal do RabbitMQ.
        var atrasado = new LancamentoRegistrado(Guid.CreateVersion7(), Guid.CreateVersion7(), dia1.AddDays(-10), "Credito", 70m, DateTimeOffset.UtcNow);
        await factory.PublicarAsync(atrasado);

        RelatorioDoPeriodo? depois = null;
        await Aguardar.AteQue(async () =>
        {
            depois = await ObterRelatorioAsync(dia1, dia2);
            return depois.SaldoInicial == antes.SaldoInicial + 70m;
        }, "relatório refletir o lançamento com data passada");

        Assert.Equal(antes.Dias.Select(d => d.SaldoAcumulado + 70m), depois!.Dias.Select(d => d.SaldoAcumulado));
        Assert.Equal(antes.Dias.Select(d => d.SaldoDoDia), depois.Dias.Select(d => d.SaldoDoDia));
        Assert.Equal(antes.SaldoFinal + 70m, depois.SaldoFinal);
    }

    [Theory]
    [InlineData("inicio=2026-10-10&fim=2026-10-01")]
    [InlineData("inicio=2026-01-01&fim=2027-01-02")]
    [InlineData("inicio=2026-10-01")]
    [InlineData("inicio=01/10/2026&fim=31/10/2026")]
    [InlineData("inicio=10-01-2026&fim=10-31-2026")]
    public async Task Periodo_invalido_retorna_400(string consulta)
    {
        var resposta = await _client.GetAsync($"/consolidado?{consulta}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("07-10-2026")]
    [InlineData("07/10/2026")]
    [InlineData("2026-13-01")]
    public async Task Consulta_de_um_dia_com_data_fora_do_formato_iso_retorna_400(string data)
    {
        var resposta = await _client.GetAsync($"/consolidado/{Uri.EscapeDataString(data)}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    private async Task<RelatorioDoPeriodo> ObterRelatorioAsync(DateOnly inicio, DateOnly fim) =>
        (await _client.GetFromJsonAsync<RelatorioDoPeriodo>($"/consolidado?inicio={inicio:yyyy-MM-dd}&fim={fim:yyyy-MM-dd}", Ct))!;

    private async Task InserirSaldoAsync(DateOnly data, decimal creditos, decimal debitos)
    {
        await using var db = factory.CriarDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO saldo_diario (data, total_creditos, total_debitos, atualizado_em) VALUES ({data}, {creditos}, {debitos}, now())", Ct);
    }

    private async Task<decimal> SaldoAntesDeAsync(DateOnly data)
    {
        await using var db = factory.CriarDbContext();
        return await db.SaldosDiarios.Where(s => s.Data < data).SumAsync(s => s.TotalCreditos - s.TotalDebitos, Ct);
    }
}
