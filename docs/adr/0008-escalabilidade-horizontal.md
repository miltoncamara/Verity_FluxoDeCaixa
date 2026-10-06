# ADR 0008: Escalabilidade horizontal com réplicas, balanceador e publicador único

**Status:** aceito
**Data:** 2026-10-05

## Contexto

A arquitetura precisa lidar com o aumento da carga sem degradar o desempenho. O caminho é rodar várias réplicas de cada API atrás de um balanceador de carga.

As APIs não guardam estado entre requisições, e o consumidor tolera concorrência ([ADR 0004](0004-consumidor-idempotente.md)). Mesmo assim, rodar várias réplicas levanta três questões:

- **Quem publica a outbox.** Cada réplica da Lancamentos.Api tem o seu publicador, e todos enxergam os mesmos eventos pendentes. Sem coordenação, cada evento seria publicado uma vez por réplica.
- **Como distribuir as requisições** entre as réplicas, e como tirar do caminho uma réplica que parou.
- **O cache em memória.** Cada réplica tem o seu, então a mesma data pode ser lida do banco uma vez por réplica.

## Decisão

**Publicador único com advisory lock.** A cada ciclo, o publicador abre uma transação numa conexão própria e chama `pg_try_advisory_xact_lock`. Só a réplica que consegue o lock publica naquele ciclo. As outras ficam de reserva e assumem no ciclo seguinte se a ativa cair, porque o lock é liberado sozinho quando a transação ou a conexão termina.

- O lock foi preferido ao `SELECT ... FOR UPDATE SKIP LOCKED` porque deixa um único publicador ativo, e a publicação segue a ordem da outbox. Com `SKIP LOCKED`, várias réplicas publicariam lotes diferentes ao mesmo tempo, intercalados.
- A ordem é a do horário de criação entre os eventos já gravados. Quando dois lançamentos são gravados em paralelo, o criado antes pode terminar o commit depois e ser publicado depois. O consolidado não depende da ordem, porque somar é comutativo.
- O lock é preso à transação, e não à sessão, para funcionar também atrás de um pool de conexões em modo de transação, como o PgBouncer.
- As marcações de publicado são gravadas uma a uma, fora dessa transação. Uma queda no meio do lote não desfaz o que já foi publicado.

**nginx como balanceador local, na frente de 2 réplicas de cada API.**

- O nginx publica as portas 5001 e 5002. As réplicas não expõem porta própria.
- As réplicas são descobertas pelo DNS do Docker (`resolve`) e entram e saem do balanceamento sozinhas.
- **Lançamentos:** round robin.
- **Consolidado:** hash pela URL. A mesma data vai sempre para a mesma réplica, o que mantém alta a taxa de acerto do cache em memória e deixa o último valor conhecido na réplica onde ele vai ser consultado.
- O hash é o simples, sem a opção `consistent`. O hash consistente do nginx monta o anel pelo nome do servidor, e aqui um único nome resolve para várias réplicas. Todas ficariam com os mesmos pontos no anel, e o nginx alternaria entre elas.
- `proxy_connect_timeout 1s`. Quando uma réplica para, o IP dela deixa de responder, mas o nginx ainda o guarda por alguns segundos. Uma conexão para um IP que não existe não é recusada, ela fica esperando até o timeout, que por padrão é de 60 s. Com 1 s, o nginx desiste logo e tenta a outra réplica.
- O header `X-Upstream-Addr` mostra qual réplica respondeu, para que o balanceamento possa ser observado.

**Migrations com várias réplicas.** Todas as réplicas aplicam as migrations ao subir. O EF Core 10 trava a aplicação de migrations no banco, então, com várias réplicas subindo juntas, só uma aplica e as outras encontram o banco já atualizado.

## Consequências

**Positivas**

- A escala horizontal é testada localmente: o teste de caos para uma réplica da Lancamentos.Api no meio do envio e nenhum POST falha.
- O publicador não duplica nem perde eventos com qualquer número de réplicas, e tem failover automático. Um teste roda dois publicadores ao mesmo tempo e confere que cada evento chega uma única vez.
- O teste de capacidade mede a solução com 1 e com 2 réplicas ([docs/carga](../carga)).
- Na Azure o desenho se mantém: o nginx vira o ingress do AKS e o Front Door, e o hash por URL pode ser trocado por um cache distribuído ([arquitetura na Azure](../arquitetura-azure.md)).

**Negativas**

- Só uma réplica publica por vez. Isso limita a vazão de publicação a uma instância, o que é muito acima do necessário aqui. Se um dia não bastar, a outbox pode ser particionada, com um lock por partição.
- Com o hash por URL, uma data muito consultada fica concentrada em uma réplica. Para este domínio isso é aceitável, porque cada consulta é muito barata.
- Mudar o número de réplicas redistribui as datas entre elas, e o cache precisa esquentar de novo.
- Rodar várias réplicas na mesma máquina divide a mesma CPU. O ganho real de capacidade só aparece com réplicas em máquinas diferentes, como no AKS.
