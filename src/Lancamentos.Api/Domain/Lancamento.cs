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
    public string? IdempotencyKey { get; private init; }

    /// <summary>Cliente da API que registrou o lançamento, para auditoria.</summary>
    public string CriadoPor { get; private init; } = "";

    // Usado pelo EF Core para materializar a entidade.
    private Lancamento() { }

    /// <summary>
    /// Cria um lançamento válido ou devolve os erros de validação por campo.
    /// </summary>
    public static (Lancamento? Lancamento, Dictionary<string, string> Erros) Criar(
        DateOnly? data, string? tipo, decimal? valor, string? descricao, string criadoPor, string? idempotencyKey = null)
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

        if (idempotencyKey is not null && (idempotencyKey.Length == 0 || idempotencyKey.Length > IdempotencyKeyTamanhoMaximo))
            erros["idempotencyKey"] = $"Idempotency-Key deve ter entre 1 e {IdempotencyKeyTamanhoMaximo} caracteres.";

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
