using System.Security.Cryptography;
using System.Text;

namespace Consolidado.Api.Seguranca;

/// <summary>
/// Exige o header X-Api-Key em todas as rotas, exceto /health.
/// A chave vem da configuração (variável de ambiente Seguranca__ApiKey) e nunca fica no repositório.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public const string Header = "X-Api-Key";

    // Falha na inicialização se a chave não estiver configurada, em vez de subir a API aberta.
    private readonly byte[] _chaveEsperada = Encoding.UTF8.GetBytes(
        configuration["Seguranca:ApiKey"] is { Length: > 0 } chave
            ? chave
            : throw new InvalidOperationException("Configure a API Key em Seguranca:ApiKey (variável de ambiente Seguranca__ApiKey)."));

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var chaveRecebida = Encoding.UTF8.GetBytes(context.Request.Headers[Header].ToString());

        // Comparação em tempo constante, para não vazar a chave pelo tempo de resposta.
        if (!CryptographicOperations.FixedTimeEquals(chaveRecebida, _chaveEsperada))
        {
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: $"Header {Header} ausente ou inválido.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
