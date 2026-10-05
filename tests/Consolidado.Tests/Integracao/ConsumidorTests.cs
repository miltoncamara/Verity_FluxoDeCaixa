using System.Net.Http.Json;
using Consolidado.Api.Endpoints;
using Consolidado.Api.Messaging;
using Contracts;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Tests.Integracao;

/// <summary>
/// Testes de ponta a ponta do consumidor: a mensagem é publicada no RabbitMQ real
/// e o resultado é verificado pela API e pelo banco.
/// </summary>
[Collection(ConsolidadoApiCollection.Nome)]
public class ConsumidorTests(ConsolidadoApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly DataUnica() => DateOnly.FromDayNumber(Random.Shared.Next(700_000, 800_000));

    [Fact]
    public async Task Eventos_publicados_atualizam_o_consolidado_do_dia()
    {
        var dia = DataUnica();
        await factory.PublicarAsync(Evento(dia, "Credito", 500m));
        await factory.PublicarAsync(Evento(dia, "Debito", 120.30m));
        await factory.PublicarAsync(Evento(dia, "Credito", 0.30m));

        var consolidado = await AguardarConsolidadoAsync(dia, c => c.TotalCreditos == 500.30m && c.TotalDebitos == 120.30m);

        Assert.Equal(dia, consolidado.Data);
        Assert.Equal(380m, consolidado.Saldo);
    }

    [Fact]
    public async Task Evento_entregue_duas_vezes_e_somado_uma_unica_vez()
    {
        var dia = DataUnica();
        var duplicado = Evento(dia, "Credito", 70m);
        var marcador = Evento(dia, "Debito", 1m);

        await factory.PublicarAsync(duplicado);
        await factory.PublicarAsync(duplicado);
        await factory.PublicarAsync(marcador);

        // A fila é processada em ordem, então quando o marcador chegar as duas cópias já foram tratadas.
        var consolidado = await AguardarConsolidadoAsync(dia, c => c.TotalDebitos == 1m);
        Assert.Equal(70m, consolidado.TotalCreditos);

        await using var db = factory.CriarDbContext();
        Assert.True(await db.EventosProcessados.AnyAsync(e => e.EventoId == duplicado.EventoId, Ct));
    }

    [Fact]
    public async Task Dia_sem_lancamentos_retorna_saldo_zero()
    {
        var consolidado = await _client.GetFromJsonAsync<ConsolidadoResponse>($"/consolidado/{DataUnica():yyyy-MM-dd}", Ct);

        Assert.NotNull(consolidado);
        Assert.Equal(0m, consolidado.TotalCreditos);
        Assert.Equal(0m, consolidado.TotalDebitos);
        Assert.Equal(0m, consolidado.Saldo);
    }

    [Fact]
    public async Task Mensagem_invalida_vai_direto_para_a_dead_letter_queue()
    {
        var messageId = Guid.NewGuid().ToString();
        var corpo = $$"""{"eventoId":"{{messageId}}","tipo":"Transferencia","valor":10,"data":"2026-10-05"}""";

        await factory.PublicarAsync(corpo, messageId);

        string? mensagemNaDlq = null;
        await Aguardar.AteQue(async () =>
        {
            mensagemNaDlq = await factory.LerDaDeadLetterAsync();
            return mensagemNaDlq is not null;
        }, "mensagem chegar na DLQ");
        Assert.Contains(messageId, mensagemNaDlq);
    }

    [Fact]
    public async Task Erro_inesperado_devolve_para_a_fila_ate_o_limite_e_depois_vai_para_a_dead_letter_queue()
    {
        // Simula um erro que não é culpa da mensagem nem é falha transitória: a tabela de saldo some.
        await using var db = factory.CriarDbContext();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE saldo_diario RENAME TO saldo_diario_tmp", Ct);
        try
        {
            var evento = Evento(DataUnica(), "Credito", 10m);
            await factory.PublicarAsync(evento);

            string? mensagemNaDlq = null;
            await Aguardar.AteQue(async () =>
            {
                mensagemNaDlq = await factory.LerDaDeadLetterAsync();
                return mensagemNaDlq is not null;
            }, $"mensagem chegar na DLQ após {LancamentoRegistradoConsumer.LimiteDeEntregas} tentativas");

            Assert.Contains(evento.EventoId.ToString(), mensagemNaDlq);
            Assert.False(await db.EventosProcessados.AnyAsync(e => e.EventoId == evento.EventoId, Ct));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE saldo_diario_tmp RENAME TO saldo_diario", Ct);
        }
    }

    private async Task<ConsolidadoResponse> AguardarConsolidadoAsync(DateOnly dia, Func<ConsolidadoResponse, bool> condicao)
    {
        ConsolidadoResponse? consolidado = null;
        await Aguardar.AteQue(async () =>
        {
            consolidado = await _client.GetFromJsonAsync<ConsolidadoResponse>($"/consolidado/{dia:yyyy-MM-dd}", Ct);
            return consolidado is not null && condicao(consolidado);
        }, $"consolidado de {dia} atualizar");
        return consolidado!;
    }

    private static LancamentoRegistrado Evento(DateOnly data, string tipo, decimal valor) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), data, tipo, valor, DateTimeOffset.UtcNow);
}
