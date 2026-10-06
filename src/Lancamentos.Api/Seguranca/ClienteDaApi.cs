namespace Lancamentos.Api.Seguranca;

/// <summary>
/// Sistema autorizado a chamar a API, lido da configuração (Seguranca:Clientes).
/// Cada cliente tem a própria chave, as próprias permissões e o próprio limite de requisições.
/// A configuração guarda só o hash SHA-256 da chave, nunca a chave em si.
/// </summary>
public sealed class ClienteDaApi
{
    public string Nome { get; set; } = "";

    /// <summary>SHA-256 da chave, em hexadecimal. Para gerar: echo -n "a-chave" | sha256sum</summary>
    public string ChaveSha256 { get; set; } = "";
    public string[] Permissoes { get; set; } = [];
    public int LimitePorSegundo { get; set; } = 50;

    public byte[] HashDaChave => Convert.FromHexString(ChaveSha256);
}
