---
name: failure-drill
description: Reproduce the course's provider failure scenarios against the mock provider and verify the service handles them. Use when working on reliability, fallback, retry, circuit breaker, timeout budget, graceful degradation, or when asked to simulate a 503, a 429, a latency spike, garbage output, a tool-call failure, or a total outage.
---

# Failure drills

The mock provider fails on demand, so every reliability scenario is reproducible
without a real provider key. The catalogue lives in `docs/failure-scenarios.md`.

## Two ways to trigger a failure

**Markers in the user message** — these pass through the gateway, so they work from
the chat UI, from `curl` against the service, and from eval cases:

| Marker | Effect |
|---|---|
| `__fail_503` | mock returns HTTP 503 |
| `__fail_429` | mock returns HTTP 429 |
| `__delay` | mock sleeps 2000 ms |
| `__garbage` | mock returns `"..."` — a successful response that is unusable |

**Query parameters against the mock directly** (inside the compose network only,
port 9000 is not published): `?fail=503`, `?delay=2000`, `?garbage=1`.

Markers are the right tool when testing the service. Query parameters are for
probing the mock itself.

## Driving a drill

```sh
curl -s -X POST http://localhost:8080/chat \
  -H 'Content-Type: application/json' \
  -d '{"message":"__fail_503 Where is my order #10482?"}'
```

Then read the log row the request left behind:

```sh
docker compose exec postgres psql -U llmops -d llmops \
  -c "SELECT model, provider, status, latency_ms, cost_usd FROM requests ORDER BY created_at DESC LIMIT 5"
```

## Scenarios and what "handled" means

| # | Scenario | Trigger | Expected behavior | Track |
|---|---|---|---|---|
| 1 | Provider down | `__fail_503` | Fall back to the next provider, log the incident | **Mandatory, HW4** |
| 2 | Rate limit | `__fail_429` | Fall back. Retry with backoff is deliberately *not* built | Lesson 7 text |
| 3 | Latency spike | `__delay` | Timeout budget, then fall back or degrade | Optional, HW3 |
| 4 | Bad or empty answer | `__garbage` | Detect as a format failure, answer on a safe honest branch | Eval case, HW5 |
| 5 | Tool-call failure | mock tool errors | No partial side effect, clean error to the user | Lesson 6 text |
| 6 | All providers down | every mock model 503 | Graceful degradation, service stays up | **Mandatory, HW4** |

The course chooses **fallback over retry** (decision in lesson 7). Do not add hidden
retries: `litellm_settings.num_retries` is `0` precisely so failures reach the
service, where the student's logic is.

## Simulating a total outage (scenario 6)

All three mock model names — `mock`, `mock-mini`, `mock-strong` — point at the same
container, so one marker fails all of them at once. `__fail_503` on a message is
therefore already a full outage from the service's point of view once fallback walks
that list.

Verify two things: the user gets the degradation message rather than a 500, and the
service is still alive afterwards (`curl http://localhost:8080/health`).

## Checking a drill did not break the happy path

A circuit breaker that opens during a drill will keep serving the stub afterwards.
Warm it back up with one clean request before running evals, or the whole suite goes
red for the wrong reason.

## Regression coverage

Scenarios 1 and 6 are covered automatically (the HW5 check that zero cases pass under
total failure, plus `forbid` on the stub text), and scenario 4 by a recommended HW5
eval case on `__garbage`. Scenarios 2, 3 and 5 have no eval of their own — they are
covered by lesson text and optional unit tests, so verify those by hand.

## Recording the drill

If you are working under a task ID, append to `.agent/Tests/<task-id>/tests.md`: which
scenario you drove, the marker used, what the service actually returned, and the log
row it left in `requests`. Note explicitly whether a circuit breaker was left open —
that affects every run afterwards, including evals.

Convention: [.agent/README.md](.agent/README.md).
