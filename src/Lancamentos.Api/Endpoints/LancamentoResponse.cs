using Lancamentos.Api.Domain;

namespace Lancamentos.Api.Endpoints;

public sealed record LancamentoResponse(Guid Id, DateOnly Data, string Tipo, decimal Valor, string Descricao, DateTimeOffset CriadoEm)
{
    public static LancamentoResponse De(Lancamento l) => new(l.Id, l.Data, l.Tipo.ToString(), l.Valor, l.Descricao, l.CriadoEm);
}
