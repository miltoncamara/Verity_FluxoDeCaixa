namespace Consolidado.Tests.Integracao;

/// <summary>
/// Agrupa os testes que compartilham a mesma ConsolidadoApiFactory (e os mesmos containers).
/// </summary>
[CollectionDefinition(Nome)]
public sealed class ConsolidadoApiCollection : ICollectionFixture<ConsolidadoApiFactory>
{
    public const string Nome = "Consolidado.Api";
}
