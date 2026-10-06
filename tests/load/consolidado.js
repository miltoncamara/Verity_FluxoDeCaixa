// Teste de carga do requisito não funcional:
// "Em dias de pico, o consolidado recebe 50 requisições por segundo com no máximo 5% de perda."
//
// Cenários (taxa fixa de chegada, independente de quanto o servidor demora a responder):
//   pico     50 req/s no GET /consolidado durante 5 minutos (o requisito)
//   folga   150 req/s no GET /consolidado durante 2 minutos, logo após o pico (3x o requisito)
//   escritas 10 req/s no POST /lancamentos durante o pico, para o consumidor trabalhar ao mesmo tempo
//   relatorio 10 req/s no GET /consolidado?inicio&fim (relatório de 30 dias) durante o pico
//
// Perda = resposta 5xx, timeout (mais de 2 s) ou conexão recusada. Qualquer resposta fora de 2xx conta.
// Rodar com: bash scripts/carga.sh

import http from 'k6/http';
import { check } from 'k6';

const CONSOLIDADO_URL = __ENV.CONSOLIDADO_URL || 'http://localhost:5002';
const LANCAMENTOS_URL = __ENV.LANCAMENTOS_URL || 'http://localhost:5001';
const API_KEY = __ENV.API_KEY || 'local-dev-key';
const DURACAO_PICO = __ENV.DURACAO_PICO || '5m';
const DURACAO_FOLGA = __ENV.DURACAO_FOLGA || '2m';
const RESULTADO = __ENV.RESULTADO || 'resultado-k6.md';

const DIAS_CONSULTADOS = 90; // as leituras se espalham pelos últimos 90 dias, então parte delas não está em cache
const TIMEOUT = '2s';
const headers = { 'X-Api-Key': API_KEY, 'Content-Type': 'application/json' };

export const options = {
  scenarios: {
    pico: {
      executor: 'constant-arrival-rate',
      exec: 'lerConsolidado',
      rate: 50,
      timeUnit: '1s',
      duration: DURACAO_PICO,
      preAllocatedVUs: 20,
      maxVUs: 100,
    },
    escritas: {
      executor: 'constant-arrival-rate',
      exec: 'registrarLancamento',
      rate: 10,
      timeUnit: '1s',
      duration: DURACAO_PICO,
      preAllocatedVUs: 5,
      maxVUs: 30,
    },
    relatorio: {
      executor: 'constant-arrival-rate',
      exec: 'lerRelatorio',
      rate: 10,
      timeUnit: '1s',
      duration: DURACAO_PICO,
      preAllocatedVUs: 5,
      maxVUs: 30,
    },
    folga: {
      executor: 'constant-arrival-rate',
      exec: 'lerConsolidado',
      rate: 150,
      timeUnit: '1s',
      duration: DURACAO_FOLGA,
      startTime: DURACAO_PICO,
      preAllocatedVUs: 50,
      maxVUs: 300,
    },
  },
  thresholds: {
    // O requisito: no máximo 5% de perda no pico. O p95 é o SLO de latência que definimos.
    'http_req_failed{scenario:pico}': ['rate<0.05'],
    'http_req_duration{scenario:pico}': ['p(95)<200'],
    // Na folga exigimos o mesmo limite de perda, com uma latência mais tolerante.
    'http_req_failed{scenario:folga}': ['rate<0.05'],
    'http_req_duration{scenario:folga}': ['p(95)<500'],
    // Escritas nunca devem falhar por causa da carga de leitura.
    'http_req_failed{scenario:escritas}': ['rate<0.01'],
    'http_req_duration{scenario:escritas}': ['p(95)<500'],
    // O relatório de 30 dias segue o mesmo SLO da consulta de um dia.
    'http_req_failed{scenario:relatorio}': ['rate<0.05'],
    'http_req_duration{scenario:relatorio}': ['p(95)<200'],
    // Sem threshold o k6 não separa as métricas por cenário no resumo, então pedimos as contagens aqui.
    'http_reqs{scenario:pico}': ['count>0'],
    'http_reqs{scenario:folga}': ['count>0'],
    'http_reqs{scenario:escritas}': ['count>0'],
    'http_reqs{scenario:relatorio}': ['count>0'],
  },
};

