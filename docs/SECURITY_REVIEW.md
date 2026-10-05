# FieldOps Security Self-Review

**Scope:** `FieldOps.Api` and its local infrastructure (Docker Compose), at commit `7206c0f`.
**Date:** October 2026.
**Method:** code review plus live probes against a locally running instance (never against a deployed environment), mapped to the OWASP Top 10 (2021).

This is a self-review written as part of a learning project. It is not a professional penetration test. Every "open" finding below has a reproducible request and its real result; every "protected" finding has evidence too.

## Remediation status

| Finding | Status | Change |
|---|---|---|
| F1 Client-asserted identity | **Fixed** | Every endpoint requires a valid bearer token (fallback authorization policy), and every employee-facing controller reads the employee and organization from validated token claims; the identity headers are ignored everywhere. Re-running the original probes: headers-only requests → 401; a valid organization 1 token with a forged `X-Organization-Id: 2` creates the work order (or employee) in organization 1. Regression tests cover both, including the Day 38/39 employee exploits. |
| F13 Organization list exposed every tenant | **Found and fixed (Day 123)** | `GET /api/organizations` returned every organization's name to any caller — missed in the first pass of this review. It now returns only the caller's organization; another organization's id → 404 (integration tests). |
| F3 JWT never required | **Fixed** | Fallback policy `RequireAuthenticatedUser`. Anonymous only for login, health checks, OpenAPI and — deliberately — customer approval (see below). Tampered tokens and tokens signed with another key → 401 (covered by integration tests). |
| F4 Public fallback signing key | **Fixed** | The hard-coded key is removed. The app refuses to start when `Jwt:SigningKey` is missing (verified in Production mode). Docker Compose and Kubernetes run the demo in Development, which uses the demo key from `appsettings.Development.json`; a real deployment must supply `Jwt__SigningKey` from a secret store. |
| F2 Credential-less login + enumeration | **Fixed** | Login requires a password, verified against a PBKDF2 hash (ASP.NET Core `PasswordHasher`) stored per employee; a plain password is never stored. An unknown employee and a wrong password return the same 401 body, and both paths run the same hash verification, so response time doesn't reveal which ids exist (measured: 10 interleaved requests each, averages 134 ms vs 152 ms, overlapping ranges). New employees must be created with a password (minimum 12 characters). |
| F5 Header-string rate-limit bypass | **Fixed** | The create limit is partitioned by the organization claim of the validated token; a rewritten `X-Organization-Id` changes nothing (integration test). |
| F6 Cross-tenant quota exhaustion | **Fixed** | The rate limiter runs after authentication/authorization, so unauthenticated or rejected requests never spend a tenant's quota, and one organization exhausting its limit leaves another untouched (integration test). |
| F7 Unlimited login | **Fixed, with a caveat** | 5 login attempts per minute per client address (429 afterwards; integration test and live check). Behind a reverse proxy, the forwarded-headers middleware must be configured with the known proxies first, or all users share one bucket. |
| Customer approval | **Open** | `POST /api/workorders/{id}/approve` stays `[AllowAnonymous]` and still trusts `X-Customer-Id`, because customers cannot log in yet. |
| F10 No production exception handler | **Fixed** | `AddProblemDetails` + `UseExceptionHandler` first in the pipeline: an unhandled exception returns a 500 ProblemDetails with a `traceId` and no exception message or stack trace (integration test; it fails when the handler is removed). |
| F11 Floating package versions | **Fixed** | `System.ServiceModel.*` pinned to `4.10.3`, the version the `4.10.*` range had resolved to; vulnerability scan still clean. |
| F12 No security-event logging | **Fixed** | Failed and successful logins and rate-limit rejections are logged under the `FieldOps.Security` category with a structured `SecurityEvent` field (`LoginFailed`, `LoginSucceeded`, `RateLimitExceeded`); the attempted password is never logged (integration tests). Alerting on them is not configured. |
| F8 Local infrastructure without authentication | Open (local only) | Redis, RabbitMQ and Elasticsearch in Docker Compose remain unauthenticated; must be secured before any shared or deployed use. |
| Account management | Out of scope | No password policy beyond length, no reset, lockout or MFA — production systems usually delegate to an identity provider. |

