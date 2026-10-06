namespace Lancamentos.Tests.Integracao;

/// <summary>
/// Collection separada, com o seu próprio container, porque o teste derruba o banco de lançamentos.
/// </summary>
[CollectionDefinition(Nome)]
public sealed class LancamentosBancoForaCollection : ICollectionFixture<LancamentosApiFactory>
{
    public const string Nome = "Lancamentos.Api com banco fora do ar";
}
