using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Consolidado.Api.Seguranca;

/// <summary>
/// Autentica o cliente pelo header X-Api-Key e transforma as suas permissões em claims.
/// As regras de acesso ficam nas policies de autorização, então trocar a API Key por JWT
/// do Entra ID muda só este esquema de autenticação. Os endpoints continuam iguais.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IReadOnlyList<ClienteDaApi> clientes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Esquema = "ApiKey";
    public const string Header = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var chaveRecebida = Request.Headers[Header].ToString();
        if (chaveRecebida.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        // Compara com todas as chaves, sempre em tempo constante e sem parar na primeira que bate,
        // para não revelar pelo tempo de resposta se a chave existe ou quanto dela está certo.
        var recebida = Encoding.UTF8.GetBytes(chaveRecebida);
        ClienteDaApi? encontrado = null;
        foreach (var cliente in clientes)
        {
            if (CryptographicOperations.FixedTimeEquals(recebida, Encoding.UTF8.GetBytes(cliente.Chave)))
                encontrado = cliente;
        }

        if (encontrado is null)
            return Task.FromResult(AuthenticateResult.Fail("API Key inválida."));

        var claims = new List<Claim> { new(ClaimTypes.Name, encontrado.Nome) };
        claims.AddRange(encontrado.Permissoes.Select(p => new Claim(Permissoes.Claim, p)));
        var usuario = new ClaimsPrincipal(new ClaimsIdentity(claims, Esquema));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(usuario, Esquema)));
    }

    // 401: sem chave ou com chave inválida.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = Esquema;
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
            title: $"Header {Header} ausente ou inválido.").ExecuteAsync(Context);
    }

    // 403: chave válida, mas sem a permissão que o endpoint exige.
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "O cliente não tem permissão para esta operação.").ExecuteAsync(Context);
}
