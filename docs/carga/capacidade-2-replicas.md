# Capacidade do consolidado com 2 réplica(s)

Executado em 2026-10-06T00:55:55.309Z com k6, passando pelo nginx. Cada degrau dura 30 s, depois de 15 s de aquecimento.
As leituras se espalham por 365 dias, então parte delas vai ao banco.

| Taxa alvo | Taxa atendida | Perda | p95 | p99 | Descartadas pelo k6 | Dentro do SLO |
|---|---|---|---|---|---|---|
| 1000 req/s | 1000 req/s | 0.00% | 1.7 ms | 2.4 ms | 0 | sim |
| 2000 req/s | 2000 req/s | 0.00% | 1.5 ms | 2.6 ms | 0 | sim |
| 4000 req/s | 4000 req/s | 0.00% | 1.7 ms | 8.6 ms | 0 | sim |
| 6000 req/s | 6000 req/s | 0.00% | 2.7 ms | 27.0 ms | 0 | sim |
| 8000 req/s | 8000 req/s | 0.00% | 3.7 ms | 14.3 ms | 0 | sim |
| 10000 req/s | 10000 req/s | 0.00% | 23.4 ms | 68.0 ms | 0 | sim |

Dentro do SLO significa perda abaixo de 5%, p95 abaixo de 200 ms e nenhuma requisição descartada pelo k6.
