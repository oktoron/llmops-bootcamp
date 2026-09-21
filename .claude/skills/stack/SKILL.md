---
name: stack
description: Bring up, reset, inspect and troubleshoot the LLMOps bootcamp docker compose stack (ui, service, gateway, mock-provider, postgres, redis). Use when asked to start or restart the stack, when a container will not come up, when a port is already allocated, when a db/schema.sql change did not take effect, when the chat answers "service unavailable", or when querying the requests and prompts tables.
---

# Working with the stack

```sh
docker compose up --build                 # the canonical start: ui :4200, service :8080, gateway :4000, postgres :5432, mock-provider (internal)
docker compose up --build -d              # same, detached, when you need the shell back
docker compose --profile advanced up      # adds redis :6379 — optional extension, only when asked
docker compose logs -f service
docker compose down                       # stop, keep data
docker compose down -v                    # stop and wipe the postgres volume
```

`--profile` is a **top-level** flag: it goes before the subcommand, so
`docker compose --profile advanced up`, not `docker compose up --profile advanced`.
`COMPOSE_PROFILES=advanced` is the equivalent environment variable. Carry the flag
through later commands that target Redis — a plain `docker compose ps` or `stop` does
not necessarily see a profiled service.

Rebuild one service after editing its code:

```sh
docker compose up --build -d service
```

## Health checks

```sh
curl http://localhost:8080/health                                  # service
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:4000/health/liveliness   # gateway
curl -X POST http://localhost:8080/chat -H 'Content-Type: application/json' \
     -d '{"message":"Hello"}'
```

`depends_on` orders startup but does not wait for readiness. LiteLLM takes longest —
the container reports `Up` while port 4000 still refuses connections, so the first
chat can answer "service temporarily unavailable". Wait around 30 seconds.

## Querying the database

```sh
docker compose exec postgres psql -U llmops -d llmops \
  -c "SELECT created_at, model, status, latency_ms, prompt_tokens, cost_usd FROM requests ORDER BY created_at DESC LIMIT 10"

docker compose exec postgres psql -U llmops -d llmops \
  -c "SELECT name, version, active FROM prompts"
```

Credentials are `llmops` / `llmops`, database `llmops`. Dev-only.

## Schema changes need a volume wipe

`db/schema.sql` is mounted into `docker-entrypoint-initdb.d`, which Postgres runs
**only when initializing an empty data directory**. `restart` and `up --build` do not
re-run it. This is standard image behavior, not a bug.

```sh
docker compose down -v && docker compose up -d
```

Then confirm the change actually landed with a `psql` query rather than assuming it did.

## Port conflicts

Two prepared options, both requiring **Compose 2.24+** because they use the
`!override` tag. On older Compose the tag is ignored silently, the port lists
*merge*, and the old binding stays — so check `docker compose config` if a shift
appears not to work.

- Local, auto-loaded, git-ignored: copy `docker-compose.override.yml.example` to
  `docker-compose.override.yml`. Ports become UI 14200, service 18080, gateway 14000,
  postgres 15432.
- Explicit overlay: `docker compose -f docker-compose.yml -f demo-ports.yml up --build -d`.
  Ports become UI 4300, service 8180, gateway 4100, postgres 25432.

Never commit `docker-compose.override.yml`. CI expects the default ports and its
"Wait for service" step would fail.

After shifting ports, every command above needs the new port number.

## Other traps

| Symptom | Cause and fix |
|---|---|
| `project name must not be empty` | The folder name contains non-ASCII characters. Use `docker compose -p llmops up -d`, or clone into an ASCII path. |
| `up` succeeded but a port behaves oddly (Windows) | Silent double bind. Test all three of `localhost`, `127.0.0.1` and `[::1]` — they must return the same `{"status":"ok"}`. If not, shift ports. |
| A script gets 404 on localhost but `curl` works | A corporate proxy is intercepting localhost, or IPv4/IPv6 resolve differently. Set `NO_PROXY=localhost,127.0.0.1` or work inside the compose network. |
| UI does not open | The first Angular build is slow. Wait for `Application bundle generation complete` in `docker compose logs ui`. |
| Cyrillic in `curl -d` turns into `?` | Git Bash mangles non-ASCII arguments. Send from the UI, or use `--data @file.json`. |

## Switching to a real provider

Only needed for real models and the optional model-based grader. The whole mandatory
track runs on the mock.

```sh
cp gateway/.env.example gateway/.env      # fill OPENAI_API_KEY or ANTHROPIC_API_KEY
MODEL=gpt-5-mini docker compose up -d     # or MODEL=claude-haiku
```

Keys belong in `gateway/.env` only. Do not add them to `environment:` in compose —
that has higher priority and would override the file with empty values. Note that
real answers vary, so the keyword-based graders get flaky.

## Recording environment state

If you are working under a task ID, note the resulting state in
`.agent/Devops/<task-id>/devops.md`: which services are up, on which ports, whether
the volume was reset, and the value of `MODEL`.

Record deviations from the defaults prominently — shifted ports, a stale volume, an
older Compose version, a real key in play. They change every later command and are the
usual reason someone else cannot reproduce a result.

Convention: [.agent/README.md](.agent/README.md).
