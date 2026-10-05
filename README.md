# FieldOps Operations Platform

A .NET 10 backend portfolio built around **FieldOps**, a multi-tenant field-service management platform, plus two smaller supporting applications. FieldOps starts as a modular monolith and grows into a distributed system with messaging, search, observability, an Angular frontend, and Kubernetes deployment manifests.

## Projects

| Project | Type | Highlights |
|---|---|---|
| **FieldOps** | Modular monolith + extracted service + Angular frontend | Multi-tenancy, database-per-module, Redis caching, RabbitMQ with outbox/inbox, Elasticsearch, OpenTelemetry, Polly resilience, JWT, Docker, Kubernetes |
| **StockPilot** | ASP.NET Core Web API | JWT access/refresh tokens with rotation, role- and policy-based authorization, optimistic concurrency, Testcontainers integration tests |
| **RoadmapOS** | ASP.NET Core MVC | Server-rendered CRUD, EF Core relationships, a progress dashboard |

## Tech stack

* .NET 10, ASP.NET Core (controller-based APIs, MVC), C# with nullable reference types
* Entity Framework Core, SQL Server
* Redis, RabbitMQ, Elasticsearch
* OpenTelemetry (tracing, metrics), Polly (timeouts, circuit breaker)
* xUnit, Testcontainers
* Angular 22 (standalone components, signals, reactive forms)
* Docker, Docker Compose, Kubernetes, GitHub Actions

---

## FieldOps

FieldOps manages organizations, employees, customers, and work orders for field-service teams.

### Architecture

* **Modular monolith** — five modules (Organizations, Employees, Customers, Work Orders, Audit Logs), each a separate class library with `internal` domain entities and its **own SQL Server database**, exposing only a public interface and DTOs to the host API. Cross-module references are validated in application code, not by foreign keys (see [ADR 0003](docs/adr/0003-database-per-module.md)).
* **Work order lifecycle** — Open → Assigned → InProgress → Completed, with reassignment, reopening, and customer approval, guarded by tenant isolation and role/ownership checks on every action.
* **Caching** — a Redis cache-aside status report with explicit invalidation and a background cache warmer.
* **Messaging** — completing a work order writes an **outbox** row in the same transaction as the state change; a background publisher delivers it to RabbitMQ and retries until it succeeds. Consumers use an **inbox** table for idempotency, exponential-backoff reconnects, and a dead-letter queue for unprocessable messages.
* **Notification service** — a separately deployable .NET worker service (`FieldOps.NotificationService`) with no compile-time dependency on the API, consuming work-order events over RabbitMQ ([ADR 0005](docs/adr/0005-notification-service-boundary.md)).
* **Search** — work orders are mirrored into Elasticsearch (via the same outbox) for tenant-filtered full-text search, with a per-organization index rebuild endpoint. SQL Server remains the source of truth.
* **External integration** — a SOAP service consumed behind an anti-corruption layer ([ADR 0009](docs/adr/0009-anti-corruption-layer.md)).
* **Observability and resilience** — structured JSON logging with correlation IDs, OpenTelemetry distributed tracing propagated through RabbitMQ message headers, custom metrics (including outbox publish lag), `/health/live` and `/health/ready` endpoints, and a Polly timeout + circuit breaker around Elasticsearch.
* **Cross-cutting** — per-organization rate limiting, idempotency keys, audit logging, and an `IAiProvider` abstraction for summarizing work-order evidence notes.

Architecture decisions are recorded in [docs/adr](docs/adr).

### Running it with Docker Compose

```
cp .env.example .env
# edit .env and set a real SA_PASSWORD
docker compose up --build -d
```

This starts SQL Server, Redis, RabbitMQ, Elasticsearch, the API, and the notification service. Apply migrations for each of the five modules against the containerized SQL Server (exposed on `localhost,14330`):

```
cd src/FieldOps.Modules.Organizations && dotnet ef database update --connection "Server=localhost,14330;Database=FieldOpsOrganizations;User Id=sa;Password=<your SA_PASSWORD>;TrustServerCertificate=True;" && cd ../..
# ...same pattern for FieldOps.Modules.Employees, FieldOps.Modules.WorkOrders, FieldOps.Modules.Customers, FieldOps.Modules.AuditLogs
```

Then visit:

* `http://localhost:5190/health/ready` — dependency health
* `http://localhost:5190/api/organizations` — seeded organizations
* `http://localhost:5190/api/workorders` — requires `X-Organization-Id` and `X-Employee-Id` headers (see below)

Tear down with `docker compose down`.

### Seeded demo identities

Requests identify the caller with an `X-Organization-Id` header and, for employee actions, an `X-Employee-Id` header (or `X-Customer-Id` for customer approval).

