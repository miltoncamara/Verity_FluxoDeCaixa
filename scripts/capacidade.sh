#!/usr/bin/env bash
# Mede até onde o consolidado aguenta, com o número de réplicas informado.
#
# Uso:
#   bash scripts/capacidade.sh 1    # uma réplica da Consolidado.Api
#   bash scripts/capacidade.sh 2    # duas réplicas (padrão do docker compose)
#
# O resultado é salvo em docs/carga/capacidade-<N>-replica(s).md.
# Ao final, o compose volta para 2 réplicas.

set -euo pipefail
cd "$(dirname "$0")/.."

REPLICAS="${1:-2}"
RAIZ="$(pwd)"
API_KEY="${API_KEY:-local-dev-key}"
export MSYS_NO_PATHCONV=1

USUARIO=()
if [ "$(uname -s)" = "Linux" ]; then
  USUARIO=(--user "$(id -u):$(id -g)")
fi

echo "Ajustando a Consolidado.Api para $REPLICAS réplica(s)"
docker compose up -d --build --scale consolidado-api="$REPLICAS" >/dev/null 2>&1
for _ in $(seq 1 60); do curl -fs http://localhost:5002/health >/dev/null 2>&1 && break; sleep 2; done
sleep 10 # dá tempo para o nginx descobrir as réplicas

if [ "$REPLICAS" = "1" ]; then SUFIXO="1-replica"; else SUFIXO="$REPLICAS-replicas"; fi

docker run --rm \
  "${USUARIO[@]}" \
  --network fluxo-caixa_default \
  -v "$RAIZ/tests/load:/scripts:ro" \
  -v "$RAIZ/docs/carga:/resultados" \
  -e CONSOLIDADO_URL=http://nginx:5002 \
  -e API_KEY="$API_KEY" \
  -e REPLICAS="$REPLICAS" \
  -e NIVEIS="${NIVEIS:-1000,2000,4000,6000,8000,10000}" \
  -e RESULTADO="/resultados/capacidade-$SUFIXO.md" \
  grafana/k6:2.3.0 run --quiet /scripts/capacidade.js

echo "Voltando a Consolidado.Api para 2 réplicas"
docker compose up -d --scale consolidado-api=2 >/dev/null 2>&1
