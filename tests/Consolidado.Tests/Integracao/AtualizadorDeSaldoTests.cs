using Consolidado.Api.Data;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Consolidado.Tests.Integracao;

/// <summary>
/// Testa a atualização do saldo direto no banco, sem passar pelo RabbitMQ,
/// para exercitar concorrência de forma controlada.
/// </summary>
[Collection(ConsolidadoApiCollection.Nome)]
public class AtualizadorDeSaldoTests(ConsolidadoApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Datas bem distantes das usadas pelos outros testes, para não misturar saldos.
    private static DateOnly DataUnica() => DateOnly.FromDayNumber(Random.Shared.Next(800_000, 900_000));

    [Fact]
    public async Task Evento_repetido_e_aplicado_uma_unica_vez()
    {
        var evento = Evento(DataUnica(), "Credito", 100m);

        var primeira = await AplicarAsync(evento);
        var segunda = await AplicarAsync(evento);

        Assert.True(primeira);
        Assert.False(segunda);
        var saldo = await SaldoAsync(evento.Data);
        Assert.Equal(100m, saldo.TotalCreditos);
    }

    [Fact]
    public async Task Mesmo_evento_aplicado_em_paralelo_soma_uma_unica_vez()
    {
        var evento = Evento(DataUnica(), "Debito", 25m);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => AplicarAsync(evento)));

        Assert.Single(resultados, aplicado => aplicado);
        var saldo = await SaldoAsync(evento.Data);
        Assert.Equal(25m, saldo.TotalDebitos);
    }

    [Fact]
    public async Task Eventos_diferentes_do_mesmo_dia_em_paralelo_nao_perdem_atualizacao()
    {
        var dia = DataUnica();
        var eventos = Enumerable.Range(0, 50)
            .Select(i => Evento(dia, i % 2 == 0 ? "Credito" : "Debito", 10m + i))
            .ToList();

        await Task.WhenAll(eventos.Select(AplicarAsync));

        var esperadoCreditos = eventos.Where(e => e.Tipo == "Credito").Sum(e => e.Valor);
        var esperadoDebitos = eventos.Where(e => e.Tipo == "Debito").Sum(e => e.Valor);
        var saldo = await SaldoAsync(dia);
        Assert.Equal(esperadoCreditos, saldo.TotalCreditos);
        Assert.Equal(esperadoDebitos, saldo.TotalDebitos);
        Assert.Equal(esperadoCreditos - esperadoDebitos, saldo.Saldo);
    }

    private async Task<bool> AplicarAsync(LancamentoRegistrado evento)
    {
        // Um escopo (e um DbContext) por chamada, como acontece no consumidor.
        await using var scope = factory.Services.CreateAsyncScope();
        var atualizador = scope.ServiceProvider.GetRequiredService<AtualizadorDeSaldo>();
        return await atualizador.AplicarAsync(evento, Ct);
    }

    private async Task<Consolidado.Api.Domain.SaldoDiario> SaldoAsync(DateOnly data)
    {
        await using var db = factory.CriarDbContext();
        return await db.SaldosDiarios.AsNoTracking().SingleAsync(s => s.Data == data, Ct);
    }

    private static LancamentoRegistrado Evento(DateOnly data, string tipo, decimal valor) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), data, tipo, valor, DateTimeOffset.UtcNow);
}
