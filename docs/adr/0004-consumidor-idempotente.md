# ADR 0004: Consumidor idempotente com ack após o commit

**Status:** aceito
**Data:** 2026-10-05

## Contexto

A entrega dos eventos é at-least-once. O mesmo evento pode chegar mais de uma vez quando:

- o publicador cai entre o publish e a marcação na outbox,
- o consumidor cai depois do commit e antes do ack,
- o RabbitMQ reentrega mensagens sem ack depois de uma queda.

Somar o mesmo lançamento duas vezes deixaria o saldo errado. Além disso, vários eventos do mesmo dia podem ser processados ao mesmo tempo.

## Decisão

O consumidor aplica cada evento numa única transação no banco do consolidado:

1. `INSERT INTO eventos_processados (evento_id) ... ON CONFLICT DO NOTHING`. Se nenhuma linha for inserida, o evento já foi aplicado. A transação é desfeita e a mensagem recebe ack.
2. `INSERT INTO saldo_diario ... ON CONFLICT (data) DO UPDATE SET total = saldo_diario.total + EXCLUDED.total`. O banco soma sobre o valor atual da linha, então atualizações simultâneas do mesmo dia nunca se perdem.
3. `COMMIT`, e só então o ack manual para o RabbitMQ.

O Id do evento é o Id da linha da outbox, gerado uma única vez na origem.

O tratamento de erros separa três casos:

| Caso | Ação |
|---|---|
| Mensagem inválida | `basic.reject` sem requeue. Vai direto para a DLQ |
| Banco do consolidado fora | Mantém a mensagem sem ack e tenta de novo a cada 5 s. Não conta como tentativa |
| Erro inesperado | `basic.reject` com requeue. A quorum queue conta as entregas e, depois de 5, move a mensagem para a DLQ |

O consumidor usa `basic.reject` e não `basic.nack` porque, desde o RabbitMQ 4.3, a quorum queue só conta para o `x-delivery-limit` as devoluções feitas com `reject`. Com `nack`, uma mensagem com erro voltaria para a fila sem parar e nunca chegaria à DLQ. Um teste de integração confere que ela chega à DLQ depois do limite.

## Consequências

**Positivas**

- Reentregas e duplicatas são inofensivas. O reprocessamento da DLQ também é seguro.
- Nenhuma atualização concorrente se perde, sem lock explícito no código.
- Uma mensagem com problema não trava a fila.
- Uma queda do banco não manda mensagens boas para a DLQ.

**Negativas**

- A tabela `eventos_processados` cresce com o volume de eventos. Em produção ela precisaria de uma retenção, por exemplo apagar registros mais antigos que o maior tempo possível de reentrega.
- A atualização do saldo usa SQL escrito à mão, e não o change tracking do EF Core. Isso é intencional, porque o `ON CONFLICT` é o que garante a atomicidade.
