# Project rules

## Version control: never commit

**Never commit anything in this repository. Ever.**

Do not run `git commit`, `git push`, `git tag`, `git merge`, or `git add`, and do not
create pull requests. Do not offer to commit at the end of a task, and do not treat a
finished feature or a clean working tree as an invitation to commit.

Write and edit files normally, then leave the changes in the working tree and state
what changed. The maintainer reviews and commits manually, every time.

Read-only git commands (`git status`, `git diff`, `git log`) are fine and are the
right way to report what changed.

## Authentication and network

**Do not try to authenticate without a very good reason.**

**If you believe you have a very good reason, stop and ask the user first.** Say what
you would run and why, then wait. Do not decide alone that the reason is good enough,
and do not try another route to the same credential instead.

"Authenticate" means anything that logs in, checks a login, or can raise a credential
or browser prompt:

| Banned | Why it is on the list |
|---|---|
| `gh`, **any subcommand** | Not run by any agent, not even read-only ones like `gh pr view` or `gh auth status`. Any `gh` call can reach api.github.com and raise an auth prompt. |
| `docker login`, registry auth | Credential prompt. |
| `npm login`, `npm adduser` | Credential prompt. |
| `git push`, `fetch`, `pull`, `clone`, `ls-remote` against a remote | Can prompt for credentials. Local git — `status`, `diff`, `log`, branches — is fine. |
| OAuth and MCP auth flows | The GitHub MCP server opens a browser OAuth prompt on first use (see [MCP.md](MCP.md)). |

This rule exists because an agent ran `gh auth status` on its own initiative during a
review. It hit api.github.com and raised a GitHub auth prompt nobody had asked for. The
command only "checked" — that is not an excuse, because the prompt is the damage.

If a task needs a PR's content, a CI run's log or anything else behind GitHub, ask the
user to paste it or to check the branch out locally. Work from local git only.

### No network to verify upstream facts

No agent makes network calls to check an upstream fact: no web search or fetch, no
`curl`/`wget` to external hosts, no package-registry queries, no GitHub. A claim you
cannot settle from local files is written up as **`unverified — follow-up`**, with what
would settle it.

**The one exception is the Context7 MCP server** for library documentation. It is
configured for the agents, needs no authentication, and several agents are told to use
it. If Context7 fails or has no answer, mark the claim unverified — **do not fall back
to any other online source.**

Not covered, because none of these authenticates or looks anything up:

- `curl` against the local stack (`localhost:8080`, `:4000` and so on).
- Pulling a **public** image by any route — `docker compose up --build`, or a plain
  `docker run` such as the eval gate's `python:3.12-slim` run in
  [.claude/skills/eval-gate/SKILL.md](.claude/skills/eval-gate/SKILL.md).
- Ordinary package downloads during build and dependency work — `dotnet build` /
  `dotnet restore` from nuget.org, `npm ci` / `npm install` — since they need no login.

These are fetching what the build needs, not checking a fact. If any of them ever
raises a credential prompt, that is the ban applying again: stop and ask.

## Workflow: always start with the manager

Non-trivial work starts with the `manager` agent. It mints the task ID, writes the
original request and the plan under [.agent/](.agent/README.md), and names which agent
takes each step. Nothing else creates a task ID.

The main session then dispatches each step and **passes the task ID to every agent**,
because subagents cannot dispatch each other. Each agent files its notes under that
same ID, so one task is traceable across every stage by folder name.

A one-line answer or a trivial lookup does not need a task folder. Anything that
changes files does.

## Orientation

- [CODEBASE.md](CODEBASE.md) — full structure and analysis of the project
- [MCP.md](MCP.md) — MCP servers used with this project
- [SKILLS.md](SKILLS.md) — agent skills and subagents for this project
- [GETTING_STARTED.md](GETTING_STARTED.md) — the student-facing guide

Subagents live in [.claude/agents/](.claude/agents/) — eleven of them: `manager`,
`coder`, `reviewer`, `backend-reviewer`, `frontend-reviewer`, `docs-reviewer`, `critic`,
`devops`, `logger`, `tester`, `tech-writer`. They inherit this file, so the rules above
apply to them too. [SKILLS.md](SKILLS.md#subagents) describes each one.

## Notes

- Course content (README, homework, code comments, mock replies) is written in
  Ukrainian. Keep it that way when editing course material.
- The mandatory track runs entirely on the mock provider. Never introduce a
  dependency on a real API key in the required path.
- `litellm_settings.num_retries` is `0` on purpose: failures must reach the .NET
  service, which is where the student's reliability logic lives. Do not add hidden
  retries at the gateway.
