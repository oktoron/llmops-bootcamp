# MCP.md

MCP (Model Context Protocol) servers used with this project, how they are configured,
and which ones were evaluated and rejected.

VS Code reads MCP configuration from either a user-profile file or a workspace
`.vscode/mcp.json`. All servers for this project are kept in **one** file, the user
profile, so there is a single place to look:

```
~/.config/Code/User/mcp.json
```

> **Note:** VS Code uses a top-level `servers` key. Claude Desktop and Cursor use
> `mcpServers`. Configuration copied between them needs the key renamed, and VS Code
> also expects an explicit `"type"` (`stdio` or `http`).

---

## Current configuration

`~/.config/Code/User/mcp.json`

```json
{
  "servers": {
    "playwright": {
      "type": "stdio",
      "command": "npx",
      "args": [
        "@playwright/mcp@latest"
      ]
    },
    "context7": {
      "type": "stdio",
      "command": "npx",
      "args": [
        "-y",
        "@upstash/context7-mcp@latest"
      ]
    },
    "microsoft-learn": {
      "type": "http",
      "url": "https://learn.microsoft.com/api/mcp"
    },
    "github": {
      "type": "http",
      "url": "https://api.githubcopilot.com/mcp/"
    },
    "postgres": {
      "type": "stdio",
      "command": "npx",
      "args": [
        "-y",
        "@bytebase/dbhub@latest",
        "--transport",
        "stdio",
        "--dsn",
        "postgres://llmops:llmops@localhost:5432/llmops?sslmode=disable"
      ]
    },
    "filesystem": {
      "type": "stdio",
      "command": "npx",
      "args": [
        "-y",
        "@modelcontextprotocol/server-filesystem",
        "/home/apakki/llmops/llmops-bootcamp"
      ]
    }
  }
}
```

---

## Servers in use

### Playwright

| | |
|---|---|
| Package | `@playwright/mcp@latest` |
| Transport | stdio, via `npx` |

Browser automation and inspection. In this project it drives the Angular UI on
`http://localhost:4200`: sending a message through the Chat view, confirming the
latency line and the tool chip (`🔧 lookup_order`) render, and checking whether
Console tiles show real numbers or the `—` placeholder after an endpoint is
implemented. Homework submissions require a console screenshot, which this can take.

Needs browsers installed on first use (`npx playwright install`).

### Context7

| | |
|---|---|
| Package | `@upstash/context7-mcp@latest` |
| Transport | stdio, via `npx` |

Fetches current, version-specific library documentation, which counters the model's
training cutoff. Relevant here for LiteLLM configuration (the config format moves
quickly), Npgsql, Angular 17 APIs, and the GitHub Actions syntax in the eval gate.

A hosted variant exists (`{"type": "http", "url": "https://mcp.context7.com/mcp"}`)
if you would rather not run Node locally. An optional API key raises rate limits.

**Registered with Claude Code as well**, because the subagents in `.claude/agents/`
require it and Claude Code does not read VS Code's `mcp.json`:

```sh
claude mcp add context7 --scope user -- npx -y @upstash/context7-mcp@latest
```

User scope, matching the VS Code arrangement. All eleven subagents are granted it via
`mcpServers: context7` in their frontmatter; `coder`, `critic` and the four review agents
are told to use it on every library API they write or judge. See
[SKILLS.md](SKILLS.md). Verify with `claude mcp list`; remove with
`claude mcp remove context7 --scope user`.

**It is the only online source agents may use.** The "Authentication and network" rule
in [CLAUDE.md](CLAUDE.md) bans web search and fetch, `gh`, and every other route to
upstream facts; Context7 is the single exception because it needs no authentication.
If it fails or has no answer, the claim is marked `unverified — follow-up` — there is
no fallback to another online source.

### Microsoft Learn

| | |
|---|---|
| Endpoint | `https://learn.microsoft.com/api/mcp` |
| Transport | streamable HTTP |
| Auth | None |

Official Microsoft server exposing Microsoft Learn documentation: search, full
article fetch, and code-sample search. This is the C#/.NET answer for this repo —
`service/` and `mock-provider/` are .NET 8 minimal APIs, and this covers minimal API
routing, `HttpClient` and `IHttpClientFactory`, configuration, and ASP.NET hosting
without guessing.

### GitHub

