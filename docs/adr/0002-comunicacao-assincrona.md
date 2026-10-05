# ADR 0002: Comunicação apenas assíncrona, por eventos no RabbitMQ

**Status:** aceito
**Data:** 2026-10-05

## Contexto

O consolidado precisa saber de cada lançamento registrado. Existem duas formas de levar essa informação:

- **Chamada síncrona.** A Lancamentos.Api chama a Consolidado.Api por HTTP a cada lançamento. Se o consolidado estiver fora ou lento, o POST do lançamento falha ou fica lento. Isso viola o requisito principal.
- **Evento assíncrono.** A Lancamentos.Api publica um evento num broker, e o consolidado consome quando puder.

## Decisão

Os serviços se comunicam **somente** por eventos assíncronos no RabbitMQ. Não existe nenhuma chamada HTTP entre eles, em nenhum sentido.

- O evento `LancamentoRegistrado` é publicado no exchange `lancamentos` (topic) com a routing key `lancamento.registrado`.
- O consolidado consome a fila `consolidado.lancamentos`, uma quorum queue durável.
- O cliente usa `RabbitMQ.Client` diretamente, sem MassTransit ou similares. O código de publicação e consumo fica explícito e curto, o que facilita explicar cada garantia.

## Consequências

**Positivas**

- O registro de lançamentos não depende da disponibilidade nem da velocidade do consolidado.
- O broker absorve picos. O consolidado processa no seu ritmo.
- Novos consumidores, como um serviço de relatórios, podem ser ligados ao mesmo exchange sem mudar a Lancamentos.Api.

**Negativas**

- Consistência eventual entre os serviços.
- A entrega é at-least-once, então o consumidor precisa ser idempotente ([ADR 0004](0004-consumidor-idempotente.md)).
- Publicar no broker depois de gravar no banco cria o risco de gravar sem publicar. Isso é resolvido pela outbox ([ADR 0003](0003-outbox-no-mesmo-banco.md)).
- O código trata reconexão e topologia manualmente, o que um framework faria sozinho. A troca foi feita por clareza.
