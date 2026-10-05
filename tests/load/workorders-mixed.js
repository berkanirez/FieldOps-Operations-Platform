// Day 119: mixed read/write load — two groups of virtual users at once.
// Readers repeat Day 117's list + report reads; writers walk a work order
// through its whole life: create (Admin) -> assign to the Member -> start
// and complete (as that Member). Run locally with k6 in Docker, never
// against a cloud environment.
//
// The per-organization rate limit on Create (Day 53: 5 per 10 s) would turn
// most writes into 429s, so the API under test runs with
// RateLimiting__PerOrganization__PermitLimit raised — a test-environment
// setting, not a code change. Created work orders are titled
// "Day119-load-..." so they can be deleted afterwards.
//
// Usage: docker run --rm -i grafana/k6 run - < tests/load/workorders-mixed.js
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://host.docker.internal:5299';
const ADMIN = { 'X-Organization-Id': '1', 'X-Employee-Id': '1', 'Content-Type': 'application/json' };
const MEMBER = { 'X-Organization-Id': '1', 'X-Employee-Id': '2', 'Content-Type': 'application/json' };

export const options = {
  scenarios: {
    readers: { executor: 'constant-vus', exec: 'read', vus: Number(__ENV.READERS || 50), duration: __ENV.DURATION || '30s' },
    writers: { executor: 'constant-vus', exec: 'write', vus: Number(__ENV.WRITERS || 20), duration: __ENV.DURATION || '30s' },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{kind:read}': ['p(95)<500'],
    'http_req_duration{kind:write}': ['p(95)<1000'],
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

export function read() {
  const list = http.get(`${BASE_URL}/api/workorders?page=1&pageSize=50`, { headers: ADMIN, tags: { kind: 'read' } });
  check(list, { 'list returns 200': (r) => r.status === 200 });
  const report = http.get(`${BASE_URL}/api/workorders/report`, { headers: ADMIN, tags: { kind: 'read' } });
  check(report, { 'report returns 200': (r) => r.status === 200 });
  sleep(0.5);
}

export function write() {
  const params = (headers) => ({ headers, tags: { kind: 'write' } });

  const created = http.post(`${BASE_URL}/api/workorders`, JSON.stringify({ title: `Day119-load-${__VU}-${__ITER}` }), params(ADMIN));
  if (!check(created, { 'create returns 201': (r) => r.status === 201 })) {
    return;
  }
  const id = created.json('id');

  const assigned = http.post(`${BASE_URL}/api/workorders/${id}/assign`, JSON.stringify({ employeeId: 2 }), params(ADMIN));
  check(assigned, { 'assign returns 200': (r) => r.status === 200 });

  const started = http.post(`${BASE_URL}/api/workorders/${id}/start`, null, params(MEMBER));
  check(started, { 'start returns 200': (r) => r.status === 200 });

  const completed = http.post(`${BASE_URL}/api/workorders/${id}/complete`, null, params(MEMBER));
  check(completed, { 'complete returns 200': (r) => r.status === 200 });

  sleep(0.5);
}
