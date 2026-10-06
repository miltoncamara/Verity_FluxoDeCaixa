// Teste de capacidade do GET /consolidado: sobe a taxa em degraus para achar o ponto de saturação.
// Cada degrau é um cenário de taxa fixa, executado um depois do outro, com métricas separadas.
// Rodar com: bash scripts/capacidade.sh 1   e   bash scripts/capacidade.sh 2   (número de réplicas)

import http from 'k6/http';

const CONSOLIDADO_URL = __ENV.CONSOLIDADO_URL || 'http://localhost:5002';
const API_KEY = __ENV.API_KEY || 'local-dev-key';
const REPLICAS = __ENV.REPLICAS || '?';
const RESULTADO = __ENV.RESULTADO || 'capacidade.md';
const NIVEIS = (__ENV.NIVEIS || '1000,2000,4000,6000,8000,10000').split(',').map(Number);
const DURACAO_SEGUNDOS = Number(__ENV.DURACAO_SEGUNDOS || 30);

// As leituras se espalham por um ano inteiro, para que boa parte delas vá ao banco e não só ao cache.
const DIAS_CONSULTADOS = 365;
const headers = { 'X-Api-Key': API_KEY };

// Aquecimento fora do relatório: aquece o JIT, os pools de conexão e o cache das réplicas recém-criadas.
// Sem ele, o primeiro degrau mede a partida a frio de uma réplica nova, e não a capacidade.
const AQUECIMENTO_SEGUNDOS = 15;
const scenarios = {
  aquecimento: {
    executor: 'constant-arrival-rate',
    exec: 'lerConsolidado',
    rate: 500,
    timeUnit: '1s',
    duration: `${AQUECIMENTO_SEGUNDOS}s`,
    preAllocatedVUs: 50,
    maxVUs: 200,
  },
};
const thresholds = {};
NIVEIS.forEach((taxa, i) => {
  const nome = `taxa_${taxa}`;
  scenarios[nome] = {
    executor: 'constant-arrival-rate',
    exec: 'lerConsolidado',
    rate: taxa,
    timeUnit: '1s',
    duration: `${DURACAO_SEGUNDOS}s`,
    startTime: `${AQUECIMENTO_SEGUNDOS + i * DURACAO_SEGUNDOS}s`,
    // VUs suficientes já no início do degrau, para o k6 não descartar requisições enquanto aloca.
    preAllocatedVUs: Math.max(50, Math.ceil(taxa / 5)),
    maxVUs: Math.max(200, Math.ceil(taxa / 2)),
  };
  // Os thresholds aqui servem só para o k6 separar as métricas por degrau no resumo.
  thresholds[`http_req_failed{scenario:${nome}}`] = ['rate<=1'];
  thresholds[`http_req_duration{scenario:${nome}}`] = ['p(95)>=0'];
  thresholds[`http_reqs{scenario:${nome}}`] = ['count>=0'];
  thresholds[`dropped_iterations{scenario:${nome}}`] = ['count>=0'];
});

export const options = { scenarios, thresholds, summaryTrendStats: ['avg', 'p(95)', 'p(99)', 'max'] };

export function lerConsolidado() {
  const dia = new Date();
  dia.setUTCDate(dia.getUTCDate() - Math.floor(Math.random() * DIAS_CONSULTADOS));
  http.get(`${CONSOLIDADO_URL}/consolidado/${dia.toISOString().slice(0, 10)}`, { headers, timeout: '2s' });
}

export function handleSummary(data) {
  const valores = (nome) => data.metrics[nome]?.values ?? {};
  const linhas = NIVEIS.map((taxa) => {
    const nome = `taxa_${taxa}`;
    const reqs = valores(`http_reqs{scenario:${nome}}`).count ?? 0;
    const perda = valores(`http_req_failed{scenario:${nome}}`).rate ?? 0;
    const duracao = valores(`http_req_duration{scenario:${nome}}`);
    const descartadas = valores(`dropped_iterations{scenario:${nome}}`).count ?? 0;
    const atende = perda < 0.05 && (duracao['p(95)'] ?? Infinity) < 200 && descartadas === 0;
    return `| ${taxa} req/s | ${(reqs / DURACAO_SEGUNDOS).toFixed(0)} req/s | ${(perda * 100).toFixed(2)}% | ${duracao['p(95)']?.toFixed(1)} ms | ${duracao['p(99)']?.toFixed(1)} ms | ${descartadas} | ${atende ? 'sim' : 'não'} |`;
  });

  const markdown = [
    `# Capacidade do consolidado com ${REPLICAS} réplica(s)`,
    '',
    `Executado em ${new Date().toISOString()} com k6, passando pelo nginx. Cada degrau dura ${DURACAO_SEGUNDOS} s, depois de ${AQUECIMENTO_SEGUNDOS} s de aquecimento.`,
    `As leituras se espalham por ${DIAS_CONSULTADOS} dias, então parte delas vai ao banco.`,
    '',
    '| Taxa alvo | Taxa atendida | Perda | p95 | p99 | Descartadas pelo k6 | Dentro do SLO |',
    '|---|---|---|---|---|---|---|',
    ...linhas,
    '',
    'Dentro do SLO significa perda abaixo de 5%, p95 abaixo de 200 ms e nenhuma requisição descartada pelo k6.',
    '',
  ].join('\n');

  return { [RESULTADO]: markdown, stdout: markdown };
}