| Organization | Employee (Admin) | Employee (Member) | Customer |
|---|---|---|---|
| 1 | id `1` | id `2` | id `1` |
| 2 | id `3` | id `4` | — |

Example:

```
curl -X POST http://localhost:5190/api/workorders \
  -H "X-Organization-Id: 1" -H "X-Employee-Id: 1" -H "Content-Type: application/json" \
  -d '{"Title":"Fix the HVAC unit"}'

curl http://localhost:5190/api/workorders/report -H "X-Organization-Id: 1" -H "X-Employee-Id: 1"
```

### Angular frontend (`src/fieldops-web`)

An Angular 22 app with work order list, detail, create, dashboard, and login screens. It uses standalone components, signals (the app is zoneless), reactive forms, client-side routing, and an HTTP interceptor that attaches a JWT from `POST /api/auth/login` to outgoing requests.

The app calls the API through relative `/api/...` URLs, so it contains no API address and the same build works in every environment:

* **Local development** — `npm start` proxies `/api` to `http://localhost:5138` (see `proxy.conf.json`). Run the API first (`cd src/FieldOps.Api && dotnet run`):

  ```
  cd src/fieldops-web
  npm install
  npm start
  ```

  Then open `http://localhost:4200`.
* **Container** — nginx serves the app and reverse-proxies `/api` to the address in the `API_UPSTREAM` environment variable (rendered into the nginx config at container start from `nginx.conf.template`). Without it, the container still starts and `/api` returns `502`.

### Kubernetes (`k8s/`)

* **Frontend** — a Deployment (liveness and readiness probes), a ClusterIP Service, and a HorizontalPodAutoscaler scaling between 2 and 6 replicas on CPU utilization (requires metrics-server). The image is a multi-stage build (Node build stage → nginx) with a client-side-routing fallback.
* **API** — a Deployment and Service configured through a ConfigMap (non-secret settings) and a Secret (connection strings). Readiness targets `/health/ready` (SQL Server and Redis reachability), liveness targets `/health/live` (no external checks), so a dependency outage takes the Pod out of traffic without restarting it. The dependencies run under Docker Compose on the host and are reached via `host.docker.internal`.

```
# frontend
docker build -t fieldops-web:day105 src/fieldops-web
kubectl apply -f k8s/fieldops-web-deployment.yaml -f k8s/fieldops-web-service.yaml -f k8s/fieldops-web-hpa.yaml

# API (dependencies first: docker compose up -d sqlserver redis rabbitmq elasticsearch, then migrations)
docker build -t fieldops-api:day101 .
kubectl create secret generic fieldops-api-secrets \
  --from-literal=ConnectionStrings__FieldOpsWorkOrdersDb="Server=host.docker.internal,14330;Database=FieldOpsWorkOrders;User Id=sa;Password=<SA_PASSWORD>;TrustServerCertificate=True;"
  # ...one --from-literal per module (Organizations, Employees, WorkOrders, Customers, AuditLogs)
kubectl apply -f k8s/fieldops-api-configmap.yaml -f k8s/fieldops-api-deployment.yaml
kubectl port-forward svc/fieldops-api 8084:80
```

The Secret is created from the command line on purpose: Kubernetes Secrets are only base64-encoded, so no Secret manifest is committed.

* **Ingress** — a single entry point: `/api/...` routes to the API, everything else to the frontend.

```
# requires an ingress controller; the local setup used ingress-nginx:
kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.15.1/deploy/static/provider/cloud/deploy.yaml
kubectl apply -f k8s/fieldops-ingress.yaml
curl http://localhost/api/organizations
```

ingress-nginx was announced for retirement by the Kubernetes project (March 2026); it is used here only on a local cluster. The Ingress rules themselves are controller-agnostic.

### Running the tests

```
dotnet test FieldOps.slnx          # backend: HTTP integration tests against Testcontainers SQL Server (Docker required)
cd src/fieldops-web && npx ng test # frontend: unit tests (Vitest)
```

### Azure deployment

FieldOps was deployed to **Azure Container Apps** as a demo (October 2026), using the images CI publishes to GHCR, then torn down at the end of the free trial. The setup:

* **Frontend** (`fieldops-web`) — external HTTPS ingress, scales to zero when idle; `API_UPSTREAM=http://fieldops-api` points its nginx reverse proxy at the API.
* **API** (`fieldops-api`) — **internal ingress only**: not reachable from the internet, only through the frontend's `/api`. Connection strings are Container Apps secrets.
* **Database** — five Azure SQL serverless databases (one per module) on the free offer, configured to auto-pause rather than bill when the free monthly limit is exhausted.
* **Migrations** — applied as idempotent SQL scripts generated by `dotnet ef migrations script --idempotent` (safe to re-run; already-applied migrations are skipped).

