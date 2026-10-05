# Requisitos não funcionais

O desafio traz dois requisitos não funcionais:

1. O serviço de lançamentos não pode ficar indisponível se o consolidado cair.
2. Em dias de pico, o consolidado recebe 50 requisições por segundo com no máximo 5% de perda.

Este documento transforma esses requisitos em metas mensuráveis, define o que conta como perda e descreve o comportamento da solução em cada tipo de falha.

## SLOs

| Indicador | Meta | Como é medido | Resultado |
|---|---|---|---|
| Disponibilidade do `POST /lancamentos` com o consolidado ou o RabbitMQ fora | 100% dos POSTs confirmados, desde que o banco de lançamentos esteja no ar | [caos.sh](../scripts/caos.sh) | 326 POSTs, 0 falhas ([resultado](caos/resultado-caos.md)) |
| Perda no `GET /consolidado` a 50 req/s | No máximo 5% (requisito). Meta interna: abaixo de 1% | k6, cenário `pico`, 5 minutos | 0,00% em 15.001 requisições ([resultado](carga/resultado-k6.md)) |
| Perda no `GET /consolidado` a 150 req/s | No máximo 5% | k6, cenário `folga`, 2 minutos | 0,00% em 18.001 requisições |
| Latência do `GET /consolidado` a 50 req/s | p95 abaixo de 200 ms | k6, cenário `pico` | p95 de 2,1 ms |
| Latência do `POST /lancamentos` durante o pico de leitura | p95 abaixo de 500 ms | k6, cenário `escritas`, 10 req/s | p95 de 4,1 ms |
| Durabilidade | Nenhum lançamento confirmado com `201` se perde | Testes de integração e caos | Confirmado. O saldo convergiu centavo por centavo |
| Atraso do consolidado em operação normal | Poucos segundos entre o `201` e o saldo atualizado | Estimativa pelo desenho | Até cerca de 6 s: até 0,5 s do ciclo da outbox, alguns milissegundos de processamento e até 5 s de cache |

Os números de carga foram medidos numa única máquina (Windows 11 com Docker Desktop), com o k6 dentro da rede do compose. Num ambiente de nuvem a latência de rede seria maior, mas a margem até o SLO de 200 ms é muito grande.

### Por que a solução aguenta a carga

- **Leitura por chave.** O `GET /consolidado/{data}` busca uma única linha pela chave primária. Ele nunca soma lançamentos, então o custo não cresce com o volume do dia ([ADR 0005](adr/0005-saldo-pre-calculado.md)).
- **Cache em memória.** Cada data lida fica 5 s em memória. No teste, as leituras se espalham por 90 dias, então parte delas vai ao banco e parte sai do cache.
- **Limite de 2 s na leitura do banco.** Sem esse limite, as retentativas do EF Core segurariam a requisição por quase um minuto com o banco fora. A 50 req/s isso esgotaria as conexões e derrubaria a API inteira.
- **Escrita isolada da leitura.** As escritas no saldo chegam pelo consumidor, num ritmo controlado pelo prefetch da fila. Um pico de lançamentos não disputa recursos com as consultas de forma descontrolada.

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
| **Consolidado.Api cai** (processo inteiro) | Nenhum. Os POSTs continuam com `201` | A consulta fica indisponível. Os eventos se acumulam na fila, que é durável | Ao subir, o consumidor reconecta e processa a fila. Mensagens que estavam sem ack voltam para a fila e a idempotência evita soma dupla | `caos.sh` |
| **Consumidor para, mas a API continua de pé** | Nenhum | A consulta responde, mas o saldo para de avançar. A resposta não vem marcada como desatualizada, porque o banco está no ar | O consumidor reconecta sozinho a cada 5 s quando perde a conexão. Para um consumidor travado, a proteção é monitorar o tamanho da fila (evolução) | `caos.sh` (reconexão após a volta do RabbitMQ) |
| **Banco do consolidado cai** | Nenhum | A consulta responde o último valor conhecido com `X-Stale-Data: true` e `Age`. Um dia nunca lido recebe `503` com `Retry-After`. O health check fica `503`. O consumidor segura a mensagem sem ack e tenta de novo a cada 5 s, sem gastar tentativas | Quando o banco volta, a mensagem retida é aplicada e o consumo continua | `BancoForaDoArTests` e validação manual na fase 3 |
| **RabbitMQ cai** | Nenhum. Os eventos ficam pendentes na outbox | A consulta responde normalmente, mas o saldo para de avançar | O publicador tenta de novo a cada 5 s e publica os pendentes em ordem quando o RabbitMQ volta. As mensagens já publicadas são persistentes e sobrevivem ao restart | `caos.sh` (85 eventos retidos na outbox), `Post_e_confirmado_mesmo_com_rabbitmq_fora_do_ar...` |
| **Banco de lançamentos cai** | O POST falha. Esse é o único caso em que o serviço de lançamentos fica indisponível, porque o banco é a fonte da verdade. O health check fica `503` | Nenhum. A consulta continua respondendo os saldos já calculados | O EF Core repete as operações com falha transitória. Quando o banco volta, a API volta. A mitigação é um banco com alta disponibilidade (evolução) | Não automatizado. É o comportamento esperado |
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
- **Saldo desatualizado sem aviso.** Se o consumidor travar com o banco no ar, a consulta não sabe que o saldo está atrasado. A mitigação é alertar pela idade do evento mais antigo na fila e na outbox.
- **Cache por instância.** Com várias réplicas da Consolidado.Api, cada uma tem o seu cache e o seu último valor conhecido. Um cache distribuído (Redis) resolveria.
- **Atraso adicional de até 5 s.** O cache soma até 5 s ao atraso natural da consistência eventual.
