using Lancamentos.Api.Domain;

namespace Lancamentos.Tests.Domain;

public class LancamentoTests
{
    private static readonly DateOnly Hoje = new(2026, 10, 5);
    private const string CriadoPor = "pdv-loja-1";
    private const string Chave = "venda-0001";

    [Theory]
    [InlineData("Credito", TipoLancamento.Credito)]
    [InlineData("debito", TipoLancamento.Debito)]
    [InlineData(" CREDITO ", TipoLancamento.Credito)]
    public void Criar_com_dados_validos_devolve_lancamento(string tipo, TipoLancamento esperado)
    {
        var (lancamento, erros) = Lancamento.Criar(Hoje, tipo, 150.75m, "  Venda balcão  ", CriadoPor, Chave);

        Assert.Empty(erros);
        Assert.NotNull(lancamento);
        Assert.NotEqual(Guid.Empty, lancamento.Id);
        Assert.Equal(Hoje, lancamento.Data);
        Assert.Equal(esperado, lancamento.Tipo);
        Assert.Equal(150.75m, lancamento.Valor);
        Assert.Equal("Venda balcão", lancamento.Descricao);
        Assert.Equal(CriadoPor, lancamento.CriadoPor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Valor_zero_ou_negativo_e_invalido(decimal valor)
    {
        var (lancamento, erros) = Lancamento.Criar(Hoje, "Credito", valor, "Venda", CriadoPor, Chave);

        Assert.Null(lancamento);
        Assert.Contains("valor", erros.Keys);
    }

    [Fact]
    public void Valor_com_mais_de_duas_casas_decimais_e_invalido()
    {
        var (lancamento, erros) = Lancamento.Criar(Hoje, "Credito", 10.123m, "Venda", CriadoPor, Chave);

        Assert.Null(lancamento);
        Assert.Contains("valor", erros.Keys);
    }

    [Fact]
    public void Valor_ausente_e_invalido()
    {
        var (_, erros) = Lancamento.Criar(Hoje, "Credito", null, "Venda", CriadoPor, Chave);

        Assert.Contains("valor", erros.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Descricao_vazia_e_invalida(string? descricao)
    {
        var (lancamento, erros) = Lancamento.Criar(Hoje, "Debito", 10m, descricao, CriadoPor, Chave);

        Assert.Null(lancamento);
        Assert.Contains("descricao", erros.Keys);
    }

    [Fact]
    public void Descricao_acima_do_tamanho_maximo_e_invalida()
    {
        var descricao = new string('x', Lancamento.DescricaoTamanhoMaximo + 1);

        var (_, erros) = Lancamento.Criar(Hoje, "Debito", 10m, descricao, CriadoPor, Chave);

        Assert.Contains("descricao", erros.Keys);
    }

    [Fact]
    public void Descricao_no_tamanho_maximo_e_valida()
    {
        var descricao = new string('x', Lancamento.DescricaoTamanhoMaximo);

        var (lancamento, _) = Lancamento.Criar(Hoje, "Debito", 10m, descricao, CriadoPor, Chave);

        Assert.NotNull(lancamento);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Transferencia")]
    [InlineData("1")]
    public void Tipo_invalido_e_rejeitado(string? tipo)
    {
        var (lancamento, erros) = Lancamento.Criar(Hoje, tipo, 10m, "Venda", CriadoPor, Chave);

        Assert.Null(lancamento);
        Assert.Contains("tipo", erros.Keys);
    }

    [Fact]
    public void Data_ausente_e_invalida()
    {
        var (_, erros) = Lancamento.Criar(null, "Credito", 10m, "Venda", CriadoPor, Chave);

        Assert.Contains("data", erros.Keys);
    }

    [Fact]
    public void Idempotency_key_acima_do_tamanho_maximo_e_invalida()
    {
        var chave = new string('k', Lancamento.IdempotencyKeyTamanhoMaximo + 1);

        var (_, erros) = Lancamento.Criar(Hoje, "Credito", 10m, "Venda", CriadoPor, chave);

        Assert.Contains("idempotencyKey", erros.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Criar_sem_saber_quem_registrou_e_erro_de_programacao(string? criadoPor)
    {
        Assert.ThrowsAny<ArgumentException>(() => Lancamento.Criar(Hoje, "Credito", 10m, "Venda", criadoPor!, Chave));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Idempotency_key_e_obrigatoria(string? chave)
    {
        var (lancamento, erros) = Lancamento.Criar(Hoje, "Credito", 10m, "Venda", CriadoPor, chave);

        Assert.Null(lancamento);
        Assert.Contains("idempotencyKey", erros.Keys);
    }

    [Fact]
    public void Repeticao_com_o_mesmo_conteudo_e_reconhecida()
    {
        var (original, _) = Lancamento.Criar(Hoje, "Credito", 5m, "Café", CriadoPor, Chave);
        var (repeticao, _) = Lancamento.Criar(Hoje, "credito", 5.00m, "  Café  ", CriadoPor, Chave);

        // Diferenças só de formato (maiúsculas no tipo, zeros no valor, espaços na descrição) não contam.
        Assert.True(original!.TemOMesmoConteudoQue(repeticao!));
    }

    [Theory]
    [InlineData("Debito", 5, "Café")]
    [InlineData("Credito", 6, "Café")]
    [InlineData("Credito", 5, "Pão de queijo")]
    public void Conteudo_diferente_e_reconhecido(string tipo, decimal valor, string descricao)
    {
        var (original, _) = Lancamento.Criar(Hoje, "Credito", 5m, "Café", CriadoPor, Chave);
        var (outro, _) = Lancamento.Criar(Hoje, tipo, valor, descricao, CriadoPor, Chave);

        Assert.False(original!.TemOMesmoConteudoQue(outro!));
    }

    [Fact]
    public void Data_diferente_e_conteudo_diferente()
    {
        var (original, _) = Lancamento.Criar(Hoje, "Credito", 5m, "Café", CriadoPor, Chave);
        var (outroDia, _) = Lancamento.Criar(Hoje.AddDays(1), "Credito", 5m, "Café", CriadoPor, Chave);

        Assert.False(original!.TemOMesmoConteudoQue(outroDia!));
    }

    [Fact]
    public void Varios_erros_sao_devolvidos_juntos()
    {
        var (lancamento, erros) = Lancamento.Criar(null, "x", -1m, "", CriadoPor, Chave);

        Assert.Null(lancamento);
        Assert.Equal(["data", "tipo", "valor", "descricao"], erros.Keys);
    }
}