| | |
|---|---|
| Endpoint | `https://api.githubcopilot.com/mcp/` |
| Transport | streamable HTTP (hosted by GitHub) |
| Auth | OAuth on first use, browser-based; token held in memory |
| Project | [github/github-mcp-server](https://github.com/github/github-mcp-server) |

GitHub's official server. It matters here because **this course runs on pull
requests**: students submit each homework as a PR, the eval gate runs on
`pull_request`, HW3 requires decision write-ups in the PR description, HW6 requires
README sections, and review is a verdict of accepted or needs rework with at most
three main remarks.

This server gives structured access to a student's PR diff, the eval-gate check run
and its logs, and existing review comments.

**For the maintainer in VS Code, not for agents.** It authenticates with a browser
OAuth prompt, which is exactly what the "Authentication and network" rule in
[CLAUDE.md](CLAUDE.md) forbids an agent to trigger. It is not registered with Claude
Code, and no subagent uses it or the `gh` CLI. `reviewer` works from local git — the
PR branch checked out, or a diff — and when it needs a PR description or a CI log, it
asks the user to paste it. Use this server yourself when you want GitHub data, then
hand the agent the result.

Two hardening options, both on the local Docker variant rather than the hosted
endpoint: `--read-only`, and `--toolsets` / `--tools` to narrow the default set
(context, repos, issues, pull requests, users). Worth using if you ever point this at
an organization account rather than your own. A Personal Access Token via
`GITHUB_PERSONAL_ACCESS_TOKEN` takes precedence over OAuth.

### Postgres (DBHub)

| | |
|---|---|
| Package | `@bytebase/dbhub@latest` |
| Transport | stdio, via `npx` |
| Target | `postgres://llmops:llmops@localhost:5432/llmops` |

Direct access to the course database — the `requests` log and the `prompts` registry.
It removes the paste-the-psql-output loop when verifying HW1 prompt seeding, checking
that `prompt_version` and `cost_usd` are actually being written, or working out the
p95 and error-rate aggregations for HW5.

Chosen over alternatives because it loads only 2 tools by default (~1.4k tokens),
roughly 13–14× fewer than comparable servers. That matters when Playwright and
Context7 are already consuming context.

**Read-write on purpose.** The database is disposable (`docker compose down -v`) and
HW1 involves seeding prompt rows. DBHub does have a read-only mode, but its own
documentation notes that a genuine guarantee comes from connecting as a read-only
database role, not from the flag. If you want it locked down, create a read-only role
and put that in the DSN.

**Requires the stack to be running.** The DSN points at the published host port, so
`docker compose up -d postgres` (or the full stack) must be up. If ports were shifted
via `docker-compose.override.yml`, update the port here to 15432, or 25432 for
`demo-ports.yml`.

**This is the one project-specific entry in a user-level file.** It is kept there
deliberately, so all servers live in one place, but that has two consequences: the
server loads in every workspace you open, and it fails to connect whenever the course
stack is down. Both are harmless — a failed stdio server shows as unavailable and
does not block the others — but if the noise becomes annoying, move just this entry
into a workspace `.vscode/mcp.json` and note it here.

### Filesystem

| | |
|---|---|
| Package | `@modelcontextprotocol/server-filesystem` |
| Transport | stdio, via `npx` |
| Allowed directory | `/home/apakki/llmops/llmops-bootcamp` |

One of the seven reference servers still maintained after the archiving, so unlike
`server-postgres` or `server-github` it is a safe choice. Allowed directories are
**positional arguments** after the package name; at least one is required. Add more by
appending paths to `args`.

Exposes nine read tools (`read_text_file`, `read_media_file`, `read_multiple_files`,
`list_directory`, `list_directory_with_sizes`, `directory_tree`, `search_files`,
`get_file_info`, `list_allowed_directories`) and four write tools (`create_directory`,
`write_file`, `edit_file`, `move_file`).

**Two caveats worth knowing, since they limit what this actually buys here:**

1. **It duplicates native tooling.** Both Claude Code and VS Code Copilot already have
   first-class file read, write, edit, glob and grep. Thirteen more tools that do the same
   jobs is context spent for capability already present — the opposite of the reasoning
   that picked DBHub for its two-tool default. It is not registered with Claude Code for
   this reason; Claude Code's native tools are strictly better there.
2. **The MCP Roots protocol overrides the allowed directories.** When a client supports
   roots, the roots it sends *completely replace* the server-side list. So in VS Code the
   effective scope is likely the open workspace rather than the path configured above, and
   the positional argument is a fallback for clients that do not send roots.

Where a filesystem server genuinely earns its place is a client with **no** native file
access — Claude Desktop being the usual case — or a deliberately sandboxed setup where
an agent gets file access *only* through a scoped server. Note that the sandbox idea does
not work for this project's review agents: they need `Bash` for local git (`git diff`,
`git log`), and `Bash` reaches the filesystem regardless.

Remove with `"filesystem": null` semantics — that is, delete the entry.

---

