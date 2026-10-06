using System.Diagnostics;
using System.Text.Json;
using Contracts;
using Lancamentos.Api.Domain;

namespace Lancamentos.Api.Data;

/// <summary>
/// Evento pendente de publicação. É gravado na mesma transação do lançamento
/// e publicado no RabbitMQ depois, por um BackgroundService.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private init; }
    public string Tipo { get; private init; } = "";
    public string Payload { get; private init; } = "";
    public DateTimeOffset CriadoEm { get; private init; }
    public DateTimeOffset? PublicadoEm { get; set; }

    /// <summary>
    /// Contexto do trace da requisição que gerou o evento, no formato W3C (traceparent).
    /// O publicador continua esse trace e o leva até o consumidor dentro da mensagem, então um
    /// único trace mostra o POST, a publicação e a atualização do saldo, mesmo sendo assíncrono.
    /// </summary>
    public string? TraceParent { get; private init; }

    private OutboxMessage() { }

    public static OutboxMessage LancamentoRegistrado(Lancamento lancamento)
    {
        var evento = new LancamentoRegistrado(
            EventoId: Guid.CreateVersion7(),
            LancamentoId: lancamento.Id,
            Data: lancamento.Data,
            Tipo: lancamento.Tipo.ToString(),
            Valor: lancamento.Valor,
            OcorridoEm: lancamento.CriadoEm);

        return new OutboxMessage
        {
            Id = evento.EventoId,
            Tipo = nameof(Contracts.LancamentoRegistrado),
            Payload = JsonSerializer.Serialize(evento, JsonSerializerOptions.Web),
            CriadoEm = lancamento.CriadoEm,
            TraceParent = Activity.Current?.Id
        };
    }
}
