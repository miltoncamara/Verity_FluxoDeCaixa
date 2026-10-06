using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lancamentos.Tests.Integracao;

/// <summary>
/// A API deve recusar subir com uma configuração de segurança errada, em vez de subir aberta.
/// Não precisa de banco: a validação acontece antes de qualquer acesso a ele.
/// </summary>
public class ConfiguracaoDeSegurancaTests
{
    [Fact]
    public void Api_nao_sobe_com_a_chave_em_texto_no_lugar_do_hash()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seguranca:Clientes:0:Nome", "admin");
            builder.UseSetting("Seguranca:Clientes:0:ChaveSha256", "local-dev-key");
        });

        var erro = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ChaveSha256", erro.ToString());
    }
}
