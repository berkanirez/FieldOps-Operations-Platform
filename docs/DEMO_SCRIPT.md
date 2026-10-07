# FieldOps — Five-Minute Demo Script

A spoken walkthrough of FieldOps for interviews and recorded demos: what to say, what to show, and the exact commands to run. It is a skeleton to rehearse in your own words, not a text to memorize. Spoken English runs at roughly 130 words per minute; each section lists its approximate word count so the whole demo fits in five minutes.

## Before the demo (once, about 3 minutes)

```
docker compose up --build -d --wait
# apply the six migrations — the loop in the README, "Running it with Docker Compose"
```

Have three things open:

1. The README on GitHub, scrolled to the architecture diagram.
2. A bash terminal in the repository root with the login helper from the README already defined:
   ```
   API=http://localhost:5190
   login() { curl -s -X POST $API/api/auth/login -H "Content-Type: application/json" \
     -d "{\"employeeId\":$1,\"password\":\"FieldOps-Demo-2026!\"}" | sed -E 's/.*"token":"([^"]+)".*/\1/'; }
   ADMIN=$(login 1); MEMBER=$(login 2); OTHER=$(login 3)
   ```
   Logging in once here matters: login is limited to five attempts per minute, and tokens are valid for an hour.
3. A second terminal ready for `docker compose logs -f fieldops-notification-service`.

**Fallback:** if the stack does not come up, say so plainly, and show the README's "API examples" section instead. Every command and status code there was executed against a freshly created stack. A failing demo handled calmly says more about you than a perfect one.

---

## 0:00–0:30 · The problem and the project (~57 words)

**Show:** the README title and Highlights.

> FieldOps is a multi-tenant platform for field-service companies: organizations, their employees and customers, and work orders that move from open to assigned, in progress and completed. I built it as a personal project to go deep on the backend problems that real systems face: tenant isolation, failures in the infrastructure around you, and performance you can measure.

---

## 0:30–1:30 · Architecture (~137 words)

**Show:** the Mermaid diagram in the README.

> It's a modular monolith. Five modules, and each one owns its own SQL Server database, so no module can query another's data, even by accident. Rules that span modules live in the host, in small application services.
>
> When a work order is completed, the state change and an outbox row are written in the same transaction. A background publisher sends the event to RabbitMQ and retries until it succeeds, so an outage delays the event but never loses it. A separately deployable notification service consumes it, with an inbox table so a redelivered message is processed once.
>
> Redis caches a status report, and Elasticsearch provides search. Both are optional: if they fail, the API keeps serving. I wrote that rule down as an architecture decision record, together with the places where the code doesn't follow it yet.

---

## 1:30–3:00 · Live: lifecycle and tenant isolation (~85 spoken words; the commands fill the rest)

**Run, one step at a time, narrating each:**

```
# 1. create (201) — note the id in the response
curl -X POST $API/api/workorders -H "Authorization: Bearer $ADMIN" \
  -H "Content-Type: application/json" -d '{"title":"Fix the HVAC unit","customerId":1}'
ID=1   # the id returned above
```
> I'm logged in as an Admin of organization 1. Every endpoint requires a token by default, and the API never asks me which organization I belong to: it reads that from the validated token.

```
# 2. assign (Admin), then start and complete (the assigned Member)
curl -X POST $API/api/workorders/$ID/assign -H "Authorization: Bearer $ADMIN" \
  -H "Content-Type: application/json" -d '{"employeeId":2}'
curl -X POST $API/api/workorders/$ID/start    -H "Authorization: Bearer $MEMBER"
curl -X POST $API/api/workorders/$ID/complete -H "Authorization: Bearer $MEMBER"
```
> The Admin assigns it, and only the assigned employee can start and complete it.

**Switch to the second terminal:** the notification service has logged `Notification: Work order 'Fix the HVAC unit' has been completed and is awaiting your approval.`
> That completion just travelled through the outbox and RabbitMQ to a separate service, which notifies the customer. (Only work orders with a customer produce a notification, which is why the create request includes `customerId`.)

```
# 3. another organization's Admin asks for the same work order
curl -i $API/api/workorders/$ID -H "Authorization: Bearer $OTHER"     # 404
# 4. a Member tries an Admin action
curl -i -X POST $API/api/workorders/$ID/assign -H "Authorization: Bearer $MEMBER" \
  -H "Content-Type: application/json" -d '{"employeeId":2}'            # 403
```
> Organization 2 gets a 404, not a 403. A 403 would confirm the work order exists. And a Member can't assign work.

