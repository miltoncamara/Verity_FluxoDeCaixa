namespace Lancamentos.Tests.Integracao;

/// <summary>
/// Gera uma Idempotency-Key nova para todo POST de teste que não traga uma, porque cada POST de teste
/// representa um lançamento novo. Os testes que tratam da chave enviam a sua explicitamente.
/// </summary>
public sealed class IdempotencyKeyAutomatica : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && !request.Headers.Contains("Idempotency-Key"))
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        return base.SendAsync(request, cancellationToken);
    }
}
