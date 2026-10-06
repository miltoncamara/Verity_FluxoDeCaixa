# ADR 0009: Segurança das APIs

**Status:** aceito
**Data:** 2026-10-06

## Contexto

O sistema precisa proteger os dados e os serviços contra ameaças, com autenticação, autorização, criptografia e proteção contra ataques.

A primeira versão tinha uma única API Key compartilhada. Ela autenticava, mas não autorizava: quem tinha a chave podia fazer tudo. Não havia limite de requisições, limite de tamanho do corpo, headers de segurança nem registro de quem fez cada lançamento.

## Decisão

**Autenticação por cliente.** Cada sistema que chama a API é um cliente, com a própria chave, as próprias permissões e o próprio limite de requisições, todos lidos da configuração (`Seguranca:Clientes`).

- A chave chega no header `X-Api-Key` e é validada por um `AuthenticationHandler` do ASP.NET.
- A comparação é feita em tempo constante contra todas as chaves, sem parar na primeira que bate.
- A API não sobe se não houver cliente configurado, ou se nomes ou chaves estiverem repetidos.

**Autorização por permissões.** O handler transforma as permissões do cliente em claims, e cada endpoint exige uma policy:

| Endpoint | Permissão |
|---|---|
| `POST /lancamentos` | `lancamentos.escrita` |
| `GET /lancamentos` e `GET /lancamentos/{id}` | `lancamentos.leitura` |
| `GET /consolidado/{data}` e `GET /consolidado?inicio=&fim=` | `consolidado.leitura` |
| `GET /health` | nenhuma, é anônimo |

A policy padrão (fallback) exige um cliente autenticado. Um endpoint novo nasce protegido, a não ser que declare o contrário. Sem chave, a resposta é `401`. Com chave mas sem a permissão, é `403`.

Como as regras ficam nas policies, trocar a API Key por JWT do Microsoft Entra ID muda só o esquema de autenticação. Os endpoints e as policies continuam iguais.

**Rate limiting.**

- Cada cliente tem a sua janela de requisições por segundo. Acima do limite, a resposta é `429` com `Retry-After`. Um cliente abusivo não afeta os outros.
- Requisições sem chave válida dividem um único limite de 10 por segundo. Isso freia tentativas de adivinhar chaves sem afetar os clientes legítimos.
- O `/health` não tem limite, para não derrubar as sondas do orquestrador.

**Endurecimento.**

- O corpo da requisição tem no máximo 16 KB, tanto no nginx quanto nas APIs (`413`).
- Headers recomendados pela OWASP para APIs REST: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Content-Security-Policy` com `default-src 'none'` e `frame-ancestors 'none'` e `Cache-Control: no-store`.
- O nginx não revela a sua versão.
- Datas aceitas só no formato ISO, e requisição mal formada responde `400` em vez de `500`.

**Auditoria.** Cada lançamento grava o cliente que o registrou (`criado_por`), vindo da autenticação e nunca do corpo da requisição. A `Idempotency-Key` passou a ser única por cliente. Dois clientes podem usar a mesma chave sem conflito, e um nunca recebe o lançamento do outro.

**Segurança no CI.** Dependabot para pacotes NuGet, imagens Docker e actions. CodeQL para análise estática do C#. Um passo que falha o build se algum pacote tiver vulnerabilidade conhecida.

## O que fica para a plataforma

A criptografia em trânsito e em repouso não roda localmente.

- **Em trânsito:** HTTPS na borda, TLS no PostgreSQL e no Service Bus.
- **Em repouso:** criptografia dos discos e dos bancos.

Na Azure, a maior parte disso já vem habilitada nos serviços gerenciados. A configuração está na [arquitetura alvo](../arquitetura-azure.md#segurança).

Localmente, um HTTPS com certificado autoassinado obrigaria o avaliador a aceitar avisos de certificado ou usar `curl -k`, sem demonstrar nada que a plataforma não faça melhor.

## Consequências

**Positivas**

- Cada sistema recebe só o acesso de que precisa (menor privilégio). Uma chave vazada de um PDV não lê relatórios.
- Uma chave pode ser revogada sem afetar os outros clientes.
- Abuso de um cliente e força bruta em chaves são contidos.
- Todo lançamento tem autor registrado.
- Os testes cobrem `401`, `403`, `429`, `413`, os headers e a auditoria.

**Negativas**

- API Key continua sendo um segredo de longa duração. Não expira sozinha e precisa ser trocada manualmente. O JWT do Entra ID resolve isso.
- O limite de requisições vale por réplica. Com 2 réplicas, um cliente pode chegar ao dobro do limite configurado. Na Azure, o limite global fica na borda, nas regras de rate limit do WAF do Front Door ou no API Management.
- O middleware e o handler de segurança se repetem nos dois serviços. A repetição foi mantida para não criar um projeto compartilhado que acoplaria os serviços ([ADR 0001](0001-dois-servicos.md)).
