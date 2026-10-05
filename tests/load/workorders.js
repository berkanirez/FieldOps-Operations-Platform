// Day 117: first load test for FieldOps.Api — run locally with k6 in Docker,
// never against a cloud environment. Each virtual user (VU) repeatedly reads
// organization 1's first page of work orders and its status report, the two
// reads behind the Angular list and dashboard.
//
// Usage (API running on the host, port 5299):
//   docker run --rm -i -e VUS=50 -e DURATION=30s grafana/k6 run - < tests/load/workorders.js
// VUS/DURATION select a constant load; without them the ramp in `stages` runs.
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://host.docker.internal:5299';
const HEADERS = { 'X-Organization-Id': '1', 'X-Employee-Id': '1' };

const load = __ENV.VUS
  ? { vus: Number(__ENV.VUS), duration: __ENV.DURATION || '30s' }
  : {
      stages: [
        { duration: '30s', target: 10 },
        { duration: '30s', target: 50 },
        { duration: '30s', target: 100 },
        { duration: '10s', target: 0 },
      ],
    };

export const options = {
  ...load,
  // Pass/fail criteria: k6 exits non-zero when one is crossed, so the same
  // script can gate a pipeline the way scripts/smoke-test.sh does.
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{endpoint:list}': ['p(95)<500'],
    'http_req_duration{endpoint:report}': ['p(95)<500'],
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const list = http.get(`${BASE_URL}/api/workorders?page=1&pageSize=50`, {
    headers: HEADERS,
    tags: { endpoint: 'list' },
  });
  check(list, { 'list returns 200': (r) => r.status === 200 });

  const report = http.get(`${BASE_URL}/api/workorders/report`, {
    headers: HEADERS,
    tags: { endpoint: 'report' },
  });
  check(report, { 'report returns 200': (r) => r.status === 200 });

  // A short pause between iterations, like a user reading the page.
  sleep(0.5);
}
