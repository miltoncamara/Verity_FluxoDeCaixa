namespace Lancamentos.Api.Domain;

/// <summary>
/// Um lançamento de fluxo de caixa. É imutável: não existe update nem delete.
/// Correção de um lançamento errado será feita por estorno (evolução futura).
/// </summary>
public sealed class Lancamento
{
    public const int DescricaoTamanhoMaximo = 200;
    public const int IdempotencyKeyTamanhoMaximo = 100;
    public const int CriadoPorTamanhoMaximo = 100;
    public const decimal ValorMaximo = 9_999_999_999_999_999.99m; // limite da coluna numeric(18,2)

    public Guid Id { get; private init; }
    public DateOnly Data { get; private init; }
    public TipoLancamento Tipo { get; private init; }
    public decimal Valor { get; private init; }
    public string Descricao { get; private init; } = "";
    public DateTimeOffset CriadoEm { get; private init; }
    /// <summary>
    /// Chave enviada pelo cliente no header Idempotency-Key. Identifica o pedido: repetir o pedido com a
    /// mesma chave devolve o lançamento original. É opcional só nos lançamentos antigos, anteriores à regra.
    /// </summary>
    public string? IdempotencyKey { get; private init; }

    /// <summary>Cliente da API que registrou o lançamento, para auditoria.</summary>
    public string CriadoPor { get; private init; } = "";

    // Usado pelo EF Core para materializar a entidade.
    private Lancamento() { }

    /// <summary>
    /// Cria um lançamento válido ou devolve os erros de validação por campo.
    /// </summary>
    public static (Lancamento? Lancamento, Dictionary<string, string> Erros) Criar(
        DateOnly? data, string? tipo, decimal? valor, string? descricao, string criadoPor, string? idempotencyKey)
    {
        // Quem registrou vem da autenticação, não do cliente. Se faltar, é erro de programação.
        ArgumentException.ThrowIfNullOrWhiteSpace(criadoPor);

        var erros = new Dictionary<string, string>();

        if (data is null)
            erros["data"] = "Data é obrigatória.";

        var tipoConvertido = ConverterTipo(tipo);
        if (tipoConvertido is null)
            erros["tipo"] = "Tipo deve ser 'Credito' ou 'Debito'.";

        if (valor is null || valor <= 0)
            erros["valor"] = "Valor deve ser maior que zero.";
        else if (decimal.Round(valor.Value, 2) != valor.Value)
            erros["valor"] = "Valor deve ter no máximo 2 casas decimais.";
        else if (valor > ValorMaximo)
            erros["valor"] = $"Valor deve ser no máximo {ValorMaximo}.";

        if (string.IsNullOrWhiteSpace(descricao))
            erros["descricao"] = "Descrição é obrigatória.";
        else if (descricao.Trim().Length > DescricaoTamanhoMaximo)
            erros["descricao"] = $"Descrição deve ter no máximo {DescricaoTamanhoMaximo} caracteres.";

        // A chave é obrigatória: só o cliente sabe se um pedido é uma venda nova ou a repetição de um
        // pedido que ficou sem resposta. Duas vendas iguais no mesmo dia são legítimas e comuns.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            erros["idempotencyKey"] = "O header Idempotency-Key é obrigatório.";
        else if (idempotencyKey.Length > IdempotencyKeyTamanhoMaximo)
            erros["idempotencyKey"] = $"Idempotency-Key deve ter no máximo {IdempotencyKeyTamanhoMaximo} caracteres.";

        if (erros.Count > 0)
            return (null, erros);

        var lancamento = new Lancamento
        {
            Id = Guid.CreateVersion7(),
            Data = data!.Value,
            Tipo = tipoConvertido!.Value,
            Valor = valor!.Value,
            Descricao = descricao!.Trim(),
            CriadoEm = AgoraEmMicrossegundos(),
            IdempotencyKey = idempotencyKey,
            CriadoPor = criadoPor
        };
        return (lancamento, erros);
    }

    /// <summary>
    /// Diz se outro lançamento descreve a mesma operação: mesma data, tipo, valor e descrição.
    /// Usado quando um pedido chega com uma Idempotency-Key já usada. Se o conteúdo for o mesmo, é uma
    /// repetição legítima. Se for diferente, o cliente reaproveitou a chave por engano.
    /// </summary>
    public bool TemOMesmoConteudoQue(Lancamento outro) =>
        Data == outro.Data && Tipo == outro.Tipo && Valor == outro.Valor && Descricao == outro.Descricao;

    // O PostgreSQL guarda timestamps com precisão de microssegundos. Truncar aqui garante que a resposta
    // do POST e a resposta de uma repetição com a mesma Idempotency-Key sejam idênticas.
    private static DateTimeOffset AgoraEmMicrossegundos()
    {
        var agora = DateTimeOffset.UtcNow;
        return agora.AddTicks(-(agora.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static TipoLancamento? ConverterTipo(string? tipo) => tipo?.Trim().ToLowerInvariant() switch
    {
        "credito" => TipoLancamento.Credito,
        "debito" => TipoLancamento.Debito,
        _ => null
    };
}
