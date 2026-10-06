# Resultado do teste de caos

Executado em 2026-10-05 com `bash scripts/caos.sh`, em Windows 11 com Docker Desktop. Cada API roda com 2 réplicas atrás do nginx.

O script envia um lançamento a cada 100 ms durante todo o teste. No meio do envio, ele para uma réplica da Lancamentos.Api e sobe de novo, como num deploy. Depois mata a Consolidado.Api inteira e, em seguida, o RabbitMQ. Por fim sobe tudo e compara o saldo consolidado com a soma dos lançamentos confirmados.

```
[21:29:01] Subindo a solução com docker compose
[21:29:04] Lancamentos.Api saudável
[21:29:04] Consolidado.Api saudável
[21:29:04] Enviando lançamentos continuamente para o dia 2147-10-19
[21:29:14] >>> Parando uma réplica da Lancamentos.Api (a outra continua atendendo)
[21:29:25] <<< Subindo a réplica de novo
[21:29:31] >>> Derrubando a Consolidado.Api (kill)
[21:29:47] >>> Derrubando o RabbitMQ (kill). Lançamentos e Consolidado.Api fora ao mesmo tempo
[21:30:03] Eventos aguardando na outbox com o RabbitMQ fora: 86
[21:30:03] <<< Subindo o RabbitMQ
[21:30:14] <<< Subindo a Consolidado.Api
[21:30:20] Consolidado.Api saudável
[21:30:30] POSTs enviados: 442. Falhas: 0. Saldo esperado: 1017785 centavos
[21:30:30] Aguardando o consolidado convergir (até 120s)

================ RESULTADO DO TESTE DE CAOS ================
Dia testado:                       2147-10-19
POSTs enviados:                    442
POSTs com falha:                   0
Lançamentos gravados na origem:    442
Eventos pendentes durante a queda: 86
Saldo esperado (centavos):         1017785
Saldo consolidado (centavos):      1017785
Mensagens na dead letter queue:    0
============================================================
PASSOU: nenhum lançamento falhou e o consolidado convergiu para o valor correto.
```

## O que o resultado mostra

- Nenhum dos 442 POSTs falhou, mesmo com uma réplica parada e, depois, com a Consolidado.Api e o RabbitMQ fora ao mesmo tempo.
- Com uma réplica da Lancamentos.Api parada, o nginx mandou os POSTs para a outra réplica.
- Com o RabbitMQ fora, 86 eventos ficaram guardados na outbox. Nenhum se perdeu.
- Depois da volta dos serviços, o saldo consolidado ficou igual, centavo por centavo, à soma dos lançamentos confirmados.
- A dead letter queue ficou vazia. Nenhuma mensagem foi descartada.

## Um problema que este teste encontrou

Na primeira execução com réplicas, 2 POSTs ficaram sem resposta. Quando uma réplica para, o seu IP some da rede, mas o nginx ainda o guarda por alguns segundos. Uma conexão para um IP que não existe não é recusada. Ela fica pendurada até o timeout de conexão, que por padrão é de 60 s.

A correção foi configurar `proxy_connect_timeout 1s` no nginx. Na mesma rede uma conexão leva menos de 1 ms, então depois de 1 s o nginx desiste e tenta a outra réplica. Depois disso o teste passou em todas as execuções.
