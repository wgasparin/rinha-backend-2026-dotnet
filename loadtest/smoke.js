// Correctness smoke test — 1 VU for 10s. Used to verify the stack responds
// before running real perf scenarios. Tight thresholds catch regressions
// in JSON shape or response codes.

import http from 'k6/http';
import { check } from 'k6';

const PAYLOADS = JSON.parse(
  open('/work/resources/example-payloads.json'),
);
const BASE = __ENV.BASE_URL || 'http://localhost:9999';

export const options = {
  vus: 1,
  duration: '10s',
  thresholds: {
    http_req_duration: ['p(99)<20'],
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
  },
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
    'has approved': (r) => r.json('approved') !== undefined,
    'fraud_score is multiple of 0.2': (r) => {
      const s = r.json('fraud_score');
      return [0, 0.2, 0.4, 0.6, 0.8, 1].some((v) => Math.abs(s - v) < 1e-6);
    },
  });
}
