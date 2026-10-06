# Arquitetura alvo na Azure

Este documento descreve como a solução rodaria em produção na Azure. A versão local usa docker compose, nginx, RabbitMQ e PostgreSQL em containers. Aqui cada peça é trocada por um serviço gerenciado, com alta disponibilidade dentro da região e recuperação de desastre em uma segunda região.

As garantias principais **não dependem da plataforma**. A outbox, o consumidor idempotente, o saldo pré-calculado e a independência entre os serviços continuam iguais. O que muda são as peças de infraestrutura e algumas bibliotecas.

## Diagrama

```mermaid
flowchart TB
    Cliente([Comerciante])
    Entra[Microsoft Entra ID<br/>emite tokens JWT]

    subgraph Global[Global]
        FD[Azure Front Door Premium<br/>WAF, TLS, roteamento entre regiões]
    end

    subgraph Primaria[Região primária, 3 zonas de disponibilidade]
        subgraph Rede[VNet com private endpoints]
            subgraph AKS[Azure Kubernetes Service, node pools em 3 zonas]
                Ingress[Application Gateway for Containers<br/>ingress]
                LApi[lancamentos-api<br/>N réplicas, HPA<br/>publicador da outbox com lock]
                CApi[consolidado-api<br/>N réplicas, HPA]
                CCons[consolidado-consumer<br/>N réplicas, KEDA]
            end

            SB{{Azure Service Bus Premium<br/>tópico lancamentos<br/>assinatura consolidado com DLQ}}
            LDb[(PostgreSQL Flexible Server<br/>lancamentos<br/>HA zone-redundant)]
            CDb[(PostgreSQL Flexible Server<br/>consolidado<br/>HA zone-redundant)]
            CRead[(Réplica de leitura<br/>consolidado)]
            Redis[(Azure Managed Redis<br/>cache do consolidado)]
        end
        subgraph Suporte[Suporte compartilhado, usado por todos os pods]
            KV[Azure Key Vault<br/>segredos por workload identity]
            Mon[Azure Monitor e Application Insights<br/>traces, métricas e logs via OpenTelemetry]
        end
    end

    subgraph Secundaria[Região secundária, recuperação de desastre]
        LDbGeo[(Réplica geográfica<br/>lancamentos)]
        CDbGeo[(Réplica geográfica<br/>consolidado)]
        SBGeo{{Service Bus<br/>Geo-Replication}}
        AKS2[AKS em espera<br/>mesmo Bicep]
    end

    Cliente -- HTTPS --> FD
    Cliente -. obtém token .-> Entra
    FD --> Ingress
    Ingress --> LApi
    Ingress --> CApi
    LApi -- lançamento + outbox --> LDb
    LApi -- publica com MessageId --> SB
    SB -- PeekLock --> CCons
    CCons -- upsert idempotente --> CDb
    CApi -- leitura por chave --> CRead
    CApi -- cache distribuído --> Redis
    CDb -. replicação .-> CRead
    LDb -. replicação assíncrona .-> LDbGeo
    CDb -. replicação assíncrona .-> CDbGeo
    SB -. replicação .-> SBGeo
    FD -. failover .-> AKS2
```

## Equivalência entre o ambiente local e a Azure

