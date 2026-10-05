namespace Consolidado.Tests.Integracao;

/// <summary>
/// O consumo de mensagens é assíncrono, então os testes esperam a condição ficar verdadeira
/// em vez de usar um sleep fixo.
/// </summary>
public static class Aguardar
{
    public static async Task AteQue(Func<Task<bool>> condicao, string descricao, TimeSpan? limite = null)
    {
        var prazo = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < prazo)
        {
            if (await condicao())
                return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Condição não atendida a tempo: {descricao}");
    }
}
