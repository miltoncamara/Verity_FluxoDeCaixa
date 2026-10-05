using System.Net;

namespace Lancamentos.Tests.Integracao;

[Collection(LancamentosApiCollection.Nome)]
public class HealthCheckTests(LancamentosApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_nao_exige_api_key_e_ignora_o_rabbitmq_fora_do_ar()
    {
        // Nesta factory o RabbitMQ está inacessível. Mesmo assim a API está saudável,
        // porque ela continua aceitando lançamentos e guardando os eventos na outbox.
        var client = factory.CreateClient();

        var resposta = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("Healthy", await resposta.Content.ReadAsStringAsync(Ct));
    }
}