## Evaluated and rejected

| Candidate | Decision | Reasoning |
|---|---|---|
| `docker/mcp-gateway` | Not used | Commonly mistaken for a container-management server. It is an **aggregator** that runs and proxies *other* MCP servers behind one endpoint. It does not expose Docker management tools itself. |
| `ckreiling/mcp-server-docker` | Not used | The genuine Docker-management option (`uvx mcp-server-docker`, 20+ tools across containers, images, volumes, networks and Compose). Rejected because the agent already runs `docker compose` through the terminal, so it adds daemon-level tool surface for little gain. Reconsider if you want structured container introspection rather than parsed CLI output. |
| Roslyn C# servers (VuDZ, kirnot92, HYMMA, SharpTools) | Not used | No official or dominant implementation exists — several competing community projects, some requiring .NET 10 or Visual Studio 2026. The C# Dev Kit extension already surfaces Roslyn diagnostics and navigation to the agent in VS Code, so the real gap is API knowledge, which Microsoft Learn covers. |
| `crystaldba/postgres-mcp` | Not used, but a reasonable swap | Adds `EXPLAIN` plans, health checks and index tuning. Overkill for a two-table schema; worth revisiting if the HW5 observability aggregations turn into query tuning. |
| Angular CLI MCP (`ng mcp`) | Not usable | Requires Angular 20.1+ (officially 21+). This repo pins Angular 17, and `ui/` is explicitly not modified by students. |
| `@modelcontextprotocol/server-postgres` | **Avoid** | Deprecated July 2025 and archived. Its read-only mode accepted `COMMIT; DROP SCHEMA public CASCADE;`, and it carried a documented SQL injection vulnerability. DBHub replaces it. |
| `@modelcontextprotocol/server-puppeteer` | **Avoid** | Archived. `@playwright/mcp` replaces it and is maintained by Microsoft. |
| `@modelcontextprotocol/server-github` | **Avoid** | Archived. GitHub's own `github/github-mcp-server` replaces it — see above. |
| `@modelcontextprotocol/server-brave-search` | **Avoid** | Archived, and needs an API key. General web search is also off-limits to agents under the "Authentication and network" rule in [CLAUDE.md](CLAUDE.md); Context7 is the only sanctioned online source. |
| `@modelcontextprotocol/server-memory` | Not used | Still maintained, but redundant: [CLAUDE.md](CLAUDE.md) holds the project rules and the agent keeps its own cross-session memory. |

> **On archived reference servers.** Most of Anthropic's original reference servers
> now live in
> [`modelcontextprotocol/servers-archived`](https://github.com/modelcontextprotocol/servers-archived),
> which states that they receive no security updates, no bug fixes, and carry no
> security guarantees. Only `Everything`, `Fetch`, `Filesystem`, `Git`, `Memory`,
> `Sequential Thinking` and `Time` remain in the live repository. Many blog posts and
> "top MCP servers" listicles still recommend the archived ones, so check the source
> repository before adopting anything from such a list.

---

## Operational notes

- **Verify servers loaded:** command palette → *MCP: List Servers*. Reload the window
  if a newly added server does not appear.
- **Nothing here is shared with students.** The configuration lives in the user
  profile, outside the repository, so cloning the starter does not bring these
  servers. That is intentional: a workspace `.vscode/mcp.json` would be git-ignored
  anyway by the `.vscode/*` rule in [.gitignore](.gitignore#L30), and sharing a
  database server would hand students direct write access to the course database.
- **Secrets never belong in `mcp.json`.** Provider keys live in `gateway/.env`, which
  is git-ignored. If a future server needs a key, use VS Code's `inputs` mechanism
  rather than inlining it.
- **`npx` servers fetch on every start.** Pin a version instead of `@latest` if you
  need reproducibility across machines — the same reasoning that pins the LiteLLM
  image by digest in `docker-compose.yml`.
- **The GitHub server authenticates on first use.** Expect a browser OAuth prompt the
  first time a GitHub tool is called. Until that completes, the server shows as
  available but its tools fail. That prompt is for you to answer when you choose to use
  the server — an agent must never be the thing that raises it.
- **Claude Code does not read VS Code's `mcp.json`.** It keeps its own registry —
  inspect it with `claude mcp list`. Only `context7` is registered there so far, because
  the subagents need it; `playwright`, `microsoft-learn`, `github` and `postgres` are
  VS Code only. If a subagent ever needs one of the auth-free ones, add it the same way:
  `claude mcp add <name> --scope user -- <command>`. **Do not register `github`** — it
  would put an OAuth flow within an agent's reach, which [CLAUDE.md](CLAUDE.md) bans.
