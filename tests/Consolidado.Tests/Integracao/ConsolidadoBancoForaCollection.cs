namespace Consolidado.Tests.Integracao;

/// <summary>
/// Collection separada, com containers próprios, porque o teste derruba o banco do consolidado.
/// </summary>
[CollectionDefinition(Nome)]
public sealed class ConsolidadoBancoForaCollection : ICollectionFixture<ConsolidadoApiFactory>
{
    public const string Nome = "Consolidado.Api com banco fora do ar";
}