## Summary

The application is robust and well tested in its business behaviour. **Its main weakness is identity:** every request states who it is in plain headers, and nothing verifies that claim. Tenant isolation, input validation and SQL safety work as designed, but all of them assume an honest caller.

| OWASP 2021 | Status | Findings |
|---|---|---|
| A01 Broken Access Control | **Open** | F1, F3 |
| A02 Cryptographic Failures | **Open** | F4 |
| A03 Injection | Protected | P-1 |
| A04 Insecure Design | **Open** | F5, F6 |
| A05 Security Misconfiguration | Partial | F8, F9, F10 |
| A06 Vulnerable and Outdated Components | Protected (with a note) | P-4, F11 |
| A07 Identification and Authentication Failures | **Open** | F1, F2, F7 |
| A08 Software and Data Integrity Failures | Partial | F11 |
| A09 Security Logging and Monitoring Failures | Partial | F12 |
| A10 Server-Side Request Forgery | Not applicable | No outbound request is built from user input |

## Findings

Severity reflects impact in this application if it were exposed to untrusted users.

### F1 — Identity is asserted by the client, not verified (Critical · A01, A07)

Every endpoint identifies the caller from `X-Organization-Id` and `X-Employee-Id` request headers. Anyone who can reach the API can claim to be any employee of any organization, including an Admin.

