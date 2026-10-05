#!/usr/bin/env bash
# Post-deploy smoke test for FieldOps: checks that the few paths a user
# depends on answer correctly through the public frontend URL. Not a
# replacement for the test suite — it answers "is this deployment alive and
# wired together?", and its exit code (0 = healthy, 1 = not) lets a person
# or a pipeline act on the result.
#
# Usage: scripts/smoke-test.sh <base-url>
#   e.g. scripts/smoke-test.sh https://fieldops-web.<env>.azurecontainerapps.io
#
# Retries cover scale-to-zero cold starts (Day 107: ~22s for the API plus a
# resuming serverless database) — and the gap between a deploy command
# reporting success and the new revision actually taking traffic (Day 108).
set -u

BASE_URL="${1:?usage: smoke-test.sh <base-url>}"
BASE_URL="${BASE_URL%/}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-6}"
RETRY_DELAY_SECONDS="${RETRY_DELAY_SECONDS:-10}"

failures=0
LAST_BODY=""
# Day 121: the API requires a bearer token; set by the "login" step below.
TOKEN=""

# check <name> <path> <expected-status> <expected-body-substring> [method] [json-body]
check() {
    local name="$1" path="$2" expected_status="$3" expected_body="$4"
    local method="${5:-GET}" data="${6:-}"
    local attempt status body
    local args=(-s --max-time 30 -w '\n%{http_code}' -X "$method")
    if [[ -n "$TOKEN" ]]; then
        args+=(-H "Authorization: Bearer $TOKEN")
    fi
    if [[ -n "$data" ]]; then
        args+=(-H "Content-Type: application/json" -d "$data")
    fi

    for ((attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)); do
        body=$(curl "${args[@]}" "$BASE_URL$path")
        status="${body##*$'\n'}"
        body="${body%$'\n'*}"

        if [[ "$status" == "$expected_status" && "$body" == *"$expected_body"* ]]; then
            echo "PASS  $name  $path -> $status"
            LAST_BODY="$body"
            return 0
        fi

        echo "      $name  $path -> $status (attempt $attempt/$MAX_ATTEMPTS)" >&2
        if ((attempt < MAX_ATTEMPTS)); then
            sleep "$RETRY_DELAY_SECONDS"
        fi
    done

    echo "FAIL  $name  $path -> $status, expected $expected_status containing '$expected_body'"
    failures=$((failures + 1))
}

echo "Smoke testing $BASE_URL"

# The Angular app itself, and a deep link — proves nginx's try_files fallback
# (Day 98), since that path only exists inside the Angular router.
check "frontend"      "/"                       200 "<app-root"
check "deep-link"     "/work-orders/1"          200 "<app-root"
# Day 121: a deployment must reject anonymous API calls (SECURITY_REVIEW.md
# F1/F3) — checked before logging in, while no token is set.
check "auth-required" "/api/workorders/report"  401 ""
# Log in as the demo organization 1 Admin (employee 1) and keep the token.
# Day 122: login requires a password — SMOKE_PASSWORD for a real environment;
# the default is the documented local demo password.
SMOKE_PASSWORD="${SMOKE_PASSWORD:-FieldOps-Demo-2026!}"
check "login"         "/api/auth/login"         200 '"token"' POST "{\"employeeId\":1,\"password\":\"$SMOKE_PASSWORD\"}"
TOKEN=$(sed -E 's/.*"token":"([^"]+)".*/\1/' <<<"$LAST_BODY")
# Through nginx's /api proxy to the API and its database (Days 105-107).
check "api+database"  "/api/organizations"      200 '"name"'
# The report endpoint — returned 500 without Redis until Day 108's fix.
check "report"        "/api/workorders/report"  200 '"organizationId":1'

if ((failures > 0)); then
    echo "Smoke test FAILED: $failures check(s) failed"
    exit 1
fi

echo "Smoke test passed"
