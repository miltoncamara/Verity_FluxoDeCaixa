using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace Consolidado.Tests.Integracao;

/// <summary>
/// Clientes da API usados nos testes, cada um com permissões e limite de requisições diferentes.
/// </summary>
public static class ClientesDeTeste
{
    public const string ChaveCompleta = "chave-de-teste";  // todas as permissões, sem limite prático
    public const string ChavePdv = "chave-pdv";            // só registra lançamentos
    public const string ChaveBi = "chave-bi";              // só lê
    public const string ChaveLimitada = "chave-limitada";  // todas as permissões, 3 requisições por segundo

    public const string NomeCompleto = "teste";
    public const int LimiteDoClienteLimitado = 3;

    private static readonly string[] Todas = ["lancamentos.escrita", "lancamentos.leitura", "consolidado.leitura"];

    public static void Configurar(IWebHostBuilder builder)
    {
        Cliente(builder, 0, NomeCompleto, ChaveCompleta, Todas, 100_000);
        Cliente(builder, 1, "pdv", ChavePdv, ["lancamentos.escrita"], 1_000);
        Cliente(builder, 2, "bi", ChaveBi, ["lancamentos.leitura", "consolidado.leitura"], 1_000);
        Cliente(builder, 3, "limitado", ChaveLimitada, Todas, LimiteDoClienteLimitado);
    }

    private static void Cliente(IWebHostBuilder builder, int i, string nome, string chave, string[] permissoes, int limite)
    {
        builder.UseSetting($"Seguranca:Clientes:{i}:Nome", nome);
        // A API só conhece o hash da chave, como em produção.
        builder.UseSetting($"Seguranca:Clientes:{i}:ChaveSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chave))));
        builder.UseSetting($"Seguranca:Clientes:{i}:LimitePorSegundo", limite.ToString());
        for (var p = 0; p < permissoes.Length; p++)
            builder.UseSetting($"Seguranca:Clientes:{i}:Permissoes:{p}", permissoes[p]);
    }
}