| Peça local | Serviço na Azure | Por quê |
|---|---|---|
| nginx como balanceador | **Azure Front Door Premium** na borda, com WAF, e **Application Gateway for Containers** como ingress do AKS | O Front Door termina TLS, filtra ataques e faz o failover entre regiões. O ingress distribui entre os pods |
| Réplicas no docker compose | **Azure Kubernetes Service (AKS)** com node pools em 3 zonas | Escala horizontal automática e tolerância à queda de uma zona. O **Azure Container Apps** é uma alternativa mais simples, com KEDA embutido |
| RabbitMQ | **Azure Service Bus Premium**: tópico `lancamentos`, assinatura `consolidado` | Gerenciado, com zonas de disponibilidade, DLQ nativa, contagem de entregas e detecção de duplicatas |
| PostgreSQL em container (um por serviço) | **Azure Database for PostgreSQL Flexible Server**, um servidor por serviço | Mantém o isolamento de falha entre os serviços, como no [ADR 0001](adr/0001-dois-servicos.md) |
| Cache em memória por réplica | **Azure Managed Redis** | Cache e último valor conhecido compartilhados entre as réplicas, sobrevivendo a restarts. O Azure Cache for Redis está em processo de aposentadoria, por isso a escolha é o Azure Managed Redis |
| Variáveis de ambiente com padrão `local-dev` | **Azure Key Vault** com **workload identity** | Nenhum segredo em variável de ambiente nem no repositório. O acesso ao PostgreSQL pode usar autenticação do Entra ID, sem senha |
| API Key no header `X-Api-Key` | **Microsoft Entra ID** com JWT e escopos (`lancamentos.escrita`, `consolidado.leitura`) | Identidade por usuário ou aplicação, com expiração e revogação. O **Azure API Management** pode ficar na frente, se a empresa já o usar para governança de APIs |
| OpenTelemetry Collector e Aspire Dashboard | **Azure Monitor** com **Application Insights**, recebendo do mesmo collector | Mesma instrumentação, com retenção, alertas e dashboards gerenciados |
| docker-compose.yml | **Bicep**, aplicado por pipeline | Toda a infraestrutura versionada e reproduzível, inclusive a região secundária |

## Escalabilidade

| Componente | Como escala | Gatilho |
|---|---|---|
| `lancamentos-api` | Horizontal, por réplicas no AKS | **HPA** por CPU ou por requisições por segundo |
| Publicador da outbox | Roda em todas as réplicas da API, mas só uma publica por vez, graças ao advisory lock do PostgreSQL ([ADR 0008](adr/0008-escalabilidade-horizontal.md)) | Não precisa escalar. Uma instância publica centenas de eventos por segundo. Se precisar de mais, a outbox pode ser particionada por chave, com um lock por partição |
| `consolidado-api` | Horizontal, por réplicas no AKS | **HPA** por CPU ou por requisições por segundo |
| `consolidado-consumer` | Horizontal, como deployment separado da API de leitura | **KEDA** com o scaler do Service Bus, pelo número de mensagens na assinatura. Com a fila vazia ele pode cair para o mínimo |
| Leitura do saldo | Redis compartilhado na frente e **réplica de leitura** do PostgreSQL atrás | A leitura já é eventualmente consistente, então o pequeno atraso da réplica é aceitável |
| Escrita de lançamentos | Vertical no PostgreSQL. Se um dia não bastar, particionamento da tabela por data | Monitoramento de CPU e IOPS do servidor |
| Service Bus | Messaging units do tier Premium | Uso de CPU e memória do namespace |

O advisory lock usado pelo publicador é preso a uma transação (`pg_try_advisory_xact_lock`). Por isso ele funciona também com o PgBouncer embutido do Flexible Server em modo de transação.

## Alta disponibilidade dentro da região

| Componente | Mecanismo | Efeito de uma queda de zona |
|---|---|---|
| Front Door | Serviço global | Não é afetado |
| AKS | Node pools distribuídos em 3 zonas, pelo menos 2 réplicas por deployment, PodDisruptionBudget | As réplicas das outras zonas continuam atendendo |
| PostgreSQL Flexible Server | **HA zone-redundant**: um standby em outra zona, com replicação síncrona | Failover automático em cerca de 60 a 120 s, sem perda de dados confirmados (RPO zero) |
| Service Bus Premium | Zonas de disponibilidade | Transparente para os clientes |
| Azure Managed Redis | Alta disponibilidade com réplicas em zonas | Transparente. E, se o cache cair, a API continua lendo do banco |

Durante o failover do banco de lançamentos, os POSTs falham por até cerca de 2 minutos. O `EnableRetryOnFailure` do EF Core absorve parte desse tempo. A `Idempotency-Key` permite que o cliente repita o POST com segurança. O consolidado continua respondendo pelo Redis e pela réplica de leitura.

## Recuperação de desastre entre regiões

Estratégia **ativo-passivo**: uma região atende e a outra fica pronta para assumir.

