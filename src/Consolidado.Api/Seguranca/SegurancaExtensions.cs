using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;

namespace Consolidado.Api.Seguranca;

/// <summary>
/// Reúne toda a configuração de segurança da API: autenticação, autorização, limite de requisições,
/// limite de tamanho do corpo e headers de segurança.
/// </summary>
public static class SegurancaExtensions
{
    // Esta API só recebe GET. O limite de 16 KB barra corpos gigantes logo na entrada.
    public const long TamanhoMaximoDoCorpo = 16 * 1024;

    // Requisições sem chave válida dividem um único limite. Isso freia tentativas de adivinhar chaves
    // sem afetar os clientes legítimos, que têm cada um o seu limite.
    public const int LimiteSemChaveValidaPorSegundo = 10;

    public static void AddSeguranca(this WebApplicationBuilder builder)
    {
        var clientes = builder.Configuration.GetSection("Seguranca:Clientes").Get<List<ClienteDaApi>>() ?? [];
        ValidarClientes(clientes);
        builder.Services.AddSingleton<IReadOnlyList<ClienteDaApi>>(clientes);

        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.Esquema)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.Esquema, null);

        // Seguro por padrão: todo endpoint exige um cliente autenticado, a não ser que diga o contrário.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Permissoes.ConsolidadoLeitura, p => p.RequireClaim(Permissoes.Claim, Permissoes.ConsolidadoLeitura));

        var limitePorCliente = clientes.ToDictionary(c => c.Nome, c => c.LimitePorSegundo);
        builder.Services.AddRateLimiter(opcoes =>
        {
            opcoes.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/health"))
                    return RateLimitPartition.GetNoLimiter("health");

                var nome = context.User.Identity is { IsAuthenticated: true } identidade ? identidade.Name! : null;
                return nome is not null
                    ? RateLimitPartition.GetFixedWindowLimiter($"cliente:{nome}", _ => PorSegundo(limitePorCliente[nome]))
                    : RateLimitPartition.GetFixedWindowLimiter("sem-chave-valida", _ => PorSegundo(LimiteSemChaveValidaPorSegundo));
            });

            opcoes.OnRejected = (rejeitado, _) =>
            {
                rejeitado.HttpContext.Response.Headers.RetryAfter = "1";
                return new ValueTask(Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Limite de requisições excedido. Tente de novo em instantes.").ExecuteAsync(rejeitado.HttpContext));
            };
        });
    }

    public static void UseSeguranca(this WebApplication app)
    {
        app.Use(LimitarCorpoEAdicionarHeadersAsync);
        app.UseAuthentication();
        app.UseRateLimiter(); // depois da autenticação, para saber de qual cliente é a requisição
        app.UseAuthorization();
    }

    private static async Task LimitarCorpoEAdicionarHeadersAsync(HttpContext context, RequestDelegate next)
    {
        // Headers recomendados pela OWASP para APIs REST. O HSTS entra junto com o HTTPS, na borda.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers.CacheControl = "no-store"; // dados financeiros não devem ficar em caches intermediários
            return Task.CompletedTask;
        });

        if (context.Request.ContentLength > TamanhoMaximoDoCorpo)
        {
            await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"O corpo da requisição deve ter no máximo {TamanhoMaximoDoCorpo / 1024} KB.").ExecuteAsync(context);
            return;
        }

        // Vale também para corpos enviados sem Content-Length (chunked), quando a API roda no Kestrel.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
            limite.MaxRequestBodySize = TamanhoMaximoDoCorpo;

        await next(context);
    }

    private static FixedWindowRateLimiterOptions PorSegundo(int limite) => new()
    {
        PermitLimit = limite,
        Window = TimeSpan.FromSeconds(1),
        QueueLimit = 0
    };

    // Falha na inicialização se a configuração estiver errada, em vez de subir a API aberta ou quebrada.
    private static void ValidarClientes(List<ClienteDaApi> clientes)
    {
        if (clientes.Count == 0)
            throw new InvalidOperationException("Configure ao menos um cliente em Seguranca:Clientes.");
        if (clientes.Any(c => string.IsNullOrWhiteSpace(c.Nome) || c.LimitePorSegundo <= 0))
            throw new InvalidOperationException("Todo cliente precisa de Nome e de LimitePorSegundo maior que zero.");
        if (clientes.Any(c => c.ChaveSha256.Length != 64 || !c.ChaveSha256.All(char.IsAsciiHexDigit)))
            throw new InvalidOperationException("ChaveSha256 deve ser o SHA-256 da chave em hexadecimal, com 64 caracteres.");
        if (clientes.Select(c => c.Nome).Distinct().Count() != clientes.Count
            || clientes.Select(c => c.ChaveSha256.ToLowerInvariant()).Distinct().Count() != clientes.Count)
            throw new InvalidOperationException("Os nomes e as chaves dos clientes devem ser únicos.");
    }
}
