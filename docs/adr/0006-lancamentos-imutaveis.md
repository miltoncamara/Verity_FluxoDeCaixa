# ADR 0006: Lançamentos imutáveis

**Status:** aceito
**Data:** 2026-10-05

## Contexto

Um lançamento registrado com erro precisa ser corrigido de alguma forma. Permitir update e delete traria três problemas:

- O consolidado precisaria de eventos de alteração e exclusão, com regras para desfazer o valor antigo e aplicar o novo, sempre na ordem certa.
- O histórico do caixa se perderia, o que é ruim para auditoria.
- Edições concorrentes do mesmo lançamento exigiriam controle de versão.

## Decisão

- Lançamentos são imutáveis. A API não tem endpoints de update nem de delete.
- A entidade `Lancamento` só tem propriedades de leitura. Ela é criada pela fábrica `Lancamento.Criar`, que valida os dados: valor maior que zero e com no máximo 2 casas decimais, descrição obrigatória com até 200 caracteres e tipo `Credito` ou `Debito`.
- A correção será feita por **estorno**, documentado como evolução futura: um novo lançamento, do tipo oposto, que referencia o original. O histórico fica completo e o consolidado continua só somando eventos.

## Consequências

**Positivas**

- Existe um único evento, `LancamentoRegistrado`, e o consolidado só precisa somar.
- O histórico completo do caixa fica preservado.
- Não há concorrência de edição.

**Negativas**

- Enquanto o estorno não existir, um lançamento errado só pode ser compensado com outro lançamento manual do tipo oposto.
