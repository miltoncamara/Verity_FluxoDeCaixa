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
            CriadoEm = lancamento.CriadoEm
        };
    }
}
