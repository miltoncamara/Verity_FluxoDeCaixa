using Consolidado.Api.Domain;
using Contracts;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Api.Data;

/// <summary>
/// Aplica um evento LancamentoRegistrado ao saldo do dia, de forma idempotente e atômica.
/// </summary>
public sealed class AtualizadorDeSaldo(ConsolidadoDbContext db)
{
    /// <returns>true se o evento foi aplicado agora, false se já tinha sido aplicado antes.</returns>
    public async Task<bool> AplicarAsync(LancamentoRegistrado evento, CancellationToken ct)
    {
        var (creditos, debitos) = SaldoDiario.ContribuicaoDe(evento.Tipo, evento.Valor);

        // Com EnableRetryOnFailure, uma transação aberta pelo código precisa rodar dentro da
        // execution strategy. Se houver falha transitória, o bloco inteiro é repetido.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transacao = await db.Database.BeginTransactionAsync(ct);

            // 1. Marca o evento como processado. Se ele já existir, nada é inserido e o evento é ignorado.
            //    Se outro consumidor estiver gravando o mesmo evento ao mesmo tempo, este INSERT espera
            //    o outro terminar e então cai no ON CONFLICT.
            var inseridos = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO eventos_processados (evento_id, processado_em)
                VALUES ({evento.EventoId}, now())
                ON CONFLICT (evento_id) DO NOTHING
                """, ct);

            if (inseridos == 0)
            {
                await transacao.RollbackAsync(ct);
                return false;
            }

            // 2. Soma o lançamento no saldo do dia numa única instrução. O banco faz a soma sobre o valor
            //    atual da linha, então duas atualizações simultâneas do mesmo dia nunca se perdem.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO saldo_diario (data, total_creditos, total_debitos, atualizado_em)
                VALUES ({evento.Data}, {creditos}, {debitos}, now())
                ON CONFLICT (data) DO UPDATE SET
                    total_creditos = saldo_diario.total_creditos + EXCLUDED.total_creditos,
                    total_debitos = saldo_diario.total_debitos + EXCLUDED.total_debitos,
                    atualizado_em = EXCLUDED.atualizado_em
                """, ct);

            // 3. As duas gravações são confirmadas juntas. Só depois disso a mensagem recebe ack.
            await transacao.CommitAsync(ct);
            return true;
        });
    }
}
