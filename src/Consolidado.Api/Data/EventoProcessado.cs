namespace Consolidado.Api.Data;

/// <summary>
/// Registro de que um evento já foi aplicado ao saldo. O Id do evento é a chave primária,
/// então o mesmo evento não consegue ser registrado duas vezes.
/// </summary>
public sealed class EventoProcessado
{
    public Guid EventoId { get; private init; }
    public DateTimeOffset ProcessadoEm { get; private init; }

    private EventoProcessado() { }
}
