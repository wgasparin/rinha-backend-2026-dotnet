// Stress test — ramp from 1 to 100 VUs over 45s, hold for 30s, then drain.
// Useful for finding the throughput ceiling and seeing how p99 degrades
// as concurrency climbs past what the 0.45 CPU per API can handle.

import http from 'k6/http';
import { check } from 'k6';

const PAYLOADS = JSON.parse(
  open('/work/resources/example-payloads.json'),
);
const BASE = __ENV.BASE_URL || 'http://localhost:9999';

export const options = {
  scenarios: {
    stress: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '15s', target: 50 },
        { duration: '30s', target: 100 },
        { duration: '30s', target: 100 },
        { duration: '15s', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_duration: ['p(99)<50'],
    http_req_failed: ['rate<0.01'],
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
  check(res, { 'status 200': (r) => r.status === 200 });
}
