# ADR 0001: Dois serviços em vez de um monolito

**Status:** aceito
**Data:** 2026-10-05

## Contexto

O desafio pede um serviço de controle de lançamentos e um serviço de consolidado diário. O requisito mais forte é que o serviço de lançamentos não pode ficar indisponível se o consolidado cair.

Num monolito, os dois compartilhariam processo, memória, pool de conexões e banco. Um pico de 50 consultas por segundo ao consolidado, um vazamento de memória ou um deploy com defeito na parte de consolidado derrubaria também o registro de lançamentos.

## Decisão

Criar dois serviços independentes:

- **Lancamentos.Api**, dona dos lançamentos, com o seu próprio PostgreSQL.
- **Consolidado.Api**, dona dos saldos diários, com o seu próprio PostgreSQL.

Cada serviço é um único projeto .NET, organizado em pastas (Domain, Data, Endpoints, Messaging, Seguranca). Os dois compartilham apenas o projeto `Contracts`, que contém só o record do evento.

## Consequências

**Positivas**

- Uma falha no consolidado não afeta os lançamentos. O teste de caos comprova isso.
- Cada serviço escala, é implantado e evolui de forma independente.
- Cada banco pode ser dimensionado para o seu padrão de uso. Lançamentos têm muita escrita. O consolidado tem muita leitura por chave.

**Negativas**

- O consolidado passa a ser eventualmente consistente. Há um atraso de alguns segundos entre o lançamento e o saldo atualizado.
- Há mais peças para operar: dois bancos e um broker.
- Algum código se repete, como o middleware de API Key. Essa repetição foi aceita para não criar um projeto compartilhado que acoplaria os serviços.
