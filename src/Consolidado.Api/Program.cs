using Consolidado.Api.Data;
using Consolidado.Api.Endpoints;
using Consolidado.Api.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddDbContext<ConsolidadoDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Consolidado"),
        npgsql => npgsql.EnableRetryOnFailure())
    .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<AtualizadorDeSaldo>();
builder.Services.AddHostedService<LancamentoRegistradoConsumer>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Aplica as migrations na inicialização. Suficiente para rodar localmente.
// Em produção a migration rodaria como etapa separada do pipeline de deploy.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ConsolidadoDbContext>();
    await db.Database.MigrateAsync();
}

app.MapConsolidadoEndpoints();

app.Run();

// Exposto para os testes de integração com WebApplicationFactory.
public partial class Program;
