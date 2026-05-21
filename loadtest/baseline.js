// Sustained moderate load — 20 VUs for 30s. The primary measurement of
// steady-state p99 under realistic concurrency. Thresholds reflect the
// Rinha SLA (p99 ≤ 1 ms) loosened by the HAProxy + network roundtrip
// overhead (~1–2 ms typical inside the bridge network).

import http from 'k6/http';
import { check } from 'k6';

const PAYLOADS = JSON.parse(
  open('/work/resources/example-payloads.json'),
);
const BASE = __ENV.BASE_URL || 'http://localhost:9999';

export const options = {
  scenarios: {
    baseline: {
      executor: 'constant-vus',
      vus: 20,
      duration: '30s',
    },
  },
  thresholds: {
    http_req_duration: ['p(50)<3', 'p(95)<5', 'p(99)<10'],
    http_req_failed: ['rate<0.001'],
    checks: ['rate>0.999'],
  },
  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'p(99.9)', 'max'],
};

export default function () {
  const p = PAYLOADS[Math.floor(Math.random() * PAYLOADS.length)];
  const res = http.post(
    `${BASE}/fraud-score`,
    JSON.stringify(p),
    { headers: { 'Content-Type': 'application/json' } },
  );
  check(res, {
    'status 200': (r) => r.status === 200,
    'has fraud_score': (r) => r.json('fraud_score') !== undefined,
  });
}
