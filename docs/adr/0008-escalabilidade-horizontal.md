# ADR 0008: Escalabilidade horizontal com réplicas, balanceador e publicador único

**Status:** aceito
**Data:** 2026-10-05

## Contexto

A arquitetura precisa lidar com o aumento da carga sem degradar o desempenho. O caminho é rodar várias réplicas de cada API atrás de um balanceador de carga.

As APIs já não guardam estado entre requisições, e o consumidor já tolera concorrência ([ADR 0004](0004-consumidor-idempotente.md)). Faltavam três coisas:

- **O publicador da outbox não estava pronto para várias réplicas.** Cada réplica roda o seu publicador e todas leem os mesmos eventos pendentes. Um teste com dois publicadores ao mesmo tempo mostrou 200 mensagens para 100 eventos: cada evento foi publicado duas vezes.
- **Não havia balanceador de carga.** O compose expunha uma única instância de cada API.
- **O cache em memória perde eficiência com várias réplicas,** porque cada réplica guarda o seu.

## Decisão

**Publicador único com advisory lock.** A cada ciclo, o publicador abre uma transação numa conexão própria e chama `pg_try_advisory_xact_lock`. Só a réplica que consegue o lock publica naquele ciclo. As outras ficam de reserva e assumem no ciclo seguinte se a ativa cair, porque o lock é liberado sozinho quando a transação ou a conexão termina.

- Escolhemos o lock em vez de `SELECT ... FOR UPDATE SKIP LOCKED`, porque com o lock existe um único publicador ativo e a publicação segue a ordem da outbox. Com `SKIP LOCKED`, várias réplicas publicariam lotes diferentes ao mesmo tempo, intercalados. A ordem é a do horário de criação entre os eventos já gravados. Com lançamentos gravados em paralelo, um criado antes pode terminar o commit depois de outro e sair depois dele. O consolidado não depende da ordem, porque somar é comutativo.
- O lock é preso à transação, e não à sessão, para funcionar também atrás de um pool de conexões em modo de transação, como o PgBouncer.
- As marcações de publicado continuam sendo gravadas uma a uma, fora dessa transação. Uma queda no meio do lote não desfaz o que já foi publicado.

**nginx como balanceador local, na frente de 2 réplicas de cada API.**

- As portas 5001 e 5002 continuam as mesmas, então nenhum exemplo muda.
- As réplicas são descobertas pelo DNS do Docker (`resolve`) e entram e saem do balanceamento sozinhas.
- **Lançamentos:** round robin.
- **Consolidado:** hash pela URL. A mesma data vai sempre para a mesma réplica, o que mantém alta a taxa de acerto do cache em memória e mantém o último valor conhecido onde ele vai ser consultado. Foi usado o hash simples, sem `consistent`. O hash consistente do nginx monta o anel pelo nome do servidor, e como um nome resolve para várias réplicas, todas ficavam com os mesmos pontos e o nginx voltava a alternar entre elas.
- `proxy_connect_timeout 1s`. Uma réplica que acabou de parar deixa o IP sem resposta, e sem esse ajuste a conexão ficaria pendurada por 60 s. O teste de caos encontrou esse problema.
- O header `X-Upstream-Addr` mostra qual réplica respondeu, para que o balanceamento possa ser observado.

**Migrations com várias réplicas.** Todas as réplicas aplicam as migrations ao subir. O EF Core 10 trava a aplicação de migrations no banco, e foi verificado que, com duas réplicas subindo juntas, a migration roda uma única vez.

## Consequências

**Positivas**

- A escala horizontal funciona e é testada localmente: o teste de caos para uma réplica da Lancamentos.Api no meio do envio e nenhum POST falha.
- O publicador não duplica nem perde eventos com qualquer número de réplicas, e tem failover automático.
- O teste de capacidade mede a solução com 1 e com 2 réplicas ([docs/carga](../carga)).
- Na Azure o desenho se mantém: o nginx vira o ingress do AKS e o Front Door, e o hash por URL pode ser trocado por um cache distribuído ([arquitetura na Azure](../arquitetura-azure.md)).

**Negativas**

- Só uma réplica publica por vez. Isso limita a vazão de publicação a uma instância, o que é muito acima do necessário aqui. Se um dia não bastar, a outbox pode ser particionada, com um lock por partição.
- Com o hash por URL, uma data muito consultada fica concentrada em uma réplica. Para este domínio isso é aceitável, porque cada consulta é muito barata.
- Mudar o número de réplicas redistribui as datas entre elas, e o cache precisa esquentar de novo.
- Rodar várias réplicas na mesma máquina divide a mesma CPU. O ganho real de capacidade só aparece com réplicas em máquinas diferentes, como no AKS.
