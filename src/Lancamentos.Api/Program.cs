using Lancamentos.Api.Data;
using Lancamentos.Api.Endpoints;
using Lancamentos.Api.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddDbContext<LancamentosDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Lancamentos"),
        npgsql => npgsql.EnableRetryOnFailure())
    .UseSnakeCaseNamingConvention());
builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Aplica as migrations na inicialização. Suficiente para rodar localmente.
// Em produção a migration rodaria como etapa separada do pipeline de deploy.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LancamentosDbContext>();
    await db.Database.MigrateAsync();
}

app.MapLancamentosEndpoints();

app.Run();

// Exposto para os testes de integração com WebApplicationFactory.
public partial class Program;
