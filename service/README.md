# App service (.NET) — skeleton

Дається .NET-skeleton із HTTP-ендпоінтом `/chat` і підключенням до gateway та Postgres.
Це control plane — тут студент реалізує:

- **routing** — яку модель обрати під задачу (faq / escalation);
- **fallback** — порядок провайдерів при 429 / збої;
- **cost** — облік tokens і вартості на запит, budget-alert;
- **tool-хендлери** — lookup_order, create_ticket, з human approval перед незворотною дією;
- **guardrails** — PII, prompt injection, policy logs;
- **обзервабіліті** — розділ поверх логів у Postgres.

LiteLLM викликається лише для самого запиту до обраної моделі.

Skeleton готовий: `Program.cs` (`/chat` + API-контракт `/observability` `/cost` `/prompts` `/health` `/approvals`), `Service.csproj`, `Dockerfile`. Позначок `TODO(student)` у `service/` немає: що і коли дороблювати, див. [GETTING_STARTED.md, §9](../GETTING_STARTED.md#9-що-будуєш-ти-seamи).
