# Resultado do teste de carga

Executado em 2026-10-06T01:16:01.736Z com k6, passando pelo nginx, com 2 réplicas de cada API. Pico de 5m e folga de 2m.

| Cenário | Taxa alvo | Requisições | Perda | Média | p95 | Máximo | Thresholds |
|---|---|---|---|---|---|---|---|
| pico | 50 req/s GET | 15001 | 0.00% | 1.3 ms | 2.3 ms | 18.2 ms | OK |
| folga | 150 req/s GET | 18000 | 0.00% | 1.0 ms | 2.0 ms | 8.7 ms | OK |
| escritas | 10 req/s POST | 3000 | 0.00% | 3.6 ms | 4.5 ms | 24.8 ms | OK |
| relatorio | 10 req/s GET 30 dias | 3001 | 0.00% | 2.4 ms | 3.7 ms | 20.7 ms | OK |

Iterações descartadas pelo k6 por falta de VUs: 0.

Perda é qualquer resposta fora de 2xx, timeout acima de 2 s ou conexão recusada.
Thresholds: perda abaixo de 5% no pico, na folga e no relatório, abaixo de 1% nas escritas.
Latência p95 abaixo de 200 ms no pico e no relatório, e abaixo de 500 ms na folga e nas escritas.
