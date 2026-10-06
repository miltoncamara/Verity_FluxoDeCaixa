namespace Lancamentos.Api.Seguranca;

/// <summary>
/// Permissões que um cliente pode ter nesta API. Cada uma também é o nome da policy de autorização.
/// </summary>
public static class Permissoes
{
    public const string Claim = "permissao";

    public const string LancamentosEscrita = "lancamentos.escrita";
    public const string LancamentosLeitura = "lancamentos.leitura";
}
