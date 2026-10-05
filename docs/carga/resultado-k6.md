# Resultado do teste de carga

Executado em 2026-10-05T23:39:43.445Z com k6. Pico de 5m e folga de 2m.

| Cenário | Taxa alvo | Requisições | Perda | Média | p95 | Máximo | Thresholds |
|---|---|---|---|---|---|---|---|
| pico | 50 req/s GET | 15001 | 0.00% | 1.1 ms | 2.1 ms | 14.8 ms | OK |
| folga | 150 req/s GET | 18001 | 0.00% | 0.7 ms | 1.7 ms | 7.1 ms | OK |
| escritas | 10 req/s POST | 3001 | 0.00% | 3.2 ms | 4.1 ms | 50.4 ms | OK |

Iterações descartadas pelo k6 por falta de VUs: 0.

Perda é qualquer resposta fora de 2xx, timeout acima de 2 s ou conexão recusada.
Thresholds: perda abaixo de 5% no pico e na folga, abaixo de 1% nas escritas.
Latência p95 abaixo de 200 ms no pico e abaixo de 500 ms na folga e nas escritas.
