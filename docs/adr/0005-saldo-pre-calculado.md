# ADR 0005: Saldo diário pré-calculado e cache em memória

**Status:** aceito
**Data:** 2026-10-05

## Contexto

O consolidado precisa responder 50 requisições por segundo em dias de pico, com no máximo 5% de perda. A forma óbvia de calcular o saldo é somar os lançamentos do dia a cada consulta. Isso tem dois problemas:

- O custo de cada consulta cresce com o número de lançamentos do dia, justamente nos dias de pico.
- O consolidado precisaria ler o banco de lançamentos, o que acoplaria os serviços ([ADR 0001](0001-dois-servicos.md)).

## Decisão

- O consolidado mantém a tabela `saldo_diario`, com uma linha por dia, contendo o total de créditos e o total de débitos. O saldo é créditos menos débitos.
- A tabela é atualizada a cada evento pelo consumidor ([ADR 0004](0004-consumidor-idempotente.md)).
- O `GET /consolidado/{data}` faz uma leitura pela chave primária. Ele nunca soma lançamentos. Um dia sem linha tem saldo zero.
- Cada leitura fica 5 s num cache em memória (`IMemoryCache`).
- A leitura no banco tem um limite de 2 s.
- O último valor lido de cada dia fica guardado por 24 h. Se o banco estiver fora, a API responde esse valor com os headers `X-Stale-Data: true` e `Age`. Se o dia nunca foi lido, responde `503` com `Retry-After`.

## Consequências

**Positivas**

- Custo constante por consulta, independente do volume do dia. O teste de carga mediu p95 de 2,1 ms a 50 req/s e 0% de perda a 150 req/s.
- A consulta continua respondendo com o banco do consolidado fora, para os dias já lidos.
- O limite de 2 s impede que as retentativas do EF Core prendam requisições por quase um minuto quando o banco cai.

**Negativas**

- O saldo pode estar até 5 s atrás do que já foi processado, além do atraso natural da fila.
- O cache é por instância. Com várias réplicas, um cache distribuído (Redis) seria mais eficiente e manteria o último valor conhecido entre restarts.
- O header `X-Stale-Data` não é padrão. O header `Warning`, que servia para isso, foi descontinuado pela RFC 9111. O `Age` que o acompanha é padrão.
