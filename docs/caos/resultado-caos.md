# Resultado do teste de caos

Executado em 2026-10-05 com `bash scripts/caos.sh`, em Windows 11 com Docker Desktop.

O script envia um lançamento a cada 100 ms durante todo o teste. No meio do envio, ele mata a Consolidado.Api e, em seguida, o RabbitMQ. Depois sobe os dois de novo e compara o saldo consolidado com a soma dos lançamentos confirmados.

```
[20:39:50] Subindo a solução com docker compose
[20:39:53] Lancamentos.Api saudável
[20:39:53] Consolidado.Api saudável
[20:39:53] Enviando lançamentos continuamente para o dia 2193-07-23
[20:40:03] >>> Derrubando a Consolidado.Api (kill)
[20:40:19] >>> Derrubando o RabbitMQ (kill). Lançamentos e Consolidado.Api fora ao mesmo tempo
[20:40:36] Eventos aguardando na outbox com o RabbitMQ fora: 85
[20:40:36] <<< Subindo o RabbitMQ
[20:40:47] <<< Subindo a Consolidado.Api
[20:40:51] Consolidado.Api saudável
[20:41:02] POSTs enviados: 326. Falhas: 0. Saldo esperado: 731597 centavos
[20:41:03] Aguardando o consolidado convergir (até 120s)

================ RESULTADO DO TESTE DE CAOS ================
Dia testado:                       2193-07-23
POSTs enviados:                    326
POSTs com falha:                   0
Lançamentos gravados na origem:    326
Eventos pendentes durante a queda: 85
Saldo esperado (centavos):         731597
Saldo consolidado (centavos):      731597
Mensagens na dead letter queue:    0
============================================================
PASSOU: nenhum lançamento falhou e o consolidado convergiu para o valor correto.
```

## O que o resultado mostra

- Nenhum dos 326 POSTs falhou, mesmo com a Consolidado.Api e o RabbitMQ fora ao mesmo tempo.
- Com o RabbitMQ fora, 85 eventos ficaram guardados na outbox. Nenhum se perdeu.
- Depois da volta dos serviços, o saldo consolidado ficou igual, centavo por centavo, à soma dos lançamentos confirmados.
- A dead letter queue ficou vazia. Nenhuma mensagem foi descartada.
