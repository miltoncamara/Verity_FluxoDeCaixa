#!/usr/bin/env bash
# Roda o teste de carga com k6 (via Docker) contra a solução que está no ar com docker compose.
#
# Uso:
#   docker compose up -d --build
#   bash scripts/carga.sh                                   # pico de 5 min + folga de 2 min
#   DURACAO_PICO=30s DURACAO_FOLGA=15s bash scripts/carga.sh # versão curta, usada no CI
#
# O k6 roda dentro da rede do compose e passa pelo nginx, como um cliente real.
# O resumo é salvo em docs/carga/resultado-k6.md.

set -euo pipefail

RAIZ="$(cd "$(dirname "$0")/.." && pwd)"
REDE="fluxo-caixa_default"
API_KEY="${API_KEY:-local-dev-key}"

# No Git Bash do Windows, impede que caminhos como /scripts sejam convertidos para C:/...
export MSYS_NO_PATHCONV=1

# No Linux o k6 precisa rodar com o mesmo usuário de quem chamou para gravar em docs/carga.
# No Docker Desktop (Windows e macOS) a pasta montada já é gravável e o --user atrapalharia.
USUARIO=()
if [ "$(uname -s)" = "Linux" ]; then
  USUARIO=(--user "$(id -u):$(id -g)")
fi

docker run --rm \
  "${USUARIO[@]}" \
  --network "$REDE" \
  -v "$RAIZ/tests/load:/scripts:ro" \
  -v "$RAIZ/docs/carga:/resultados" \
  -e CONSOLIDADO_URL=http://nginx:5002 \
  -e LANCAMENTOS_URL=http://nginx:5001 \
  -e API_KEY="$API_KEY" \
  -e DURACAO_PICO="${DURACAO_PICO:-5m}" \
  -e DURACAO_FOLGA="${DURACAO_FOLGA:-2m}" \
  -e RESULTADO=/resultados/resultado-k6.md \
  grafana/k6:2.3.0 run /scripts/consolidado.js
