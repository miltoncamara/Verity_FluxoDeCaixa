#!/usr/bin/env bash
# Teste de caos do requisito "o serviço de lançamentos não pode ficar indisponível se o consolidado cair".
#
# 1. Sobe tudo com docker compose
# 2. Envia lançamentos sem parar para um dia exclusivo deste teste
# 3. Derruba a Consolidado.Api (kill) e depois o RabbitMQ (kill)
# 4. Confirma que nenhum POST falhou durante as quedas
# 5. Sobe tudo de novo e verifica que o saldo consolidado convergiu para o valor correto
#
# Uso: bash scripts/caos.sh      (sai com código 0 se passou e 1 se falhou)

set -euo pipefail
cd "$(dirname "$0")/.."

LANCAMENTOS="http://localhost:5001"
CONSOLIDADO="http://localhost:5002"
API_KEY="${API_KEY:-local-dev-key}"
RABBITMQ_USUARIO="fluxo:${RABBITMQ_PASSWORD:-local-dev}"
TEMPO_MAXIMO_CONVERGENCIA=120

TMP="$(mktemp -d)"
POSTS="$TMP/posts.txt"
touch "$POSTS"

log() { echo "[$(date +%H:%M:%S)] $*"; }

# Se o script for interrompido no meio, para o envio e deixa os serviços de pé.
finalizar() {
  touch "$TMP/parar"
  docker compose start rabbitmq consolidado-api >/dev/null 2>&1 || true
}
trap finalizar EXIT

aguardar_saude() {
  local nome="$1" url="$2"
  for _ in $(seq 1 60); do
    if curl -fs --max-time 2 "$url/health" >/dev/null 2>&1; then
      log "$nome saudável"
      return 0
    fi
    sleep 2
  done
  log "ERRO: $nome não ficou saudável a tempo"
  exit 1
}

