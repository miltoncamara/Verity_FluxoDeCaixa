# Registros de decisão de arquitetura

Cada ADR registra uma decisão, o contexto em que ela foi tomada e as suas consequências.

| ADR | Decisão |
|---|---|
| [0001](0001-dois-servicos.md) | Dois serviços em vez de um monolito |
| [0002](0002-comunicacao-assincrona.md) | Comunicação apenas assíncrona, por eventos no RabbitMQ |
| [0003](0003-outbox-no-mesmo-banco.md) | Transactional outbox no mesmo banco dos lançamentos |
| [0004](0004-consumidor-idempotente.md) | Consumidor idempotente com ack após o commit |
| [0005](0005-saldo-pre-calculado.md) | Saldo diário pré-calculado e cache em memória |
| [0006](0006-lancamentos-imutaveis.md) | Lançamentos imutáveis |
| [0007](0007-simplificacoes-assumidas.md) | Simplificações assumidas |
| [0008](0008-escalabilidade-horizontal.md) | Escalabilidade horizontal com réplicas, balanceador e publicador único |