Redis, RabbitMQ, and Elasticsearch are intentionally not deployed (to stay within free limits): the API keeps working without them, but caching, messaging, notifications, and search are inactive there.

**Deploying a new version and rolling back.** Every image is tagged with its commit SHA, so a deploy and a rollback are the same command with a different tag; each creates a new Container Apps revision:

```bash
az containerapp update --name fieldops-api --resource-group <rg> --image ghcr.io/berkanirez/fieldops-api:<sha>
```

**Verifying a deployment.** [`scripts/smoke-test.sh`](scripts/smoke-test.sh) checks the frontend, an Angular deep link, the API with its database, and the report endpoint through the public URL. It retries to ride out scale-to-zero cold starts, checks response bodies as well as status codes, and exits `0` (healthy) or `1` (failed):

```bash
scripts/smoke-test.sh https://<frontend-app>.<environment>.azurecontainerapps.io
```

The same script can be run from GitHub: **Actions → Smoke test → Run workflow** ([`.github/workflows/smoke-test.yml`](.github/workflows/smoke-test.yml)) takes the URL as input and fails the job when any check fails.

Logs from the containers go to a Log Analytics workspace (queryable with KQL), and Container Apps exposes request and replica metrics.

### CI/CD

GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs on every push and pull request:

* **Backend** — builds and tests all three solutions, builds the API image, starts the full Docker Compose stack, applies migrations, and checks `/health/ready`.
* **Frontend** — `npm ci`, unit tests, and a production build of `src/fieldops-web`.
* **Publish** (pushes to `master` only, after both test jobs pass) — builds the API and frontend images and pushes them to GitHub Container Registry as `ghcr.io/berkanirez/fieldops-api` and `ghcr.io/berkanirez/fieldops-web`, each tagged with the short commit SHA and `latest`.

### Known limitations

* JWT issuance exists, but no endpoint enforces it yet — requests are still identified by unverified `X-Organization-Id` / `X-Employee-Id` headers. Login takes only an employee id (no password), and role-based hiding in the frontend is a UI convenience, not authorization.
* The JWT signing key is in `appsettings.Development.json` (development only).
* The AI provider is a deterministic fake; evidence "attachments" are plain text notes.
* In Kubernetes, the API's dependencies (SQL Server, Redis, RabbitMQ, Elasticsearch) still run outside the cluster under Docker Compose, and the notification service is not deployed to the cluster yet.
* Migrations are applied by hand rather than by a dedicated migration job.
* Deployments to Azure are run by hand (`az containerapp update` followed by the smoke test) rather than by a CI/CD deploy step.
* EF Core retries transient SQL failures (`EnableRetryOnFailure`) in the API's modules, but if a work-order create's commit succeeds and only the acknowledgement is lost, the retry inserts a duplicate — the `Idempotency-Key` header deduplicates client retries, not this server-side re-run (that would need a database-level check). The separate notification service does not retry yet.

---

## StockPilot Inventory API

A controller-based ASP.NET Core Web API for product inventory: JWT authentication (access + refresh tokens, rotation), role-based and policy-based authorization, EF Core + SQL Server, optimistic concurrency, transactions, and unit, mocked, and real HTTP integration tests (against a Testcontainers-managed SQL Server).

### Running it

Requires the .NET 10 SDK and a local SQL Server (`localhost\SQLEXPRESS` by default — see `src/StockPilot.Api/appsettings.Development.json`).

```
cd src/StockPilot.Api
dotnet ef database update
dotnet run
```

* `/scalar/v1` — interactive API documentation
* `/api/products` — list products (search, sort, paging)

Demo accounts for `POST /api/auth/login`:

| Username | Password | Role |
|---|---|---|
| `admin` | `Passw0rd!` | `Admin` |
| `employee` | `Employee123!` | `Employee` |

Deleting and bulk-creating products require an `Admin` access token.

### Running the tests

```
dotnet test StockPilot.slnx
```

Integration tests need Docker running.

### Known limitations

* Two hardcoded demo accounts; no user store, registration, or password reset.
* Refresh tokens are stored in memory (lost on restart, not shared across instances).
* Only the product domain exists; orders and warehouses are not built.

---

## RoadmapOS

A single-user ASP.NET Core MVC app for tracking skills (target and current levels), projects and milestones, and evidence records, with an overall and per-category progress dashboard.

### Running it

Requires the .NET 10 SDK and a local SQL Server (`localhost\SQLEXPRESS` by default — see `src/RoadmapOS.Web/appsettings.Development.json`).

```
cd src/RoadmapOS.Web
dotnet ef database update
dotnet run
```

Starter data is seeded automatically in `Development`. Pages: `/Skills`, `/Dashboard`.

### Running the tests

```
dotnet test RoadmapOS.slnx
```

### Known limitations

* Single user, no authentication.
* No delete flow for skills.
