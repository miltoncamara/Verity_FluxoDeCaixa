# ADR 0003: Transactional outbox no mesmo banco dos lançamentos

**Status:** aceito
**Data:** 2026-10-05

## Contexto

Ao registrar um lançamento, duas coisas precisam acontecer: gravar o lançamento no banco e publicar o evento no RabbitMQ. Não existe transação que englobe os dois.

- Se a API gravar e depois publicar, uma queda entre os dois passos deixa um lançamento sem evento. O saldo fica errado para sempre.
- Se a API publicar e depois gravar, uma falha na gravação deixa um evento de um lançamento que não existe.
- Se a API publicar dentro do POST, o RabbitMQ fora do ar derruba o registro de lançamentos.

## Decisão

Usar o padrão transactional outbox no próprio banco de lançamentos.

1. O POST grava o lançamento e uma linha na tabela `outbox`, com o evento serializado em JSON, **no mesmo `SaveChanges`**. O EF Core executa isso numa única transação.
2. Só depois do commit a API responde `201`.
3. Um `BackgroundService` (`OutboxPublisher`) lê os eventos pendentes a cada 500 ms, em ordem de criação, e publica um de cada vez.
4. Cada publicação usa mensagem persistente, publisher confirms e a flag `mandatory`. O evento só é marcado como publicado (`publicado_em`) depois da confirmação do broker.
5. Se o RabbitMQ estiver fora, ou se não houver fila para receber a mensagem, o publicador apenas loga e tenta de novo em 5 s.
6. Eventos publicados há mais de 7 dias são apagados a cada hora.

## Consequências

**Positivas**

- Garante a invariante central: um lançamento confirmado sempre tem o seu evento, e o evento sempre chega ao broker.
- O RabbitMQ fora do ar não afeta o POST. No teste de caos, 85 eventos ficaram na outbox e foram entregues depois.
- A flag `mandatory` evita perder eventos publicados antes de o consumidor criar a fila. Sem ela, o broker confirmaria uma mensagem que nenhuma fila recebeu.

**Negativas**

- Entrega at-least-once. Se o publicador cair depois do publish e antes de marcar o evento, ele publica de novo. O consumidor idempotente absorve isso.
- Atraso de até 500 ms entre o commit e a publicação.
- Com várias réplicas da Lancamentos.Api, cada uma roda o seu publicador. Para que só uma publique por vez, sem duplicar eventos, o publicador usa um advisory lock do PostgreSQL ([ADR 0008](0008-escalabilidade-horizontal.md)).
- A tabela `outbox` cresce até a limpeza. Por isso existem a limpeza periódica e o índice parcial só para os pendentes.
