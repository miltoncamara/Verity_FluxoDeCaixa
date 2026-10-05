namespace Lancamentos.Tests.Integracao;

/// <summary>
/// Agrupa os testes que compartilham a mesma LancamentosApiFactory (e o mesmo container PostgreSQL).
/// </summary>
[CollectionDefinition(Nome)]
public sealed class LancamentosApiCollection : ICollectionFixture<LancamentosApiFactory>
{
    public const string Nome = "Lancamentos.Api";
}
