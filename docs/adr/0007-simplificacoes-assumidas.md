# ADR 0007: Simplificações assumidas

**Status:** aceito
**Data:** 2026-10-05

## Contexto

O objetivo é uma solução que atenda todos os requisitos, que possa ser executada com um comando e que seja simples de ler e explicar. Algumas escolhas de produção foram trocadas por versões mais simples. Cada uma está listada aqui, com o motivo e o caminho para evoluir.

## Decisões

| Simplificação | Motivo | Evolução |
|---|---|---|
| **Um único comerciante** | Não há requisito de vários comerciantes | Id do comerciante no lançamento, no evento e na chave do saldo, vindo do token |
| **API Key por cliente** no header `X-Api-Key` | Autentica e autoriza por permissões sem exigir um provedor de identidade para rodar localmente ([ADR 0009](0009-seguranca.md)) | JWT do Microsoft Entra ID, com as mesmas policies |
| **TLS só nas conexões com os bancos**, com o certificado de teste da imagem e sem validar o servidor | HTTPS autoassinado na borda só traria avisos de certificado para quem roda o projeto | HTTPS no Front Door, `VerifyFull` no PostgreSQL, Service Bus com TLS e criptografia em repouso dos serviços gerenciados |
| **Valores padrão `local-dev`** para senhas e API Key no docker compose | O avaliador sobe tudo com um comando, sem criar arquivo `.env`. Os valores só servem para containers descartáveis | Segredos no Azure Key Vault, lidos por managed identity |
| **Publicador e consumidor como `BackgroundService`** dentro das APIs | Menos processos para subir e explicar | Deployments separados no AKS, escalando cada um pela sua carga |
| **Migrations aplicadas na inicialização** | O banco fica pronto sem passo manual | Etapa separada no pipeline de deploy |
| **Cache em memória**, com afinidade por data no nginx | Atende a carga pedida com folga e não adiciona infraestrutura | Azure Managed Redis |
| **Arquitetura em pastas**, sem projetos por camada, sem interfaces de implementação única, sem repositórios genéricos e sem mediator | Cada serviço é pequeno. Essas abstrações não resolvem nenhum problema real aqui e dificultariam a leitura | Extrair abstrações quando surgir uma necessidade concreta, como uma segunda implementação |
| **Logs no console** | Suficientes para depurar localmente | OpenTelemetry com traces, métricas e alertas |
| **Um único nó de RabbitMQ e de PostgreSQL** | Execução local | Serviços gerenciados com alta disponibilidade ([arquitetura na Azure](../arquitetura-azure.md)) |
| **nginx como balanceador**, com réplicas na mesma máquina | Mostra a escala horizontal e o failover entre réplicas rodando localmente | Front Door e ingress do AKS, com réplicas em zonas diferentes |

## Consequências

- A solução roda inteira com `docker compose up -d --build` e os testes rodam com `dotnet test`.
- Nenhuma simplificação enfraquece as garantias principais: a invariante da outbox, a idempotência do consumidor e a independência entre os serviços.
- O caminho de cada simplificação até produção está descrito na [arquitetura alvo na Azure](../arquitetura-azure.md).