# "12.34" -> 1234. Usa base 10 explícita para "0.05" não virar número octal.
para_centavos() {
  local valor="${1/./}"
  echo $((10#$valor))
}

# Envia um lançamento a cada ~100 ms até o arquivo "parar" existir.
# Grava "status tipo centavos" de cada POST para conferir no final.
enviar_lancamentos() {
  local i=0 tipo centavos valor status
  while [ ! -f "$TMP/parar" ]; do
    i=$((i + 1))
    if ((i % 4 == 0)); then tipo="Debito"; else tipo="Credito"; fi
    centavos=$(((i % 97) * 100 + (i % 100) + 1))
    valor=$(printf '%d.%02d' $((centavos / 100)) $((centavos % 100)))
    status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 \
      -X POST "$LANCAMENTOS/lancamentos" \
      -H "X-Api-Key: $API_KEY" -H "Content-Type: application/json" -H "Idempotency-Key: caos-$DATA-$i" \
      -d "{\"data\":\"$DATA\",\"tipo\":\"$tipo\",\"valor\":$valor,\"descricao\":\"caos $i\"}") || status="000"
    echo "$status $tipo $centavos" >>"$POSTS"
    sleep 0.1
  done
}

# Um dia entre 2100 e 2199, para não misturar com outros dados nem com execuções anteriores.
SEGUNDOS=$((4102444800 + ((RANDOM * 32768 + RANDOM) % 36500) * 86400))
DATA=$(date -u -d "@$SEGUNDOS" +%F 2>/dev/null || date -u -r "$SEGUNDOS" +%F)

log "Subindo a solução com docker compose"
docker compose up -d --build --quiet-pull >/dev/null 2>&1
aguardar_saude "Lancamentos.Api" "$LANCAMENTOS"
aguardar_saude "Consolidado.Api" "$CONSOLIDADO"

log "Enviando lançamentos continuamente para o dia $DATA"
enviar_lancamentos &
ENVIO_PID=$!
sleep 10

log ">>> Derrubando a Consolidado.Api (kill)"
docker compose kill consolidado-api >/dev/null 2>&1
sleep 15

log ">>> Derrubando o RabbitMQ (kill). Lançamentos e Consolidado.Api fora ao mesmo tempo"
docker compose kill rabbitmq >/dev/null 2>&1
sleep 15

PENDENTES=$(docker compose exec -T postgres-lancamentos psql -U postgres -d lancamentos -tAc \
  "select count(*) from outbox where publicado_em is null")
log "Eventos aguardando na outbox com o RabbitMQ fora: $PENDENTES"

log "<<< Subindo o RabbitMQ"
docker compose start rabbitmq >/dev/null 2>&1
sleep 10
log "<<< Subindo a Consolidado.Api"
docker compose start consolidado-api >/dev/null 2>&1
aguardar_saude "Consolidado.Api" "$CONSOLIDADO"
sleep 10

touch "$TMP/parar"
wait "$ENVIO_PID"

TOTAL=$(wc -l <"$POSTS" | tr -d ' ')
FALHAS=$(grep -vc '^201 ' "$POSTS" || true)
CREDITOS=$(awk '$1 == "201" && $2 == "Credito" { s += $3 } END { print s + 0 }' "$POSTS")
DEBITOS=$(awk '$1 == "201" && $2 == "Debito" { s += $3 } END { print s + 0 }' "$POSTS")
ESPERADO=$((CREDITOS - DEBITOS))
log "POSTs enviados: $TOTAL. Falhas: $FALHAS. Saldo esperado: $ESPERADO centavos"

# A fonte da verdade (Lancamentos.Api) precisa ter exatamente os lançamentos confirmados.
GRAVADOS=$(curl -s -H "X-Api-Key: $API_KEY" "$LANCAMENTOS/lancamentos?data=$DATA" | grep -o '"id"' | wc -l | tr -d ' ')

log "Aguardando o consolidado convergir (até ${TEMPO_MAXIMO_CONVERGENCIA}s)"
SALDO=""
for _ in $(seq 1 $((TEMPO_MAXIMO_CONVERGENCIA / 2))); do
  RESPOSTA=$(curl -s --max-time 3 -H "X-Api-Key: $API_KEY" "$CONSOLIDADO/consolidado/$DATA" || true)
  VALOR=$(echo "$RESPOSTA" | sed -nE 's/.*"saldo":([0-9]+\.[0-9]+).*/\1/p')
  if [ -n "$VALOR" ]; then
    SALDO=$(para_centavos "$VALOR")
    [ "$SALDO" -eq "$ESPERADO" ] && break
  fi
  sleep 2
done

DLQ=$(curl -s -u "$RABBITMQ_USUARIO" "http://localhost:15672/api/queues/%2F/consolidado.lancamentos.dlq" |
  grep -o '"messages":[0-9]*' | head -1 | cut -d: -f2 || true)

echo
echo "================ RESULTADO DO TESTE DE CAOS ================"
echo "Dia testado:                       $DATA"
echo "POSTs enviados:                    $TOTAL"
echo "POSTs com falha:                   $FALHAS"
echo "Lançamentos gravados na origem:    $GRAVADOS"
echo "Eventos pendentes durante a queda: $PENDENTES"
echo "Saldo esperado (centavos):         $ESPERADO"
echo "Saldo consolidado (centavos):      ${SALDO:-sem resposta}"
echo "Mensagens na dead letter queue:    ${DLQ:-desconhecido}"
echo "============================================================"

RESULTADO=0
if [ "$FALHAS" -ne 0 ]; then
  echo "FALHOU: $FALHAS POST(s) não retornaram 201 durante as quedas."
  grep -v '^201 ' "$POSTS" | cut -d' ' -f1 | sort | uniq -c
  RESULTADO=1
fi
if [ "$GRAVADOS" -ne $((TOTAL - FALHAS)) ]; then
  echo "FALHOU: a Lancamentos.Api tem $GRAVADOS lançamentos, mas $((TOTAL - FALHAS)) foram confirmados."
  RESULTADO=1
fi
if [ "${SALDO:-x}" != "$ESPERADO" ]; then
  echo "FALHOU: o consolidado não convergiu para o saldo esperado."
  RESULTADO=1
fi
[ "$RESULTADO" -eq 0 ] && echo "PASSOU: nenhum lançamento falhou e o consolidado convergiu para o valor correto."
exit "$RESULTADO"
