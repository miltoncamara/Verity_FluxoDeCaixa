using Lancamentos.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Lancamentos.Tests.Integracao;

/// <summary>
/// Sobe a Lancamentos.Api com PostgreSQL e RabbitMQ reais, para testar o publicador da outbox.
/// </summary>
public sealed class LancamentosComRabbitMqFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithUsername("fluxo").WithPassword("teste").Build();

    public string RabbitMqConnectionString => _rabbit.GetConnectionString();

    public async ValueTask InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Lancamentos", _postgres.GetConnectionString());
        builder.UseSetting("RabbitMQ:Host", _rabbit.Hostname);
        builder.UseSetting("RabbitMQ:Port", _rabbit.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("RabbitMQ:User", "fluxo");
        builder.UseSetting("RabbitMQ:Password", "teste");
    }

    public LancamentosDbContext CriarDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<LancamentosDbContext>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}
