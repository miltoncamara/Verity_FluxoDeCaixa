namespace Lancamentos.Tests.Integracao;

/// <summary>
/// Agrupa os testes que compartilham a LancamentosComRabbitMqFactory (PostgreSQL e RabbitMQ reais).
/// </summary>
[CollectionDefinition(Nome)]
public sealed class LancamentosComRabbitMqCollection : ICollectionFixture<LancamentosComRabbitMqFactory>
{
    public const string Nome = "Lancamentos.Api com RabbitMQ";
}