```bash
# No token, no password — just claim to be organization 2's Admin (employee 3)
curl -X POST http://localhost:5299/api/workorders \
  -H "X-Organization-Id: 2" -H "X-Employee-Id: 3" \
  -H "Content-Type: application/json" -d '{"title":"..."}'
# -> 201 Created

# Organization 1's work order, requested with organization 2's honest headers -> 404 (isolation works)...
# ...and with forged organization 1 headers -> 200 (isolation bypassed)
```
The tenant checks (404 for another organization's work order, 403 for a role mismatch) are correct, but they are only as trustworthy as the headers they read.

### F2 — Login issues a token without any credential, and reveals which employees exist (Critical · A07)

`POST /api/auth/login` accepts only an employee id. Any id that exists receives a signed one-hour JWT carrying that employee's organization and role. An unknown id returns a different response, so valid ids can be enumerated.

```bash
curl -X POST http://localhost:5299/api/auth/login -H "Content-Type: application/json" -d '{"employeeId":3}'
# -> 200 {"token":"eyJhbGciOiJIUzI1NiIs...","role":"Admin",...}
curl -X POST http://localhost:5299/api/auth/login -H "Content-Type: application/json" -d '{"employeeId":999}'
# -> 404 "Employee 999 does not exist."
```

### F3 — JWT validation is configured but never required (High · A01, A07)

`AddJwtBearer` and `UseAuthentication` are registered, but no controller or endpoint has `[Authorize]`, and controllers never read token claims. Every request in F1 succeeded with no `Authorization` header at all. A token is therefore neither needed nor used for any decision.

### F4 — Public fallback signing key; no signing key in production configuration (High · A02)

`Program.cs` and `AuthController.cs` fall back to a hard-coded signing key when `Jwt:SigningKey` is not configured. That string is in the public repository. `appsettings.json` (Production) has no `Jwt` section, so in Production — including the October 2026 Azure demo — tokens were signed with this public key. Once F3 is fixed, anyone could mint a valid token for any identity unless the key is supplied from a secret store.

### F5 — Rate limit can be bypassed by rewriting the header (Medium · A04)

The create rate limit (5 per 10 seconds) is partitioned by the raw `X-Organization-Id` string. ASP.NET Core model binding parses `01` as organization 1, but the limiter treats `"01"` as a separate bucket.

```bash
# 6 creates with "X-Organization-Id: 1"   -> 201 201 201 201 201 429
# 7th create with "X-Organization-Id: 01" -> 201   (same organization, fresh bucket)
```

### F6 — Unauthenticated requests can exhaust another organization's limit (Medium · A04)

The rate limiter runs before the membership check, and it counts rejected requests. Requests claiming organization 2 with a non-existent employee are rejected with 400, but they still consume organization 2's quota. Organization 2's real Admin is then refused.

```bash
# 5 creates as "org 2 / employee 999" -> 400 400 400 400 400
# org 2's real Admin (employee 3)     -> 429 Too Many Requests
```

### F7 — Only Create is rate limited; login is not (Medium · A07)

Nothing limits `POST /api/auth/login`. Combined with F2, enumeration is unbounded, and a future password check would face unlimited guessing.

### F8 — Local infrastructure runs without authentication (Low in this setup · A05)

In `docker-compose.yml`: Redis has no password; RabbitMQ uses the image's default `guest/guest` account, with its management UI published on host port 15672; Elasticsearch runs with `xpack.security.enabled: "false"`. All three are local development services only. None was deployed to Azure. They must be secured before any shared or deployed use.

### F9 — Development signing key committed (Low · A05)

`appsettings.Development.json` contains a JWT signing key. It is clearly labelled as a demo value and is only used in Development, but it must never be reused elsewhere.

### F10 — No production exception handler (Low · A05)

There is no `UseExceptionHandler` or `AddProblemDetails`. Validation errors already return RFC 9110 ProblemDetails without internal detail (P-2). An unhandled exception in Production, however, returns a bare 500 instead of a consistent ProblemDetails body. Development intentionally shows the detailed exception page.

### F11 — Floating package versions (Info · A06, A08)

`FieldOps.Api.csproj` references the `System.ServiceModel.*` packages as `4.10.*`. Builds are therefore not fully reproducible, and a new patch version could change behaviour without review. Container images are published by CI without signing.

### F12 — Security events are not logged distinctly (Low · A09)

Structured JSON logs with correlation IDs exist (and Day 109 showed them queryable in Log Analytics). However, failed logins, 403s and rate-limit rejections are not recorded as security events that could be alerted on.

## What is protected (with evidence)

- **P-1 Injection (A03):** the title `Day120 probe '); DROP TABLE WorkOrders;--` was stored verbatim and the table was untouched (list still 200). There is no raw-SQL API in the code (`FromSqlRaw`, `ExecuteSqlRaw` and similar). EF Core sends parameters such as `@organizationId`.
- **P-2 Input validation:** malformed JSON returns 400. A 201-character title returns 400 ProblemDetails (`"Title": ["... maximum length of 200."]`) with no stack trace or internals.
- **P-3 Tenant isolation logic:** another organization's work order returns 404, not 403, so ids of other tenants cannot be confirmed (Day 115). Queries filter by organization in SQL (Day 113).
- **P-4 Dependencies (A06):** `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages in any of the 8 projects. `npm audit --omit=dev` reports 0 vulnerabilities in the Angular app.
- **Other:** CORS allows exactly one configured origin, not `*`. Request and response DTOs are explicit (no over-posting into entities). Connection strings and passwords come from environment variables / Kubernetes and Container Apps secrets, never from tracked files. In the Azure demo the API had internal-only ingress.

## Remediation plan (priority order)

1. **Require authentication everywhere** (F1, F3): add `[Authorize]` (a fallback authorization policy), read the organization and employee from validated token claims, and stop trusting the identity headers.
2. **Real credentials at login** (F2, F7): verify a hashed password or delegate to an identity provider; return the same response for unknown users and wrong passwords; rate limit login.
3. **Signing key from a secret store** (F4): remove the hard-coded fallback; fail at startup when `Jwt:SigningKey` is missing outside Development.
4. **Rate limit the authenticated tenant** (F5, F6): partition by the organization claim after authentication, so forged or rejected requests cannot spend another tenant's quota.
5. **Production error handling** (F10): `AddProblemDetails` + `UseExceptionHandler`.
6. **Harden infrastructure before any deployment** (F8): Redis password/ACL, non-default RabbitMQ credentials, Elasticsearch security on.
7. **Supply chain** (F11) and **security event logging** (F12).
