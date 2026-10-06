# ADR 0005: Saldo diário pré-calculado, relatório por período e cache em memória

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
- O `GET /consolidado?inicio=&fim=` é o relatório do período, com uma linha por dia: créditos, débitos, saldo do dia e saldo acumulado. Ele faz duas leituras na mesma tabela `saldo_diario`. A primeira soma as linhas diárias anteriores ao período para obter o saldo inicial. A segunda busca as linhas do período pela chave. Dias sem movimento entram zerados. O período tem no máximo 366 dias.
- **O saldo acumulado é calculado na leitura, e não gravado.** O cliente pode registrar um lançamento com data passada. Se o acumulado fosse gravado em cada dia, um lançamento atrasado obrigaria a reescrever todos os dias seguintes. Calculado na leitura, ele sempre reflete os saldos diários atuais.
- Cada leitura fica 5 s num cache em memória (`IMemoryCache`).
- A leitura no banco tem um limite de 2 s.
- O último valor lido de cada dia ou período fica guardado por 24 h. Se o banco estiver fora, a API responde esse valor com os headers `X-Stale-Data: true` e `Age`. Se o dia nunca foi lido, responde `503` com `Retry-After`.

## Consequências

**Positivas**

- Custo constante por consulta, independente do volume do dia. O teste de carga mediu p95 de 2,3 ms a 50 req/s, 0% de perda a 150 req/s e p95 de 3,7 ms no relatório de 30 dias.
- O saldo inicial do relatório soma no máximo uma linha por dia, e não os lançamentos. Mesmo com 10 anos de histórico são cerca de 3.650 linhas pequenas, lidas pela chave primária.
- Um lançamento com data passada corrige automaticamente o acumulado de todos os dias seguintes, sem nenhuma rotina de recálculo.
- A consulta continua respondendo com o banco do consolidado fora, para os dias e períodos já lidos.
- O limite de 2 s impede que as retentativas do EF Core prendam requisições por quase um minuto quando o banco cai.

**Negativas**

- O saldo pode estar até 5 s atrás do que já foi processado, além do atraso natural da fila.
- O cache é por instância. Localmente, o nginx manda cada data sempre para a mesma réplica para compensar isso ([ADR 0008](0008-escalabilidade-horizontal.md)). Em produção, um cache distribuído (Redis) manteria o último valor conhecido entre réplicas e restarts.
- O header `X-Stale-Data` não é padrão. O header `Warning`, que servia para isso, foi descontinuado pela RFC 9111. O `Age` que o acompanha é padrão.
