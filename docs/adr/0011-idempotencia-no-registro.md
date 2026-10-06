# ADR 0011: Idempotência no registro de lançamentos

**Status:** aceito
**Data:** 2026-10-06

## Contexto

Um POST pode falhar sem que o cliente saiba se o lançamento foi gravado. Isso acontece num timeout de rede, numa queda da API depois do commit e antes da resposta, ou quando o banco cai no meio do pedido. O cliente precisa poder repetir o pedido sem o risco de registrar a mesma venda duas vezes.

Uma alternativa seria o backend detectar a repetição sozinho, por um hash do conteúdo do pedido. Isso não funciona no fluxo de caixa: duas vendas iguais no mesmo dia, como dois cafés de R$ 5,00, têm exatamente o mesmo conteúdo e são duas vendas legítimas. Com o hash como chave, a segunda seria descartada e o caixa ficaria errado.

Há também o caso do banco fora. As retentativas do EF Core seguram o pedido por cerca de 1 minuto antes de desistir, e o nginx responde `504` antes disso. Com vários pedidos chegando, eles se acumulam presos na API.

## Decisão

**A chave vem do cliente e é obrigatória.**

- O header `Idempotency-Key` é exigido em todo `POST /lancamentos`. Sem ele, a resposta é `400`.
- Só o cliente sabe se um pedido é uma venda nova ou a repetição de um pedido que ficou sem resposta. Na prática a chave costuma ser um identificador que o sistema de origem já tem, como o número do cupom ou o Id da venda no PDV.
- É o mesmo modelo de Stripe, Adyen e PayPal, e da proposta de padrão da IETF para o header `Idempotency-Key`.

**A chave é única por cliente.** O índice único é `(criado_por, idempotency_key)`. Dois clientes podem usar a mesma chave sem conflito ([ADR 0009](0009-seguranca.md)).

**O conteúdo é conferido.**

| Pedido | Resposta |
|---|---|
| Chave nova | `201`: grava o lançamento |
| Mesma chave e mesmo conteúdo | `200`: repetição legítima, devolve o lançamento original |
| Mesma chave e conteúdo diferente | `422`: o cliente reaproveitou a chave por engano, e isso não pode passar em silêncio |

O conteúdo é comparado com o lançamento já gravado (data, tipo, valor e descrição), depois da normalização feita pela entidade. Diferenças só de formato, como `5` e `5.00` ou espaços na descrição, não contam. Não é preciso guardar um hash: o próprio lançamento gravado serve de referência.

**Falha rápida com o banco fora.** A gravação tem limite de 5 segundos. Se o banco não responder, o POST devolve `503` com `Retry-After` e a orientação de repetir com a mesma chave.

- Uma oscilação curta ainda é absorvida pelas retentativas do EF Core dentro desse limite.
- Uma queda de verdade falha rápido e não acumula pedidos presos na API.
- Se o commit tiver acontecido antes do limite, a repetição com a mesma chave devolve o original. É isso que torna o limite seguro, e é por isso que a chave precisa ser obrigatória.

## Consequências

**Positivas**

- Repetir um pedido é sempre seguro, inclusive depois de um `503` ou de um timeout.
- Duas vendas iguais são registradas como duas vendas.
- Um bug do cliente que reaproveita chaves aparece como `422`, em vez de esconder uma venda.
- Com o banco fora, o cliente recebe uma resposta clara em cerca de 5 segundos, em vez de esperar 1 minuto. Isso também importa na Azure, onde o failover do PostgreSQL leva de 1 a 2 minutos.

**Negativas**

- Quem integra precisa gerar e guardar uma chave por venda.
- As chaves ficam guardadas para sempre junto com o lançamento. Em produção, uma janela de validade para a chave (por exemplo, 24 horas) reduziria o índice, ao custo de deixar de reconhecer repetições muito antigas.
