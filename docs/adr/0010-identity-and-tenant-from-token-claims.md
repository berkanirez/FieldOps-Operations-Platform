# ADR 0010: Identity and Tenant Come Only From Validated Token Claims, and Every Endpoint Requires a Token by Default

## Status

Accepted — 2026-10-06 (Phase 6, Day 126). **Recorded retrospectively:** the decision was made and implemented on Days 121–123 (Week 21, security hardening); this ADR writes down the reasoning after the fact. In a team, it would have been written and reviewed before the change.

## Context

From Phase 3 until Day 121, FieldOps identified the caller with request headers: `X-Organization-Id` chose the tenant and `X-Employee-Id` chose the acting employee, whose role then decided what they could do. Day 93 added `POST /api/auth/login`, which issued a signed JWT, but no endpoint required it — the token was produced and never checked.

The Week 21 OWASP self-review ([SECURITY_REVIEW.md](../SECURITY_REVIEW.md)) showed what that meant in practice:

* **F1 — client-asserted identity:** any caller could send `X-Employee-Id: 1` and act as organization 1's Admin. The headers were the whole security model, and the client controls them.
* **F3 — JWT never required:** authentication existed in code but protected nothing.
* **F4 — public fallback signing key:** when `Jwt:SigningKey` was missing, a hard-coded key from the repository was used, so anyone could mint a valid token.
* **F2 — credential-less login:** login took only an employee id.
* **F5/F6 — rate limiting keyed on a header:** the per-organization limit could be bypassed by changing the header, or used to exhaust another tenant's quota.
* **F13 (found on Day 123):** `GET /api/organizations` returned every tenant's name to any caller.

A multi-tenant system's most important guarantee is that one tenant can never read or change another's data. None of the existing per-action role and ownership checks meant anything while the identity they checked was supplied by the caller.

## Decision

1. **Secure by default.** A fallback authorization policy (`RequireAuthenticatedUser`) applies to every endpoint that does not say otherwise. Anonymous access is an explicit, reviewable exception marked with `[AllowAnonymous]` / `.AllowAnonymous()`: login, the health checks, the OpenAPI document and — deliberately, as an open finding — customer approval. A new controller is protected without anyone remembering to protect it.
2. **Identity and tenant come only from validated token claims.** Controllers read the acting employee (`ClaimTypes.NameIdentifier`) and organization (`organizationId`) through `ClaimsPrincipalExtensions.GetEmployeeId()` / `GetOrganizationId()`. The old headers are no longer read anywhere for employees; the token's signature, issuer, audience and lifetime are validated before any claim is trusted.
3. **The database still has the final word on the employee.** A valid token for an employee who no longer exists returns 401; role and ownership checks (`ValidateIsAdminAsync`, `ValidateOwnershipAsync`) still run against the employee's current record, not only the role claim.
4. **Other tenants' data is indistinguishable from missing data.** Reading another organization's work order returns 404, not 403, so ids cannot be probed to learn what exists. `GET /api/organizations` returns only the caller's own organization.
5. **No insecure defaults for secrets.** The app refuses to start without `Jwt:SigningKey`; the key comes from configuration or a secret store, never from source code.
6. **Login is a real credential check.** Passwords are stored as PBKDF2 hashes (ASP.NET Core `PasswordHasher`). An unknown employee and a wrong password return the same 401, and the unknown-employee path verifies the password against a dummy hash (generated once per process) so both take similar time (no user enumeration by response or timing).
7. **Limits use the same trusted identity.** The rate limiter runs after authentication and authorization and partitions the create limit by the organization claim; login is limited per client address. Failed and successful logins and rate-limit rejections are logged as security events.

## Consequences

* **Positive:** Tenant isolation now rests on something the client cannot forge. The per-action checks written in Phases 3–4 finally protect what they were meant to protect.
* **Positive:** Forgetting `[Authorize]` on a new endpoint can no longer expose it; the mistake that remains possible — adding `[AllowAnonymous]` — is visible in a diff and in review.
* **Positive:** Every rule is pinned by integration tests that log in through the real endpoint (`TestAuth.AuthenticateAsAsync`) rather than forging tokens, so the tests exercise the same path as a client.
* **Cost:** Every client must log in first. The Angular app, the smoke test, the k6 scripts and the README examples all had to change, and one regression slipped through: on Day 121 the rate limiter was still keyed on a header nobody sent any more, so all traffic shared one bucket — caught and fixed on Day 122.
* **Cost:** Tokens are stateless and valid for an hour; a deleted employee is rejected by the database lookup, but a changed role is only re-read where a check queries the employee record. There is no token revocation and no refresh token in FieldOps (StockPilot demonstrates refresh-token rotation).
* **Open:** customer approval still trusts `X-Customer-Id` and `X-Organization-Id` headers because customers cannot log in yet. It is the one deliberate exception to rule 2, marked `[AllowAnonymous]` and listed as open in the security review.
* **Open:** the per-client login limit sees the proxy's address when the API sits behind a reverse proxy, unless forwarded headers are configured for trusted proxies only (F7 caveat).

## Alternatives Considered

* **Keep the headers and validate them against the token.** Rejected — two sources of the same fact invite the code to read the wrong one; a single source (the claims) removes the question.
* **Add `[Authorize]` to each controller instead of a fallback policy.** Rejected — protection would depend on remembering it on every new controller, and the failure mode (an unprotected endpoint) is silent. With the fallback policy the failure mode is an explicit `[AllowAnonymous]`.
* **Return 403 for another tenant's resources.** Rejected — 403 confirms the resource exists; 404 reveals nothing.
* **Take the tenant from the URL (`/api/organizations/{id}/workorders`).** Rejected for now — the URL is still client-controlled and would have to be checked against the claim on every request anyway; the claim alone is simpler and cannot disagree with itself.
