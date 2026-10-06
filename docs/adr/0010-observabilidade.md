# ADR 0010: Observabilidade com OpenTelemetry

**Status:** aceito
**Data:** 2026-10-06

## Contexto

A solução se recupera sozinha de várias falhas, mas sem telemetria ninguém fica sabendo que algo deu errado. Um consumidor parado não gera erro: o saldo só para de avançar. Um RabbitMQ fora não derruba nada: os eventos só se acumulam na outbox.

Também é preciso seguir um lançamento através de um fluxo assíncrono: do POST, passando pela outbox e pelo RabbitMQ, até a atualização do saldo em outro serviço.

A empresa pode usar Datadog, Azure Monitor ou outra plataforma. A instrumentação não deve prender a solução a nenhuma delas.

## Decisão

**Os três pilares com OpenTelemetry, exportados por OTLP.**

- **Traces:** requisições HTTP, comandos no PostgreSQL e spans próprios de publicação e de consumo das mensagens.
- **Métricas:** HTTP, runtime do .NET, pool de conexões do PostgreSQL e métricas de negócio.
- **Logs:** estruturados, com os campos separados e com o `TraceId` e o `SpanId` de cada linha.

O código usa só as APIs nativas do .NET (`ActivitySource`, `Meter` e `ILogger`). O SDK do OpenTelemetry coleta e exporta. O destino vem da variável padrão `OTEL_EXPORTER_OTLP_ENDPOINT`. Sem ela, a telemetria é coletada mas não enviada, e por isso os testes rodam sem nenhum backend.

**Trace de ponta a ponta através da fila.**

- A outbox grava, junto com o evento, o contexto do trace da requisição (`traceparent` do W3C).
- O publicador continua esse trace e o coloca no header `traceparent` da mensagem.
- O consumidor lê o header e continua o mesmo trace.

O resultado é um único trace com o POST, o INSERT, a publicação, o consumo e os SQL que atualizam o saldo, mesmo com minutos de diferença entre eles se o broker estiver fora.

**Métricas de negócio pensadas para alerta.**

| Métrica | O que revela |
|---|---|
| `outbox.eventos_pendentes` e `outbox.idade_do_evento_mais_antigo` | Publicação atrasada: RabbitMQ fora ou publicador parado |
| `outbox.eventos_publicados` e `outbox.falhas_de_publicacao` | Vazão e erros da publicação |
| `consolidado.atraso_do_evento` | Tempo entre o lançamento e a atualização do saldo: o atraso real da consistência eventual |
| `consolidado.eventos_aplicados` e `consolidado.eventos_duplicados` | Vazão do consumidor e frequência de reentregas |
| `consolidado.mensagens_rejeitadas`, com o motivo | Mensagens indo para a DLQ ou voltando para a fila |
| `consolidado.falhas_transitorias` | Banco do consolidado fora |
| `consolidado.leituras`, com a origem | Proporção de respostas do cache, do banco, do último valor conhecido e de indisponibilidade |
| `lancamentos.registrados`, com o tipo | Vazão de negócio |

**Collector no meio.** As APIs enviam para um OpenTelemetry Collector, que repassa aos destinos. Localmente o destino é o Aspire Dashboard. Mandar para Datadog, Azure Monitor ou outro é mudar a configuração do collector, sem mexer nas APIs. Os exemplos já estão comentados em `infra/otel-collector/config.yaml` e validados.

**Menos ruído.**

- O `/health` não gera trace.
- Um amostrador descarta as consultas ao banco que não pertencem a nenhuma operação. Sem isso, o loop da outbox, que consulta o banco a cada 500 ms, geraria um trace solto a cada ciclo.

## Consequências

**Positivas**

- Um atraso na outbox, um consumidor parado ou uma mensagem na DLQ passam a ser visíveis, e podem virar alerta, antes de alguém reclamar do saldo.
- Um lançamento pode ser seguido do POST até o saldo num único trace.
- Trocar de plataforma de observabilidade não exige mudar código.
- Os testes cobrem a propagação do trace pela outbox e pela mensagem, e as métricas do consumidor.

**Negativas**

- O collector é mais uma peça para operar. Na Azure, ele pode rodar como sidecar ou DaemonSet no AKS, ou ser trocado pela distribuição do Azure Monitor para OpenTelemetry.
- As métricas da outbox são calculadas por uma consulta a cada ciclo do publicador, em todas as réplicas. É barato, porque o índice parcial cobre só os pendentes.
- As regras de alerta ficam na plataforma de destino e não estão versionadas neste repositório. As sugestões estão na [arquitetura alvo](../arquitetura-azure.md#observabilidade).