| Componente | Mecanismo | Perda de dados (RPO) |
|---|---|---|
| PostgreSQL de lançamentos e do consolidado | **Réplica de leitura em outra região** com **virtual endpoints**. No desastre, a réplica é promovida a primária e o endpoint passa a apontar para ela, sem mudar a connection string | Segundos. A replicação entre regiões é assíncrona |
| Service Bus | **Geo-Replication** do tier Premium, que replica metadados e mensagens. A região secundária é promovida e o mesmo hostname passa a apontar para ela | Configurável entre replicação síncrona e assíncrona |
| AKS e demais recursos | Mesmo Bicep aplicado na região secundária. Pode ficar mínimo (warm standby) ou ser criado só no desastre | Não guarda dados |
| Front Door | Health probes nas duas regiões e failover automático do roteamento | Não guarda dados |

> **Sobre "failover group":** esse recurso é do **Azure SQL Database** (auto-failover groups). No PostgreSQL Flexible Server, o equivalente é a réplica geográfica com virtual endpoints, descrita acima. Se a empresa preferir Azure SQL, os auto-failover groups fazem esse papel, mas a troca de banco exigiria adaptar o SQL do `ON CONFLICT` para `MERGE`.

**Por que a solução se recupera bem de um desastre:** depois do failover pode haver pequenas diferenças. Algum evento pode ter sido publicado mas não marcado na outbox, ou entregue duas vezes. A idempotência do consumidor absorve isso e o saldo volta a convergir sozinho, como o teste de caos mostra localmente.

Metas sugeridas, para validar com o negócio:

| Meta | Valor sugerido |
|---|---|
| RTO, tempo para voltar a operar em outra região | Até 1 hora |
| RPO, perda máxima de lançamentos confirmados num desastre regional | Até 5 minutos |
| RPO numa queda de zona | Zero |

## Segurança

O que já roda localmente está no [ADR 0009](adr/0009-seguranca.md). A tabela mostra como cada proteção fica na Azure.

| Proteção | Localmente | Na Azure |
|---|---|---|
| **Autenticação** | API Key por cliente no header `X-Api-Key` | JWT emitido pelo **Microsoft Entra ID**, com expiração e revogação. Os sistemas clientes usam client credentials ou managed identity. Só o esquema de autenticação muda: as policies continuam as mesmas |
| **Autorização** | Permissões por cliente, exigidas por policies em cada endpoint (`403`) | As mesmas policies, lendo os escopos e roles do token (`lancamentos.escrita`, `consolidado.leitura`) |
| **Criptografia em trânsito** | TLS 1.3 obrigatório nas conexões com os dois PostgreSQL (`SSL Mode=Require`), com o certificado de teste da imagem. As chamadas às APIs e ao RabbitMQ seguem em HTTP e AMQP | HTTPS com TLS 1.2 ou superior no Front Door e no ingress, com HSTS. PostgreSQL com `SslMode=VerifyFull`. O Service Bus só aceita TLS |
| **Criptografia em repouso** | As chaves dos clientes ficam só como hash SHA-256. Os volumes dos bancos não são criptografados | Habilitada por padrão no PostgreSQL Flexible Server, no Service Bus, no Redis e nos discos do AKS. Se a empresa exigir, com chave própria (customer-managed key) guardada no Key Vault |
| **Rate limiting** | Por cliente e por réplica, nas APIs (`429`) | Também na borda, com regras de rate limit do WAF do Front Door, que valem para todas as réplicas juntas. O API Management é uma alternativa, com cotas por assinatura |
| **Proteção contra ataques web** | Validação de entrada, limite de corpo, headers OWASP | WAF do Front Door com as regras gerenciadas da OWASP e proteção contra bots. Proteção DDoS da própria Azure |
| **Rede** | Só o nginx publica portas | **Private endpoints**: banco, Service Bus, Redis e Key Vault ficam sem acesso público. Network policies no AKS limitam quem fala com quem |
| **Segredos** | Variáveis de ambiente com padrão `local-dev`. As chaves dos clientes aparecem só como hash | **Key Vault** com **workload identity**. Os pods acessam o PostgreSQL e o Service Bus pela identidade, sem senha |
| **Auditoria** | Coluna `criado_por` em cada lançamento | A mesma coluna, mais os logs de acesso do Front Door e do Entra ID no Azure Monitor |
| **Dependências e código** | Dependabot, CodeQL e verificação de pacotes vulneráveis no CI | O mesmo, mais o **Microsoft Defender for Containers** analisando as imagens no Azure Container Registry e o cluster em execução |
| **Postura** | Não se aplica | **Microsoft Defender for Cloud** com as recomendações do Azure Security Benchmark |

