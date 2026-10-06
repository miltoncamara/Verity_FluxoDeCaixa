# Requisitos não funcionais

O desafio traz dois requisitos não funcionais:

1. O serviço de lançamentos não pode ficar indisponível se o consolidado cair.
2. Em dias de pico, o consolidado recebe 50 requisições por segundo com no máximo 5% de perda.

Este documento transforma esses requisitos em metas mensuráveis, define o que conta como perda e descreve o comportamento da solução em cada tipo de falha.

## SLOs

| Indicador | Meta | Como é medido | Resultado |
|---|---|---|---|
| Disponibilidade do `POST /lancamentos` com o consolidado ou o RabbitMQ fora | 100% dos POSTs confirmados, desde que o banco de lançamentos esteja no ar | [caos.sh](../scripts/caos.sh) | 326 POSTs, 0 falhas ([resultado](caos/resultado-caos.md)) |
| Perda no `GET /consolidado` a 50 req/s | No máximo 5% (requisito). Meta interna: abaixo de 1% | k6, cenário `pico`, 5 minutos | 0,00% em 15.001 requisições, passando pelo nginx com 2 réplicas ([resultado](carga/resultado-k6.md)) |
| Perda no `GET /consolidado` a 150 req/s | No máximo 5% | k6, cenário `folga`, 2 minutos | 0,00% em 18.000 requisições |
| Latência do `GET /consolidado` a 50 req/s | p95 abaixo de 200 ms | k6, cenário `pico` | p95 de 2,3 ms |
| Latência do `POST /lancamentos` durante o pico de leitura | p95 abaixo de 500 ms | k6, cenário `escritas`, 10 req/s | p95 de 4,5 ms |
| Relatório de 30 dias (`GET /consolidado?inicio=&fim=`) durante o pico | p95 abaixo de 200 ms | k6, cenário `relatorio`, 10 req/s | 0,00% de perda e p95 de 3,7 ms |
| Durabilidade | Nenhum lançamento confirmado com `201` se perde | Testes de integração e caos | Confirmado. O saldo convergiu centavo por centavo |
| Atraso do consolidado em operação normal | Poucos segundos entre o `201` e o saldo atualizado | Estimativa pelo desenho | Até cerca de 6 s: até 0,5 s do ciclo da outbox, alguns milissegundos de processamento e até 5 s de cache |

Os números de carga foram medidos numa única máquina (Windows 11 com Docker Desktop), com o k6 dentro da rede do compose. Num ambiente de nuvem a latência de rede seria maior, mas a margem até o SLO de 200 ms é muito grande.

### Por que a solução aguenta a carga

- **Leitura por chave.** O `GET /consolidado/{data}` busca uma única linha pela chave primária. O relatório de um período lê uma linha por dia. Nenhum dos dois soma lançamentos, então o custo não cresce com o volume do dia ([ADR 0005](adr/0005-saldo-pre-calculado.md)).
- **Cache em memória.** Cada data lida fica 5 s em memória. No teste, as leituras se espalham por 90 dias, então parte delas vai ao banco e parte sai do cache.
- **Limite de 2 s na leitura do banco.** Sem esse limite, as retentativas do EF Core segurariam a requisição por quase um minuto com o banco fora. A 50 req/s isso esgotaria as conexões e derrubaria a API inteira.
- **Escrita isolada da leitura.** As escritas no saldo chegam pelo consumidor, num ritmo controlado pelo prefetch da fila. Um pico de lançamentos não disputa recursos com as consultas de forma descontrolada.

## Escalabilidade

O requisito é lidar com o aumento da carga sem degradação significativa do desempenho, com dimensionamento horizontal, balanceamento de carga e cache.

### Como cada peça escala

