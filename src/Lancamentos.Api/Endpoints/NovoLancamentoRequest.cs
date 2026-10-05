namespace Lancamentos.Api.Endpoints;

/// <summary>
/// Corpo do POST /lancamentos. Os campos são anuláveis para que a validação
/// aconteça na entidade Lancamento, que devolve todos os erros de uma vez.
/// </summary>
public sealed record NovoLancamentoRequest(DateOnly? Data, string? Tipo, decimal? Valor, string? Descricao);
