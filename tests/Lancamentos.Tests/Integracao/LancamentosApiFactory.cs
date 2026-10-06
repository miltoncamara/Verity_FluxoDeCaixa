using Lancamentos.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Lancamentos.Tests.Integracao;

/// <summary>
/// Sobe a Lancamentos.Api em memória apontando para um PostgreSQL real em container.
/// O RabbitMQ aponta de propósito para uma porta onde não há nada: é o cenário de broker fora do ar.
/// </summary>
public sealed class LancamentosApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ClientesDeTeste.Configurar(builder);
        builder.UseSetting("ConnectionStrings:Lancamentos", _postgres.GetConnectionString());
        builder.UseSetting("RabbitMQ:Host", "localhost");
        builder.UseSetting("RabbitMQ:Port", "1");
    }

    public HttpClient CriarClienteAutenticado(string chave = ClientesDeTeste.ChaveCompleta)
    {
        var client = CreateDefaultClient(new IdempotencyKeyAutomatica());
        client.DefaultRequestHeaders.Add("X-Api-Key", chave);
        return client;
    }

    public Task PararBancoAsync() => _postgres.StopAsync();

    public LancamentosDbContext CriarDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<LancamentosDbContext>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
