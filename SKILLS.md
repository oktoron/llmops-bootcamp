# SKILLS.md

Agent skills and subagents for this project: what exists, what each one covers, and
what is worth adding next.

**Skills** are procedures loaded into the current agent's context. **Subagents** are
separate agents with their own context, tool set and model. Skills are documented
below; subagents are in [Subagents](#subagents) at the end.

## Where skills live

Skills are stored in `.claude/skills/<name>/SKILL.md`. VS Code Copilot reads workspace
skills from `.github/skills/`, `.claude/skills/` or `.agents/skills/`, so this one
location serves VS Code Copilot, Copilot CLI and Claude Code with no duplication.

```
.claude/skills/
├── eval-gate/SKILL.md
├── failure-drill/SKILL.md
└── stack/SKILL.md
```

Each skill is invocable manually as `/<name>` in chat, and is also loaded
automatically when its `description` matches the task. Only the name and description
sit in context until a skill is actually needed, so adding skills is cheap.

### SKILL.md format

```markdown
---
name: kebab-case-name          # required, lowercase/digits/hyphens, max 64 chars
description: What it does and when to use it.   # required, max 1024 chars
---

# Body: the instructions the agent follows
```

Optional frontmatter fields: `argument-hint`, `user-invocable` (default `true`),
`disable-model-invocation` (default `false`), `context: fork` (experimental, runs the
skill in a dedicated subagent).

The `description` is the only thing that decides whether a skill triggers, so it must
name the concrete symptoms and vocabulary of the task, not just the topic. Keep the
body under roughly 500 lines and move long reference material into sibling files.

---

## Skills in this project

### `/eval-gate`

> Run the LLMOps eval suite against the local service the way CI runs it, and
> interpret the result.

**Triggers on:** running evals, checking the eval gate, verifying a prompt change did
not regress quality, debugging failing or all-red eval cases, adding cases to
`golden.jsonl`.

**Why it exists.** The eval workflow has several traps that cost real time and look
like bugs in the student's code:

- A cold LiteLLM fails the first request. Combined with a student's own fallback or
  circuit breaker, every later case lands in the degradation stub, so **all cases go
  red within seconds** and look like a prompt regression.
- The correct run command is a `docker run` inside the compose network, and on
  Windows Git Bash it breaks twice — once visibly (MSYS path rewriting) and once
  silently (a `$(pwd)` mount resolving to an empty directory).
- The 5-of-6 threshold is deliberate, and its consequence is non-obvious: a single
  case cannot turn the gate red on its own.
- The refusal grader deliberately excludes the word for "sorry", because it appears
  in the degradation stub and would let a safety case pass during a full outage.

**Covers:** warming the gateway, the in-network and host run commands, the Windows
variant, reading the output, the regression self-test (removing `support` from the
prompt must turn the gate red), per-case debugging against the mock's intent
precedence, and adding cases with the matching threshold bump.

### `/failure-drill`

> Reproduce the course's provider failure scenarios against the mock provider and
> verify the service handles them.

**Triggers on:** reliability, fallback, retry, circuit breaker, timeout budget,
graceful degradation, or simulating a 503, 429, latency spike, garbage output,
tool-call failure or total outage.

**Why it exists.** The failure markers are the backbone of weeks 3–4 but are spread
between `mock-provider/Program.cs`, `docs/failure-scenarios.md` and the lesson text.
The skill also carries the course's design decisions, which an agent would otherwise
"helpfully" violate — most importantly that **fallback is chosen over retry** and
that `num_retries: 0` is intentional.

**Covers:** the four markers (`__fail_503`, `__fail_429`, `__delay`, `__garbage`) and
the query-parameter alternative, how to drive a drill and read the resulting log row,
the six-scenario table with mandatory-versus-lesson-text status, simulating a total
outage across all three mock model names, warming a tripped circuit breaker back down
before running evals, and which scenarios have automated regression coverage.

### `/stack`

> Bring up, reset, inspect and troubleshoot the docker compose stack.

**Triggers on:** starting or restarting the stack, a container that will not come up,
`port is already allocated`, a `db/schema.sql` change that did not take effect, the
chat answering "service unavailable", or querying the `requests` and `prompts` tables.

**Why it exists.** `GETTING_STARTED.md` §8 is a long troubleshooting table, and the
two highest-frequency traps are both counter-intuitive:

- `db/schema.sql` runs **only** on an empty data directory, so schema edits need
  `docker compose down -v`. Neither `restart` nor `up --build` re-runs it.
- Port shifting needs the `!override` tag and **Compose 2.24+**. On older Compose the
  tag is silently ignored, port lists merge, and the old binding survives.

**Covers:** compose commands and per-service rebuilds, health checks and the LiteLLM
readiness gap, `psql` queries against both tables, the volume-wipe rule, both
port-shift paths, the Windows double-bind check, proxy and IPv4/IPv6 localhost
issues, and switching to a real provider key without breaking the `env_file`
precedence rule.

---

## Built-in skills worth using here

These ship with Claude Code and need no setup:

| Skill | Use in this project |
|---|---|
| `/code-review` | Review a homework branch or a change to the service for correctness bugs |
| `/security-review` | Relevant to the week 4 guardrails work: PII handling, prompt injection, the HITL approval path |
| `/simplify` | Quality pass on the control-plane code as it accumulates routing, cache and fallback layers |
| `/init` | Already effectively done — see [CLAUDE.md](CLAUDE.md) and [CODEBASE.md](CODEBASE.md) |

---

## Candidates worth adding

Not written yet. Listed roughly by value.

| Proposed skill | Scope |
|---|---|
| `observability-api` | The `/observability`, `/cost`, `/prompts`, `/providers` and `/approvals` contract: the exact JSON shape each Console tile expects, plus the two endpoints the UI calls that do not exist yet (`POST /approvals/{id}/approve`, `POST /prompts/{version}/activate`). Prevents an agent inventing a shape that leaves tiles showing `—`. |
| `prompt-registry` | The week 1 seam end to end: seeding `prompts`, reading the active version, logging `prompt_version`, and the deliberate rule that the fallback default must **not** contain the word `support` so a broken registry is visible rather than masked. |
| `cost-model` | Token-to-cost arithmetic, the `NUMERIC(10,6)` column, budget alerting, and the mock's `length / 4` token approximation — so cost figures are not silently compared against real provider pricing. |

---

## Conventions

- **One topic per skill.** A skill that covers everything triggers on nothing.
- **Write descriptions from the symptom, not the topic.** "all eval cases red" belongs
  in a description; "quality assurance" does not.
- **Record the *why*, not just the command.** The value in these three skills is the
  reasoning behind the traps — an agent can already read `docker-compose.yml`.
- **Encode course design decisions explicitly.** Fallback over retry, no hidden
  retries at the gateway, and the mock-only mandatory track are choices an agent will
  otherwise undo while trying to be helpful.
- Skills are currently in English. Course material is Ukrainian; if students are meant
  to read these, translate them to match.

---

## Subagents

Defined in `.claude/agents/<name>.md`. Project agents are checked into version control
and take precedence over user-level agents in `~/.claude/agents/`. Invoke by name, or
explicitly with `@agent-<name>`. Note that `/agents` no longer opens a wizard — edit
the files directly.

| Agent | Model | Writes | Notes file | Role |
|---|---|---|---|---|
| `manager` | opus | code: no | `Tasks/`, `Plans/`, `Research/` | Mints the task ID, breaks work into vertical slices, names the agent for each step |
| `coder` | opus | code: yes | — (the diff is the output) | Implements the `TODO(student, W#)` seams and the Console API contract |
| `reviewer` | opus | code: **no** | `Review/<id>/review.md` | Reviews a **change** — a diff or student PR — against the week's mandatory criteria |
| `backend-reviewer` | opus | code: **no** | `Review/<id>/review-backend.md` | Domain pass: `service/`, `mock-provider/`, `db/`, `gateway/`, `evals/`, CI |
| `frontend-reviewer` | opus | code: **no** | `Review/<id>/review-frontend.md` | Domain pass: `ui/` and the API contract. Findings phrased as the service-side fix |
| `docs-reviewer` | opus | code: **no** | `Review/<id>/review-docs.md` | Domain pass: student-facing docs checked **against the code**, not just proofread |
| `critic` | opus, xhigh | code: **no** | `Critic/<id>/critic.md` | Harsh second pass. Assumes the change is broken until proven, and rates the first review |
| `devops` | sonnet | code: yes | `Devops/<id>/devops.md` | `docker compose up --build`, health checks, ports, volumes, keys |
| `logger` | sonnet | code: **no** | `Devops/<id>/logs.md` | Diagnoses from the `requests` table and container logs |
| `tester` | sonnet | code: yes | `Tests/<id>/tests.md` | Evals, failure drills, xUnit tests |
| `tech-writer` | opus | code: yes | — (the doc is the output) | Docs, homework text, troubleshooting entries |

All eleven are granted the `context7` MCP server (`mcpServers: context7` in each
file's frontmatter). It is **mandatory** for `coder`, `reviewer`, the three domain
reviewers and `critic` — look up a library API before writing it, before passing it in
review, and again when challenging a review's claim about it. The others use it when
they need it. It is also the **only** online source any agent may use: web search and
fetch are not permitted, and a fact Context7 cannot settle is marked
`unverified — follow-up` (see "Authentication and network" in [CLAUDE.md](CLAUDE.md)).

Context7 must be registered with **Claude Code**, not only in VS Code's `mcp.json`:
`claude mcp add context7 --scope user -- npx -y @upstash/context7-mcp@latest`. See
[MCP.md](MCP.md).

### Design notes

- **`reviewer`, `critic` and `logger` do not edit code.** `Edit` is disallowed; they
  keep `Write` because they have to file their own notes under `.agent/`. Since all
  three also have `Bash`, this is a strong convention rather than a sandbox — enforcing
  it properly would need a permissions hook. A reviewer that can edit fixes what it
  finds instead of reporting it, which is the failure mode worth preventing.
- **Models are not uniform.** Judgement-heavy roles (`manager`, `coder`, `reviewer`,
  `tech-writer`) use opus; procedural ones (`devops`, `logger`, `tester`) use sonnet
  with the relevant skill preloaded, since the procedure carries the knowledge.
- **`skills:` preloads context at startup** — `devops` and `logger` get `stack`,
  `tester` gets `eval-gate` and `failure-drill`, so the traps are present before the
  first command rather than discovered after.
- **All agents inherit [CLAUDE.md](CLAUDE.md)**, so the never-commit rule applies to
  every one. Do not set `omitClaudeMd: true` on an agent that touches this repo.
- **The authentication and network rule is inherited the same way**, and each agent file
  also points to it in one line so it is in view at the moment of temptation. No agent
  authenticates without a very good reason, and one that thinks it has one stops and
  asks. No agent runs `gh` at all — `reviewer` works from local git and asks for a PR's
  content when it needs it. Upstream facts come from Context7 or are marked unverified.
  The rule exists because `critic`, being thorough, ran `gh auth status` unprompted and
  raised a GitHub auth prompt — so "it only reads" is explicitly not an exemption.
- **`manager` cannot dispatch.** Subagents do not spawn subagents, so it names the
  agent for each step and the main session dispatches. This is a real limitation, not
  an oversight in the prompt.
- **`reviewer` absorbed the proposed `hw-review` skill.** A PR review is read-heavy
  fan-out with a fixed output format and a hard need not to edit the code under
  review — all reasons an agent fits better than a skill.
- **Four review agents, split by job rather than by seniority.** `reviewer` reviews a
  *change*; the three specialists review *surfaces* of the existing tree. The split
  follows where defects actually cluster here, which is not where you would guess: a
  whole-tree review found one backend bug, one infrastructure bug, and five separate
  cases of documentation contradicting code. Hence `docs-reviewer` exists at all, and
  hence its brief insists it read the code — a docs pass that only reads docs finds
  typos.

  `frontend-reviewer` is deliberately not an Angular critic. `ui/` is frozen and students
  may not touch it, so component-level findings are unusable by construction; its real job
  is verifying that every field the console reads is a field the service sends, and every
  finding is phrased as the service-side fix.

  The specialists have disjoint surfaces, so they run in parallel. The cost of that
  disjointness is that a cross-cutting defect can be individually dismissed by each of
  them and still be real, which is why `critic` is told to read every review file together
  and hunt for findings that span them.
- **`critic` is deliberately harsh, and that is calibrated, not decorative.** It treats
  the change as guilty until proven, rejects untested code as unproven rather than
  incomplete, bans hedging phrases from its output, rounds severity *up* under
  uncertainty, and rates the first review as adequate, shallow or wrong. It runs at
  `xhigh` effort because its whole value is finding what a normal pass missed.

  Two guardrails keep that from becoming noise. Harshness targets the code and the
  reasoning, never the author — a remark about competence is both wrong and
  counterproductive, since it guarantees the finding is ignored. And manufactured
  findings are forbidden: every one needs a concrete failure scenario, or it is filed as
  a question. Inventing severity to look thorough teaches the author to ignore the
  agent, which then gets the real blocker ignored too. `CLEAN` remains a permitted
  verdict; it is just meant to be rare.
- **Destructive operations still ask.** `devops` is instructed to confirm before
  `docker compose down -v`, because it destroys the request log and the prompt
  registry.
