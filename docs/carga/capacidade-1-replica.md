# Capacidade do consolidado com 1 réplica(s)

Executado em 2026-10-06T00:52:23.814Z com k6, passando pelo nginx. Cada degrau dura 30 s, depois de 15 s de aquecimento.
As leituras se espalham por 365 dias, então parte delas vai ao banco.

| Taxa alvo | Taxa atendida | Perda | p95 | p99 | Descartadas pelo k6 | Dentro do SLO |
|---|---|---|---|---|---|---|
| 1000 req/s | 1000 req/s | 0.00% | 1.4 ms | 2.0 ms | 0 | sim |
| 2000 req/s | 2000 req/s | 0.00% | 0.9 ms | 1.8 ms | 0 | sim |
| 4000 req/s | 4000 req/s | 0.00% | 1.0 ms | 3.3 ms | 0 | sim |
| 6000 req/s | 6000 req/s | 0.00% | 1.8 ms | 8.7 ms | 0 | sim |
| 8000 req/s | 8000 req/s | 0.00% | 3.6 ms | 10.5 ms | 0 | sim |
| 10000 req/s | 10000 req/s | 0.00% | 9.7 ms | 32.6 ms | 0 | sim |

Dentro do SLO significa perda abaixo de 5%, p95 abaixo de 200 ms e nenhuma requisição descartada pelo k6.