| Peça | Estratégia | Onde está |
|---|---|---|
| Lancamentos.Api | Réplicas sem estado atrás do nginx, em round robin | `docker-compose.yml` com 2 réplicas e [nginx.conf](../infra/nginx/nginx.conf) |
| Publicador da outbox | Roda em todas as réplicas, mas um advisory lock do PostgreSQL deixa só uma publicar por vez. Não duplica, não perde eventos e tem failover automático | [OutboxPublisher.cs](../src/Lancamentos.Api/Messaging/OutboxPublisher.cs) |
| Consolidado.Api | Réplicas sem estado atrás do nginx, com hash pela URL para cada data cair sempre na mesma réplica e aproveitar o cache | `nginx.conf` |
| Consumidor | Consumidores concorrentes na mesma fila. A idempotência e o upsert atômico garantem o resultado com qualquer número de réplicas | [AtualizadorDeSaldo.cs](../src/Consolidado.Api/Data/AtualizadorDeSaldo.cs) |
| Leitura do saldo | Saldo pré-calculado lido por chave e cache em memória de 5 s | [ADR 0005](adr/0005-saldo-pre-calculado.md) |

As decisões estão no [ADR 0008](adr/0008-escalabilidade-horizontal.md). A escala na Azure, com HPA, KEDA, Redis e réplica de leitura, está na [arquitetura alvo](arquitetura-azure.md#escalabilidade).

### O que foi comprovado

| Teste | Resultado |
|---|---|
| Duas réplicas publicando a outbox ao mesmo tempo | 100 eventos, 100 mensagens, sem duplicata e sem falta. Sem o lock, cada evento seria publicado uma vez por réplica |
| Uma réplica da Lancamentos.Api parada no meio do teste de caos | 442 POSTs, 0 falhas. O nginx mandou tudo para a outra réplica |
| Capacidade com 1 réplica da Consolidado.Api | Até 10.000 req/s dentro do SLO: 0% de perda e p95 de 9,7 ms ([resultado](carga/capacidade-1-replica.md)) |
| Capacidade com 2 réplicas da Consolidado.Api | Até 10.000 req/s dentro do SLO: 0% de perda e p95 de 23,4 ms ([resultado](carga/capacidade-2-replicas.md)) |

### Como ler os números de capacidade

- **Uma única réplica aguenta 200 vezes o requisito.** O requisito é 50 req/s, e uma réplica atende 10.000 req/s com p95 abaixo de 10 ms. Numa sondagem extra, a saturação começou entre 12.000 e 16.000 req/s.
- **Localmente, a segunda réplica não aumenta a capacidade.** O k6, o nginx, as réplicas e o PostgreSQL dividem a mesma CPU. Com duas réplicas, elas disputam o mesmo processador, e a 10.000 req/s o p95 até sobe. O teto medido é o da máquina, e não o da arquitetura.
- **O ganho real de capacidade aparece com réplicas em máquinas diferentes,** como nos nós do AKS. O que o teste local comprova é que a escala horizontal funciona corretamente: o balanceamento distribui a carga, a queda de uma réplica não gera erro, o publicador não duplica eventos e os consumidores concorrentes mantêm o saldo exato.
- Antes de cada medição, 15 s de aquecimento ficam fora do relatório. Sem isso, o primeiro degrau mediria a partida a frio da réplica recém-criada (JIT e pool de conexões vazios), e não a capacidade.

### Gargalos conhecidos

| Gargalo | Quando aparece | Mitigação |
|---|---|---|
| Linha do dia em `saldo_diario` | Todos os eventos do mesmo dia atualizam a mesma linha, e o banco serializa essas atualizações | O PostgreSQL faz milhares por segundo, muito acima do cenário. Se um dia não bastar, contadores parciais por partição somados na leitura |
| Publicação da outbox | Só uma réplica publica por vez | Uma instância publica centenas de eventos por segundo. Se precisar, particionar a outbox com um lock por partição |
| Escrita no banco de lançamentos | Um único servidor primário | Escala vertical e, depois, particionamento da tabela por data |
| Cache por réplica | Cada réplica tem o seu cache | Afinidade por data no nginx hoje. Redis compartilhado na Azure |

## Definição de perda

Uma requisição é considerada **perdida** quando:

- recebe uma resposta 5xx, ou
- não recebe resposta em até **2 segundos** (timeout), ou
- não consegue conectar (conexão recusada ou erro de rede).

Respostas 4xx causadas pelo cliente, como `400` por dado inválido ou `401` sem API Key, não são perda do serviço. O k6 é mais rigoroso que essa definição: ele conta como falha qualquer resposta fora de 2xx.

Uma resposta `200` com o header `X-Stale-Data: true` **não** é perda. O cliente recebe o último saldo conhecido e é avisado de que ele pode estar desatualizado.

## Modos de falha

A tabela mostra o efeito de cada falha nos dois serviços, como a solução se recupera e onde isso foi verificado.

| Falha | Efeito em lançamentos | Efeito no consolidado | Recuperação | Verificado em |
|---|---|---|---|---|
| **Uma réplica de uma API cai ou é reiniciada** (deploy, scale-in) | Nenhum. O nginx manda as requisições para a outra réplica. Se a réplica parada era a que publicava a outbox, outra assume no ciclo seguinte | Nenhum. As outras réplicas continuam consumindo e respondendo | O nginx redescobre as réplicas pelo DNS do Docker a cada 5 s | `caos.sh` (uma réplica parada no meio do envio, 0 falhas) e `Duas_replicas_publicando_ao_mesmo_tempo_nao_duplicam_nem_perdem_eventos` |
| **Consolidado.Api cai** (todas as réplicas) | Nenhum. Os POSTs continuam com `201` | A consulta fica indisponível. Os eventos se acumulam na fila, que é durável | Ao subir, o consumidor reconecta e processa a fila. Mensagens que estavam sem ack voltam para a fila e a idempotência evita soma dupla | `caos.sh` |
| **Consumidor para, mas a API continua de pé** | Nenhum | A consulta responde, mas o saldo para de avançar. A resposta não vem marcada como desatualizada, porque o banco está no ar | O consumidor reconecta sozinho a cada 5 s quando perde a conexão. Para um consumidor travado, a proteção é monitorar o tamanho da fila (evolução) | `caos.sh` (reconexão após a volta do RabbitMQ) |
| **Banco do consolidado cai** | Nenhum | A consulta responde o último valor conhecido com `X-Stale-Data: true` e `Age`. Um dia nunca lido recebe `503` com `Retry-After`. O health check fica `503`. O consumidor segura a mensagem sem ack e tenta de novo a cada 5 s, sem gastar tentativas | Quando o banco volta, a mensagem retida é aplicada e o consumo continua | `BancoForaDoArTests` e validação manual na fase 3 |
| **RabbitMQ cai** | Nenhum. Os eventos ficam pendentes na outbox | A consulta responde normalmente, mas o saldo para de avançar | O publicador tenta de novo a cada 5 s e publica os pendentes em ordem quando o RabbitMQ volta. As mensagens já publicadas são persistentes e sobrevivem ao restart | `caos.sh` (85 eventos retidos na outbox), `Post_e_confirmado_mesmo_com_rabbitmq_fora_do_ar...` |
| **Banco de lançamentos cai** | O POST falha em cerca de 5 s com `503` e `Retry-After`. Nada é gravado, nem o lançamento nem o evento. Esse é o único caso em que o serviço de lançamentos fica indisponível, porque o banco é a fonte da verdade. O health check fica `503` | Nenhum. A consulta continua respondendo os saldos já calculados | O cliente repete com a mesma `Idempotency-Key` quando o banco volta. Se o pedido chegou a ser gravado, recebe o original. A mitigação é um banco com alta disponibilidade (evolução) | `Com_o_banco_fora_o_post_falha_rapido_com_503_e_retry_after` |
| **Publicador da outbox falha** (exceção ou queda entre o publish e a marcação) | Nenhum. Os eventos continuam pendentes | O saldo atrasa até o próximo ciclo | O loop captura qualquer erro e tenta de novo. Se o evento foi publicado mas não marcado, ele é publicado outra vez e o consumidor ignora a duplicata | `OutboxPublisherTests` e `Evento_entregue_duas_vezes_e_somado_uma_unica_vez` |
| **Lancamentos.Api cai entre o commit e a resposta** | O cliente recebe erro ou timeout, mas o lançamento foi gravado | Nenhum. O evento será publicado normalmente | O cliente repete o POST com a mesma `Idempotency-Key` e recebe o lançamento original, sem duplicar | `Post_repetido_com_mesma_idempotency_key_nao_duplica` |
| **Nenhuma fila ligada ao exchange** (por exemplo, o consolidado nunca subiu) | Nenhum | Nenhum evento é perdido | Com `mandatory`, o broker devolve a mensagem e ela continua pendente na outbox até a fila existir | `Sem_fila_para_receber_o_evento_continua_pendente_e_e_publicado_depois` |
| **Mensagem inválida** | Nenhum | A mensagem vai direto para a DLQ e não trava a fila | Análise manual da DLQ | `Mensagem_invalida_vai_direto_para_a_dead_letter_queue` |
| **Erro inesperado ao processar** (bug, tabela ausente) | Nenhum | A mensagem volta para a fila até 5 vezes e depois vai para a DLQ. As outras mensagens continuam sendo processadas | Corrigir a causa e devolver a mensagem da DLQ para a fila. O reprocessamento é seguro por causa da idempotência | `Erro_inesperado_devolve_para_a_fila_ate_o_limite...` |

### Combinações

| Falha combinada | Efeito | Verificado em |
|---|---|---|
| Consolidado.Api e RabbitMQ fora ao mesmo tempo | Lançamentos continuam funcionando. Os eventos ficam na outbox. Quando tudo volta, o saldo converge para o valor exato | `caos.sh` |
| RabbitMQ e banco do consolidado fora | Lançamentos continuam funcionando. A consulta responde o último valor conhecido ou `503`. Tudo converge quando os dois voltam | Coberto pelos casos individuais |
| Tudo fora, menos o banco de lançamentos | Lançamentos continuam funcionando. Nenhum evento se perde, porque todos ficam na outbox | Coberto pelo `caos.sh` |
| Banco de lançamentos fora junto com qualquer outra peça | O POST falha. Nenhum dado é perdido, porque nada foi confirmado ao cliente | Comportamento esperado |

## Limites conhecidos

- **Banco do consolidado fora por muito tempo.** Uma mensagem pode ficar sem ack por até 30 minutos, que é o `consumer_timeout` padrão do RabbitMQ. Depois disso o broker fecha o canal e a mensagem volta para a fila, contando uma entrega. Uma queda de mais de duas horas e meia (cinco ciclos de 30 minutos) levaria a mensagem para a DLQ. Ela pode ser reprocessada com segurança depois.
- **Quedas repetidas do consumidor.** Uma queda com mensagens sem ack também conta uma entrega para elas. Cinco quedas seguidas com a mesma mensagem em mãos a mandariam para a DLQ.
- **Saldo desatualizado sem aviso na resposta.** Se o consumidor travar com o banco no ar, a consulta não sabe que o saldo está atrasado. As métricas `outbox.idade_do_evento_mais_antigo` e `consolidado.atraso_do_evento` mostram o atraso, e a plataforma de observabilidade alerta ([ADR 0010](adr/0010-observabilidade.md)).
- **Cache por instância.** Cada réplica da Consolidado.Api tem o seu cache e o seu último valor conhecido. O nginx compensa isso mandando cada data sempre para a mesma réplica. Se essa réplica cair, a outra começa sem cache para aquela data. Um cache distribuído (Redis) resolveria.
- **Atraso adicional de até 5 s.** O cache soma até 5 s ao atraso natural da consistência eventual.