function diaAleatorio() {
  const dia = new Date();
  dia.setUTCDate(dia.getUTCDate() - Math.floor(Math.random() * DIAS_CONSULTADOS));
  return dia.toISOString().slice(0, 10);
}

export function lerConsolidado() {
  const resposta = http.get(`${CONSOLIDADO_URL}/consolidado/${diaAleatorio()}`, { headers, timeout: TIMEOUT });
  check(resposta, { 'consolidado 200': (r) => r.status === 200 });
}

export function lerRelatorio() {
  const fim = new Date();
  fim.setUTCDate(fim.getUTCDate() - Math.floor(Math.random() * DIAS_CONSULTADOS));
  const inicio = new Date(fim);
  inicio.setUTCDate(inicio.getUTCDate() - 29);
  const periodo = `inicio=${inicio.toISOString().slice(0, 10)}&fim=${fim.toISOString().slice(0, 10)}`;
  const resposta = http.get(`${CONSOLIDADO_URL}/consolidado?${periodo}`, { headers, timeout: TIMEOUT });
  check(resposta, { 'relatorio 200': (r) => r.status === 200 });
}

export function registrarLancamento() {
  const tipo = Math.random() < 0.7 ? 'Credito' : 'Debito';
  const valor = Math.round((1 + Math.random() * 500) * 100) / 100;
  const corpo = JSON.stringify({ data: diaAleatorio(), tipo, valor, descricao: 'teste de carga' });
  const resposta = http.post(`${LANCAMENTOS_URL}/lancamentos`, corpo, { headers, timeout: TIMEOUT });
  check(resposta, { 'lancamento 201': (r) => r.status === 201 });
}

// Gera um resumo em Markdown, salvo em docs/carga, além do resumo padrão no terminal.
export function handleSummary(data) {
  const metrica = (nome) => data.metrics[nome]?.values ?? {};
  const passou = (nome) => Object.values(data.metrics[nome]?.thresholds ?? {}).every((t) => t.ok);
  const ms = (v) => (v === undefined ? '-' : `${v.toFixed(1)} ms`);
  const pct = (v) => (v === undefined ? '-' : `${(v * 100).toFixed(2)}%`);

  const linha = (cenario, taxa) => {
    const reqs = metrica(`http_reqs{scenario:${cenario}}`).count ?? 0;
    const falhas = metrica(`http_req_failed{scenario:${cenario}}`);
    const duracao = metrica(`http_req_duration{scenario:${cenario}}`);
    const ok = passou(`http_req_failed{scenario:${cenario}}`) && passou(`http_req_duration{scenario:${cenario}}`);
    return `| ${cenario} | ${taxa} | ${reqs} | ${pct(falhas.rate)} | ${ms(duracao.avg)} | ${ms(duracao['p(95)'])} | ${ms(duracao.max)} | ${ok ? 'OK' : 'FALHOU'} |`;
  };

  const descartadas = metrica('dropped_iterations').count ?? 0;
  const markdown = [
    '# Resultado do teste de carga',
    '',
    `Executado em ${new Date().toISOString()} com k6, passando pelo nginx, com 2 réplicas de cada API. Pico de ${DURACAO_PICO} e folga de ${DURACAO_FOLGA}.`,
    '',
    '| Cenário | Taxa alvo | Requisições | Perda | Média | p95 | Máximo | Thresholds |',
    '|---|---|---|---|---|---|---|---|',
    linha('pico', '50 req/s GET'),
    linha('folga', '150 req/s GET'),
    linha('escritas', '10 req/s POST'),
    linha('relatorio', '10 req/s GET 30 dias'),
    '',
    `Iterações descartadas pelo k6 por falta de VUs: ${descartadas}.`,
    '',
    'Perda é qualquer resposta fora de 2xx, timeout acima de 2 s ou conexão recusada.',
    'Thresholds: perda abaixo de 5% no pico, na folga e no relatório, abaixo de 1% nas escritas.',
    'Latência p95 abaixo de 200 ms no pico e no relatório, e abaixo de 500 ms na folga e nas escritas.',
    '',
  ].join('\n');

  return {
    [RESULTADO]: markdown,
    stdout: markdown,
  };
}
