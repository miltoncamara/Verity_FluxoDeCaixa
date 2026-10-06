using Consolidado.Api.Data;
using Consolidado.Api.Endpoints;
using Consolidado.Api.Messaging;
using Consolidado.Api.Seguranca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Consolidado");

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddDbContext<ConsolidadoDbContext>(options => options
    .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
    .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<AtualizadorDeSaldo>();
builder.Services.AddHostedService<LancamentoRegistradoConsumer>();

// O health check olha somente o próprio banco, nunca o RabbitMQ nem a Lancamentos.Api.
builder.Services.AddHealthChecks().AddAsyncCheck("postgres", async ct =>
{
    try
    {
        await using var conexao = new NpgsqlConnection(connectionString);
        await conexao.OpenAsync(ct);
        return HealthCheckResult.Healthy();
    }
    catch (Exception ex)
    {
        return HealthCheckResult.Unhealthy("Banco do consolidado indisponível.", ex);
    }
}, timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

// Erro de leitura da requisição (JSON inválido, data em formato errado, parâmetro faltando) responde 400.
// Sem isso, no ambiente Development o ASP.NET lança a exceção e o handler devolveria 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException erro ? erro.StatusCode : StatusCodes.Status500InternalServerError
});
app.UseStatusCodePages();
app.UseMiddleware<ApiKeyMiddleware>();

// Aplica as migrations na inicialização. Suficiente para rodar localmente.
// Em produção a migration rodaria como etapa separada do pipeline de deploy.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ConsolidadoDbContext>();
    await db.Database.MigrateAsync();
}

app.MapHealthChecks("/health");
app.MapConsolidadoEndpoints();

app.Run();

// Exposto para os testes de integração com WebApplicationFactory.
public partial class Program;