## Observabilidade

Os três pilares já rodam localmente com OpenTelemetry ([ADR 0010](adr/0010-observabilidade.md)). As APIs enviam traces, métricas e logs por OTLP a um OpenTelemetry Collector. Na Azure muda só o destino do collector:

- **Azure Monitor e Application Insights:** exportador `azuremonitor` do collector, já comentado em `infra/otel-collector/config.yaml`. Outra opção é a distribuição do Azure Monitor para OpenTelemetry direto nas APIs.
- **Datadog:** exportador `datadog` do collector, também comentado no mesmo arquivo, ou o Datadog Agent recebendo OTLP.

No AKS, o collector roda como DaemonSet ou como deployment central. As APIs continuam apontando para ele pela variável `OTEL_EXPORTER_OTLP_ENDPOINT`.

Alertas sugeridos, todos sobre métricas que as APIs já emitem ou que o Service Bus publica:

| Alerta | Métrica | Condição |
|---|---|---|
| Publicação atrasada | `outbox.idade_do_evento_mais_antigo` | Acima de 60 s por 2 minutos |
| Saldo atrasado | `consolidado.atraso_do_evento`, p95 | Acima de 30 s por 5 minutos |
| Consumidor parado | Mensagens ativas na assinatura do Service Bus, e `consolidado.eventos_aplicados` sem crescer | Fila crescendo por 5 minutos |
| Mensagem na DLQ | Mensagens na dead letter da assinatura, e `consolidado.mensagens_rejeitadas` | Qualquer mensagem |
| Banco do consolidado fora | `consolidado.falhas_transitorias` | Qualquer ocorrência em 1 minuto |
| Consolidado em fallback | `consolidado.leituras` com origem `ultimo_valor_conhecido` ou `indisponivel` | Acima de 1% das leituras |
| SLO de latência e de perda | `http.server.request.duration`, p95 e taxa de 5xx | p95 acima de 200 ms ou perda acima de 1% |
| Abuso ou ataque | `aspnetcore.rate_limiting.requests` rejeitadas | Pico fora do padrão |

O trace distribuído liga o `POST /lancamentos` à publicação e à atualização do saldo pelo header `traceparent` da mensagem. Os logs de cada etapa trazem o mesmo `TraceId`.

## O que muda no código

| Parte | Hoje | Na Azure |
|---|---|---|
| Publicação | `RabbitMQ.Client` com publisher confirms e `mandatory` | `Azure.Messaging.ServiceBus`. O `MessageId` recebe o Id do evento e a **detecção de duplicatas** do Service Bus descarta reenvios dentro da janela configurada. A outbox continua igual |
| Consumo | Ack manual, `reject` com requeue e quorum queue com limite de entregas | `ServiceBusProcessor` em modo PeekLock: `Complete` depois do commit, `Abandon` para erro inesperado (o `MaxDeliveryCount` manda para a DLQ) e `DeadLetter` para mensagem inválida. A idempotência continua igual |
| Cache | `IMemoryCache` | `HybridCache` com Redis como segundo nível |
| Autenticação | Middleware de API Key | `AddJwtBearer` com o Entra ID e políticas por escopo |
| Configuração | Variáveis de ambiente | Key Vault e workload identity |

O domínio, a outbox, o upsert idempotente, os endpoints e os testes de domínio não mudam.

## Infraestrutura como código

Módulos Bicep sugeridos, aplicados pelo pipeline em cada região:

- `rede.bicep`: VNet, subnets e zonas DNS privadas
- `aks.bicep`: cluster, node pools em zonas e workload identity
- `postgres.bicep`: os dois Flexible Servers, HA zone-redundant, réplicas de leitura e geográficas e virtual endpoints
- `servicebus.bicep`: namespace Premium, tópico, assinatura com `MaxDeliveryCount` 5 e geo-replicação
- `redis.bicep`, `keyvault.bicep`, `frontdoor.bicep` e `monitor.bicep`

Os manifestos do Kubernetes (deployments, HPA, KEDA ScaledObject, PodDisruptionBudget) ficariam num chart Helm por serviço.