---

## 3:00–4:15 · One hard problem (~170 words)

**Show:** the README's Performance bullet, or nothing. This part is a story.

> The problem I learned the most from: my integration test suite slowed down from eight and a half minutes to thirty-seven. My first hypothesis was that the parallel SQL Server test containers were exhausting Docker's memory. I measured it with docker stats before changing anything: about half the memory was free. Wrong guess.
>
> So I captured the thread stacks of the running test process with dotnet-stack. Requests were stuck inside the Redis client: during an outage, StackExchange.Redis doesn't fail commands, it queues them in a backlog until the connection comes back. One request had been waiting there for over eleven minutes.
>
> That wasn't just a test problem. In production it would mean a Redis outage hangs requests, even though I had written code to fall back when Redis fails. The fallback never ran, because nothing ever failed. Switching the client to fail-fast fixed it; the suite went back under a minute.
>
> The lesson I took: measure before you fix, and make sure your error handling can actually be reached.

---

## 4:15–5:00 · What's open and what I learned (~90 words)

**Show:** `docs/SECURITY_REVIEW.md` (the remediation table) or ADR 0011's open list.

> I also ran an OWASP Top 10 review on my own code. It found that identity used to come from headers any client could forge, which I fixed. And I kept a list of what's still open: customers approve work orders without logging in, and the readiness check still depends on Redis, which contradicts my own decision record.
>
> The thing I'd most want you to take away: every number I mentioned was measured, and every decision is written down with its trade-off. I'm happy to go deeper into any part.

**Total:** about 520 spoken words (around four minutes) plus roughly a minute of running commands and switching screens.

---

## Short versions

### 30 seconds (elevator pitch, ~70 words)

> FieldOps is a multi-tenant field-service platform I built in .NET 10 to learn backend engineering in depth. It's a modular monolith with a database per module, a separately deployed notification service behind RabbitMQ with an outbox, Redis caching and Elasticsearch search. What I'm proudest of is the measured work: I load-tested it with k6, doubled throughput by fixing thread-pool starvation, and ran an OWASP review on my own code.

### 2 minutes (~200 words)

Use the 0:00–0:30 section, then this compressed architecture paragraph, then the hard problem in four sentences:

> It's a modular monolith: five modules, each with its own SQL Server database. Completing a work order writes an outbox row in the same transaction; a background publisher delivers it through RabbitMQ to a separate notification service, which uses an inbox table so each message is processed once. Tokens are required everywhere, and the tenant comes only from validated claims; another tenant's data returns 404. Redis and Elasticsearch are optional by design.
>
> The hardest problem: my test suite slowed from 8.5 to 37 minutes. I captured thread stacks with dotnet-stack and found requests waiting in the Redis client's backlog during an outage, so my fallback code never ran. Fail-fast fixed it.

Close with the last sentence of the 4:15–5:00 section.

---

## Likely follow-up questions

Short pointers. The full answers live in the linked documents.

| Question | Where the answer is |
|---|---|
| Why a modular monolith and not microservices? | [ADR 0001](adr/0001-modular-monolith-one-way-dependencies.md): boundaries now, deployment split only where it pays (the notification service, [ADR 0005](adr/0005-notification-service-boundary.md)). One Compose file is not independent deployment ([ADR 0007](adr/0007-independent-deployment.md)). |
| How do you keep data consistent without cross-database foreign keys? | [ADR 0002](adr/0002-cross-module-references-via-host-orchestration.md)/[0003](adr/0003-database-per-module.md): the host validates references; the dangling-reference gap is accepted and documented. |
| Why the outbox? Why not publish directly? | Publishing after the commit can lose the event if the broker is down or the process dies in between. The outbox makes the event part of the same transaction ([ADR 0006](adr/0006-rest-vs-messaging.md), README Messaging). |
| What happens if Redis goes down? | [ADR 0011](adr/0011-optional-infrastructure-degrades.md): the report is computed from SQL Server; idempotency fails open, a deliberate trade-off; readiness is the known inconsistency. |
| How did async double the throughput? | Blocking calls held thread-pool threads while waiting for I/O; under load the pool starved and requests queued. Async releases the thread during the wait. Measured with k6 at the same load, before and after. |
| What would you do next? | Customer login (close the open approval finding), a Degraded Redis health check, 503 for unavailable search, a migration job, publishing the notification service image. |
