namespace Consolidado.Api.Seguranca;

/// <summary>
/// Sistema autorizado a chamar a API, lido da configuração (Seguranca:Clientes).
/// Cada cliente tem a própria chave, as próprias permissões e o próprio limite de requisições.
/// </summary>
public sealed class ClienteDaApi
{
    public string Nome { get; set; } = "";
    public string Chave { get; set; } = "";
    public string[] Permissoes { get; set; } = [];
    public int LimitePorSegundo { get; set; } = 50;
}
