// Spike test — 5 VUs baseline, sudden jump to 150, hold 20s, drop back.
// Exercises HAProxy queueing, container CPU throttle response, and recovery
// behavior. Failures here matter less than how fast p99 returns to baseline.

import http from 'k6/http';
import { check } from 'k6';

const PAYLOADS = JSON.parse(
  open('/work/resources/example-payloads.json'),
);
const BASE = __ENV.BASE_URL || 'http://localhost:9999';

export const options = {
  scenarios: {
    spike: {
      executor: 'ramping-vus',
      startVUs: 5,
      stages: [
        { duration: '10s', target: 5 },    // baseline
        { duration: '5s',  target: 150 },  // spike up (3-second rise effective)
        { duration: '20s', target: 150 },  // hold spike
        { duration: '5s',  target: 5 },    // spike down
        { duration: '10s', target: 5 },    // recovery
      ],
    },
  },
  thresholds: {
    http_req_duration: ['p(99)<100'],
    http_req_failed: ['rate<0.05'],
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
