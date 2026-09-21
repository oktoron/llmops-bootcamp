---
name: eval-gate
description: Run the LLMOps eval suite against the local service the way CI runs it, and interpret the result. Use when asked to run evals, check the eval gate, verify a prompt change did not regress quality, debug why eval cases fail or why all cases are red, or add cases to golden.jsonl.
---

# Running the eval gate

The eval runner sends each case in `evals/golden.jsonl` to the service's `POST /chat`,
grades the answer with rule-based checks, and exits non-zero when fewer than
`--threshold` cases pass. CI runs exactly this on every pull request.

## Always warm the gateway first

LiteLLM is cold right after `docker compose up` and fails the first request. If the
student has already implemented fallback (week 4) or a circuit breaker, that one
failure sends every later case into the degradation stub.

**Symptom: all cases red within seconds. That is a cold start, not a prompt regression.**

Warm it, then run:

```sh
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:4000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"mock","messages":[{"role":"user","content":"hi"}]}'
```

Repeat until it returns `200`. CI does this in the "Wait for gateway" step of
`.github/workflows/eval-gate.yml`.

## Preferred: run inside the compose network

This matches CI and avoids host ports and corporate proxies.

```sh
docker run --rm --network <project>_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "$(pwd)/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 5
```

`<project>` is the compose project prefix, usually the folder name. Confirm with
`docker network ls`.

On Windows Git Bash this breaks twice, and the second break is silent: MSYS rewrites
the container-side path, and a `$(pwd)` host path mounts as an empty directory with no
error. Use:

```sh
MSYS_NO_PATHCONV=1 docker run --rm --network <project>_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "$(pwd -W)/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 5
```

## From the host

Only when port 8080 is free and no proxy intercepts localhost:

```sh
SERVICE_URL=http://localhost:8080 python evals/run.py --dataset evals/golden.jsonl --threshold 5
```

## Reading the output

Per-case lines, then `eval: N/M passed, threshold T`. Exit code `0` is green, `1` is red.

The threshold is 5 of 6 on purpose: it absorbs one flake, and a broken prompt fails
three cases at once so it always trips the gate. The consequence is that a single
case cannot turn the gate red by itself. A case that must be caught individually,
such as `safety-1`, has to be duplicated with variations.

## How a regression is supposed to surface

The mock provider only returns canonical answers when the system prompt contains the
word `support` (case-insensitive, see `mock-provider/Program.cs`). Without it,
non-action questions return `не знаю`, which `forbid` catches.

So: removing `support` from the prompt must turn the gate red. That is the gate's
self-test. After changing routing, the prompt registry or the model, rebuild and
re-run:

```sh
docker compose up --build -d service
```

## When a single case fails

1. Print the actual answer. The runner already truncates it to 60 chars per line.
2. Check the case's `expect` / `forbid` keywords against `Reply()` in
   `mock-provider/Program.cs`. Graders are substring matches, lowercased, and the
   expectations are Ukrainian.
3. Remember the intent precedence in the mock: refund and urgency beat order lookup,
   so "refund for order #123" produces `create_ticket`, not `lookup_order`.
4. `expect_refusal` passes on `не можу`, `cannot` or `refuse`. The word for "sorry"
   was deliberately excluded because it also appears in the degradation stub, which
   would let a refusal case pass during a full outage. Do not add it back.

## Adding cases

One JSON object per line in `evals/golden.jsonl`:

```json
{"id": "faq-3", "input": "...", "expect": ["keyword"], "forbid": ["не знаю"]}
{"id": "safety-2", "input": "...", "expect_refusal": true}
```

Raise `--threshold` in `.github/workflows/eval-gate.yml` alongside the dataset, or
the gate gets looser as cases are added.

## Recording the run

If you are working under a task ID, append the result to
`.agent/Tests/<task-id>/tests.md`: the exact command, the real output including any
failures, and the verdict. Keep failures verbatim — a recorded red run and its cause
saves the next person from the same trap.

Convention: [.agent/README.md](.agent/README.md).
