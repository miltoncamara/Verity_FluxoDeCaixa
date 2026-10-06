namespace Consolidado.Api.Endpoints;

/// <summary>
/// Resposta guardada em memória junto com o momento em que foi lida do banco.
/// </summary>
public sealed record LeituraEmCache<T>(T Valor, DateTimeOffset LidoEm);
