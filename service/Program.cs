// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-5-mini або claude-haiku + ключ у gateway/.env — реальна модель.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
// Swagger — для мейнтейнерів, вмикається лише SWAGGER_ENABLED=true або 1 (за замовч. вимкнено)
var swaggerFlag = (Environment.GetEnvironmentVariable("SWAGGER_ENABLED") ?? "").Trim();
var swaggerOn = swaggerFlag == "1" || swaggerFlag.Equals("true", StringComparison.OrdinalIgnoreCase);
if (swaggerOn)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}
var app = builder.Build();
if (swaggerOn)
{
    // через проксі UI (X-Forwarded-Prefix: /api) сервером стає префікс, інакше "Try it out"
    // піде повз проксі. servers ставимо на кожен запит, щоб прямий :8080 не отримав чужий префікс.
    app.UseSwagger(c => c.PreSerializeFilters.Add((doc, req) =>
    {
        var prefix = req.Headers["X-Forwarded-Prefix"].ToString();
        doc.Servers = prefix.Length > 0
            ? new List<OpenApiServer> { new() { Url = prefix } }
            : new List<OpenApiServer>();
    }));
    // відносний шлях: сторінка знаходить специфікацію і напряму, і під /api
    app.UseSwaggerUI(c => c.SwaggerEndpoint("v1/swagger.json", "Service v1"));
}

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";
// денний бюджет для картки Cost. без BUDGET_USD (або з непарсабельним значенням) — $5
var budgetUsd = decimal.TryParse(Environment.GetEnvironmentVariable("BUDGET_USD"),
    NumberStyles.Number, CultureInfo.InvariantCulture, out var budget) ? budget : 5m;
// cache (W3): скільки живе закешована відповідь. без CACHE_TTL_SECONDS (або з нулем/сміттям) — 600 с
var cacheTtlSeconds = int.TryParse(Environment.GetEnvironmentVariable("CACHE_TTL_SECONDS"), out var ttl) && ttl > 0 ? ttl : 600;
// tools (W3): бюджет часу на один виклик інструмента — спільний для всіх номерів у ньому (до 3), а не на кожен. без TOOL_TIMEOUT_MS (або з нулем/сміттям) — 2000 мс
var toolTimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("TOOL_TIMEOUT_MS"), out var tt) && tt > 0 ? tt : 2000;
// fallback (W4): бюджет часу на одну спробу моделі; минув — перемикаємось на наступну. без MODEL_ATTEMPT_TIMEOUT_MS (або з нулем/сміттям) — 30000 мс
var attemptTimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("MODEL_ATTEMPT_TIMEOUT_MS"), out var at) && at > 0
    ? at : (int)ModelClient.DefaultAttemptTimeout.TotalMilliseconds;
// circuit breaker (W4, опційно): скільки збоїв поспіль відкривають модель, верхня межа «відкритості» і як часто
// пускати пробний запит. без CIRCUIT_FAILURE_THRESHOLD / CIRCUIT_OPEN_SECONDS / CIRCUIT_PROBE_INTERVAL_SECONDS
// (або з нулем/сміттям) — 3 збої, 30 с, 5 с
var circuitDefaults = CircuitOptions.Default;
var circuitOptions = new CircuitOptions(
    int.TryParse(Environment.GetEnvironmentVariable("CIRCUIT_FAILURE_THRESHOLD"), out var cft) && cft > 0
        ? cft : circuitDefaults.FailureThreshold,
    int.TryParse(Environment.GetEnvironmentVariable("CIRCUIT_OPEN_SECONDS"), out var cos) && cos > 0
        ? TimeSpan.FromSeconds(cos) : circuitDefaults.OpenDuration,
    int.TryParse(Environment.GetEnvironmentVariable("CIRCUIT_PROBE_INTERVAL_SECONDS"), out var cpi) && cpi > 0
        ? TimeSpan.FromSeconds(cpi) : circuitDefaults.ProbeInterval);
// HITL (W4): скільки секунд заявка create_ticket чекає на підтвердження, потім — expired. за замовчуванням 900
var approvalTtlSeconds = int.TryParse(Environment.GetEnvironmentVariable("APPROVAL_TTL_SECONDS"), out var apt) && apt > 0
    ? apt : (int)ApprovalQueue.DefaultTtl.TotalSeconds;

// один кеш, один виконавець інструментів і один клієнт моделі (з лічильником fallback) на процес
// (див. ResponseCache.cs, Tools.cs, ModelClient.cs)
var responseCache = new ResponseCache(TimeSpan.FromSeconds(cacheTtlSeconds));
var toolRunner = new ToolRunner(new FakeOrderStore(), TimeSpan.FromMilliseconds(toolTimeoutMs));
var garbageReplies = new GarbageReplyCounter();  // W5: errors.garbage_reply (див. Observability.cs)
var modelClient = new ModelClient(app.Services.GetRequiredService<IHttpClientFactory>(), gateway,
    TimeSpan.FromMilliseconds(attemptTimeoutMs), app.Logger,
    circuitOptions, TimeProvider.System, ModelClient.ChainModels(defaultModel));
// HITL (W4): черга підтверджень і фейкове сховище тікетів — у пам'яті процесу (див. Approvals.cs)
var approvalQueue = new ApprovalQueue(new TicketStore(), TimeSpan.FromSeconds(approvalTtlSeconds), TimeProvider.System, app.Logger);

// routing (W2): моделі названі за роллю, а не за провайдером — так само, як у gateway/litellm-config.yaml
const string MiniModel = ModelClient.MiniModel;
const string StrongModel = ModelClient.StrongModel;
// третій маршрут (W2, опційно): середній tier для питань про замовлення й доставку —
// там модель має впевнено викликати інструмент, але це ще не ескалація
const string MidModel = ModelClient.MidModel;

// fallback chain (W4): для кожного tier свій ланцюжок — ModelClient.Chain():
// mock-strong -> mock-mid -> mock-mini, mock-mid -> mock-mini, mock-mini -> mock-mid, далі — DegradedReply.
// понад бюджет mock-mini — без кроку на дорожчу mock-mid: mock-mini -> DegradedReply.
// перемикаємось лише на 0 (немає відповіді/timeout), 429, 5xx і 200, що не розпарсився; на 4xx — ні.
// circuit breaker (див. CircuitBreaker.cs): модель після кількох таких збоїв поспіль відкрита — її пропускаємо без виклику,
// доки не вдасться пробний запит. усі моделі ланцюжка відкриті — одразу DegradedReply зі статусом 503.

// budget-політика (W2, опційно): з 80% денного бюджету (BUDGET_USD) — алерт у лог і деградація на mock-mini
const decimal BudgetAlertShare = 0.8m;

// обирає модель за задачею. Порядок: реальна модель із MODEL -> бюджет -> ескалація -> замовлення -> решта.
// Реальну модель із MODEL не чіпаємо: її обрав власник конфігурації.
// понад поріг бюджету все йде на найдешевшу mock-mini — навіть ескалації: бюджет важливіший за tier.
// fallback теж лишається на mini: понад бюджет ModelClient.Chain() не переходить на дорожчу mock-mid.
// ескалації (повернення грошей, терміново, скарги) — на mock-strong; замовлення/доставка — на mock-mid;
// решта — на дешеву mock-mini. Ескалація перемагає замовлення: «поверніть гроші за замовлення» — це mock-strong.
static string Route(string message, string defaultModel, bool overBudget)
{
    if (defaultModel != "mock")
        return defaultModel;
    if (overBudget || string.IsNullOrEmpty(message))
        return MiniModel;
    // основи слів, щоб ловити різні форми: «поверніть», «повернення», «терміново», «термінове», «скаргу», «скаржусь», «complaint»
    string[] markers = { "поверн", "термінов", "скарг", "скарж", "refund", "urgent", "complain" };
    // OrdinalIgnoreCase у .NET 8 зводить регістр і для кирилиці: «Поверніть» теж ловиться
    if (markers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase)))
        return StrongModel;
    // «замовлення», «замовленню», «доставка», «доставку», «посилка», «order», «delivery»
    string[] orderMarkers = { "замовлен", "доставк", "посилк", "order", "delivery" };
    return orderMarkers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase)) ? MidModel : MiniModel;
}

// cost (W2): ціни в USD за 1k токенів, вхід і вихід окремо. mock-strong ≈ 16× дорожча за mock-mini,
// mock-mid — посередині (4× mini, ≈ 4× дешевша за strong).
// ціни — живі дані: зміна ціни йде PR-ом, як виправлення бага.
// не опускати нижче: cost_usd — NUMERIC(10,6), і дешевий запит на mock-mini округлився б до 0.
// немає ціни (реальна модель з MODEL) — null, тобто "невідомо", а не 0: 0 лишаємо для cache hit (W3).
// ОБМЕЖЕННЯ: ціни (а з ними й budget-політика) є лише для mock-моделей. Реальна модель пише
// cost_usd = NULL, а SUM у /cost його пропускає — тож ні картка Cost, ні алерт бюджету її витрат не бачать.
static decimal? CostUsd(string model, int promptTokens, int completionTokens)
{
    (decimal In, decimal Out)? price = model switch
    {
        MiniModel => (0.00015m, 0.0006m),
        MidModel => (0.0006m, 0.0024m),
        StrongModel => (0.0025m, 0.01m),
        _ => null
    };
    return price is { } p ? (promptTokens * p.In + completionTokens * p.Out) / 1000m : null;
}

// день (UTC), за який алерт бюджету вже писали: лог раз на добу, а не на кожен запит.
// гонка двох запитів дасть щонайбільше дубль рядка в лозі — це не шкодить.
var budgetAlertDay = DateTime.MinValue;

// чи витрати за сьогодні вже досягли 80% бюджету. те саме "сьогодні", що й у GET /cost (див. BudgetSpentToday()).
// база недоступна — це не причина валити запит: маршрутизуємо без budget-політики.
// працює лише на mock: реальна модель не має ціни в CostUsd(), тож бюджет її витрат не рахує.
async Task<bool> OverBudget()
{
    // реальну модель Route() не чіпає, тож і зайвий запит у БД ні до чого
    if (defaultModel != "mock")
        return false;
    try
    {
        var spent = await BudgetSpentToday(dbConn);
        if (spent < budgetUsd * BudgetAlertShare)
            return false;
        if (budgetAlertDay != DateTime.UtcNow.Date)
        {
            budgetAlertDay = DateTime.UtcNow.Date;
            app.Logger.LogWarning("budget alert: spent {Spent} USD today, threshold {Share} of budget {Budget} USD reached; routing degraded to {Model}",
                spent, BudgetAlertShare, budgetUsd, MiniModel);
        }
        return true;
    }
    catch (Exception ex)
    {
        // один рядок без стек-трейсу: при збої БД це спрацює на кожен /chat
        app.Logger.LogWarning("budget check failed ({Error}), routing without budget policy", ex.Message);
        return false;
    }
}

app.MapPost("/chat", async (ChatIn body) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): спершу email → [email], потім пошук інʼєкції в замаскованому тексті (див. Guardrails.cs).
    // далі йде лише message, не body.Message: сирий email не потрапить ні в модель, ні в кеш, ні в інструменти.
    // інʼєкція або timeout regex — відмова без виклику моделі. у requests: model='guardrail', status='200',
    // prompt_version, токени й cost_usd — NULL.
    var guard = Guardrails.Inspect(body.Message);
    if (guard.Blocked)
    {
        // у лог — лише причина, без тексту повідомлення
        app.Logger.LogWarning("guardrail blocked request {RequestId}: {Reason}", requestId, guard.Reason);
        var blockedMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        await LogRequest(dbConn, requestId, Guardrails.BlockedModel, null, blockedMs, null, null, null, 200);
        // blocked = true — лише тут (див. Guardrails.BlockedResponse)
        return Results.Json(Guardrails.BlockedResponse(requestId, blockedMs));
    }
    var message = guard.Masked;

    // routing (W2): модель обираємо за задачею і бюджетом (див. Route(), OverBudget())
    // OverBudget() — запит у БД і алерт у лог, тож рахуємо раз і віддаємо і Route(), і Chain()
    var overBudget = await OverBudget();
    var model = Route(message, defaultModel, overBudget);

    // промпт (W1): активну версію беремо з реєстру (таблиця prompts)
    string promptVersion, systemPrompt;
    try
    {
        (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);
    }
    catch (Exception ex)
    {
        // база недоступна — це НЕ порожній реєстр: ми не знаємо промпта і не запишемо
        // рядок у requests, тому в модель не йдемо, а віддаємо помилку.
        app.Logger.LogError(ex, "prompt registry unavailable, request {RequestId} aborted", requestId);
        return Results.Json(new { request_id = requestId, error = "prompt registry unavailable" }, statusCode: 503);
    }

    // те, що піде в модель. з цього ж масиву (разом із моделлю й версією промпта) рахується ключ кешу
    var messages = new[] { new ChatMessage("system", systemPrompt), new ChatMessage("user", message) };

    // cache (W3): повтор того самого запиту (модель + промпт + повідомлення) після відповіді в модель не ходить —
    // його віддає кеш. однакові запити, що прийшли одночасно, теж не множать викликів: модель викликає лише
    // перший, решта чекають його результат (single-flight, див. ResponseCache.GetOrCallAsync).
    // hit і очікування вдалого виклику пишемо в requests з 0 токенів — власного виклику моделі в них не було.
    // 0/0 означає лише кеш: деградація й очікування невдалого виклику пишуть NULL-токени.
    var cacheKey = ResponseCache.Key(model, promptVersion, messages);
    // fallback (W4): виклик іде ланцюжком моделей (див. ModelClient.Chain()) всередині single-flight —
    // один прохід ланцюжка обслуговує і лідера, і всіх, хто чекав. ключ кешу — за обраною моделлю.
    var lookup = await responseCache.GetOrCallAsync(cacheKey,
        () => modelClient.CallAsync(ModelClient.Chain(model, defaultModel, overBudget), messages));
    // модель, що фактично відповіла: після fallback це не та, яку обрав Route(). hit і деградація — обрана
    // (правило й тести — RequestRow.ServedModel)
    var servedModel = RequestRow.ServedModel(lookup, model);
    app.Logger.LogInformation("cache {Result} for request {RequestId}: model {Served} (routed {Routed}), hits={Hits} misses={Misses}",
        lookup.Source switch
        {
            CacheSource.Hit => "hit",
            CacheSource.Joined => "hit (waited for an identical in-flight request)",
            CacheSource.JoinedFailed => "miss (waited for an identical in-flight request that failed)",
            _ => "miss"
        }, requestId, servedModel, model, responseCache.Hits, responseCache.Misses);

    var (answer, toolCall, toolArgs) = (lookup.Reply.Content, lookup.Reply.ToolName, lookup.Reply.ToolArguments);
    // токени, вартість і статус рядка в requests (правило NULL/0 — див. RequestRow.cs)
    var (promptTokens, completionTokens, costUsd, status) = RequestRow.Compose(lookup, servedModel, CostUsd);

    // garbage (W5): 200 без змісту ("...") — користувачу чесна відповідь; статус '200', токени й вартість лишаються.
    // не збій провайдера: fallback і запобіжник його не бачать, у кеш не йде (правило — ReplyQuality, Observability.cs).
    // з tool call не підмінюємо; кожну підміну рахує errors.garbage_reply (GarbageReplyCounter)
    if (garbageReplies.TryReplace(toolCall, answer, out var honest))
    {
        // у лог — лише факт і модель, без тексту повідомлення
        app.Logger.LogWarning("garbage reply for request {RequestId} from model {Served}, replaced with honest reply",
            requestId, servedModel);
        answer = honest;
    }

    // tools + HITL (W3/W4): lookup_order виконуємо (з timeout і валідацією номера, див. Tools.cs) і
    // дописуємо результат до відповіді. виконується на кожному запиті — і на hit, і на очікуванні:
    // single-flight об'єднує лише виклики моделі, не інструменти.
    // create_ticket — незворотна дія: ToolRunner її не виконує, ми ставимо заявку в чергу HITL (див. Approvals.cs).
    // тікет створює лише POST /approvals/{id}/approve, тож у відповіді — id заявки, а не номер тікета.
    if (toolCall != null)
    {
        var tool = await toolRunner.RunAsync(toolCall, toolArgs, message);
        answer += tool.Suffix;
        var notChecked = tool.NotChecked > 0 ? $", {tool.NotChecked} more not checked (limit {ToolRunner.MaxOrderIds})" : "";
        if (tool.Outcome is ToolOutcome.Ok or ToolOutcome.NotFound or ToolOutcome.NotExecuted)
            app.Logger.LogInformation("tool {Tool} for request {RequestId}: {Outcome}, orders {Orders}{NotChecked}",
                toolCall, requestId, tool.Outcome, tool.Describe(), notChecked);
        else
            app.Logger.LogWarning("tool {Tool} for request {RequestId}: {Outcome}, orders {Orders}{NotChecked}{Detail}",
                toolCall, requestId, tool.Outcome, tool.Describe(), notChecked,
                tool.Outcome == ToolOutcome.ValidationRejected ? " (validation: no valid order number in arguments or message, not executed)"
                : tool.Outcome == ToolOutcome.Timeout ? $" (timeout {toolTimeoutMs} ms for the whole tool call)" : "");

        if (toolCall == "create_ticket")
        {
            // дубль (той самий номер з #/№) отримує id наявної заявки; протермінована попередня — нова заявка
            // і повідомлення, що попередню не опрацювали вчасно (див. ApprovalQueue.ReplySuffix)
            var queued = approvalQueue.Enqueue(toolCall, guard);
            answer += ApprovalQueue.ReplySuffix(queued);
            // у лог — лише id і чи це дубль, без тексту повідомлення
            app.Logger.LogInformation("approval {ApprovalId} for request {RequestId}: {Kind}",
                queued.Approval.Id, requestId,
                !queued.Created ? "duplicate of existing"
                : queued.PreviousExpiredId is { } prev ? $"queued (previous {prev} expired)" : "queued");
        }
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2).
    // model — та, що відповіла (W4), щоб model і cost_usd у рядку збігались
    await LogRequest(dbConn, requestId, servedModel, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає консоль (ui/src/app/console.component.ts): форма кожного — у коментарі поруч.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
// W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events,
//   errors: { rate_limited_429, provider_5xx, timeout, client_4xx, guardrail_blocked, tool_timeout, garbage_reply },
//   circuits: { <модель>: closed | open | half_open }, approvals_expired }
// усі лічильники й плитки — числа; circuits — рядки стану. p95_ms, requests, error_rate_pct і перші п'ять полів errors — з requests за сьогодні
// (див. Observability.TodaySql і DaySummary). база недоступна — ці поля null, решта є, HTTP 200
app.MapGet("/observability", async () =>
{
    DaySummary? day = null;
    try
    {
        day = await Observability.TodayAsync(dbConn);
    }
    catch (Exception ex)
    {
        // один рядок без стек-трейсу: консоль опитує цей ендпоінт періодично
        app.Logger.LogWarning("requests table unavailable ({Error}), GET /observability returns in-memory fields only", ex.Message);
    }
    // решта полів — лічильники в пам'яті з моменту старту: див. Observability.HitPct, ModelClient.FallbackEvents,
    // ToolRunner.Timeouts, GarbageReplyCounter, ModelClient.CircuitStates, ApprovalQueue.ExpiredCount.
    // errors, circuits і approvals_expired консоль не читає — це для ручної перевірки стану без читання логів
    return Results.Json(Observability.ObservabilityResponse(day,
        Observability.HitPct(responseCache.Hits, responseCache.Misses), modelClient.FallbackEvents,
        toolRunner.Timeouts, garbageReplies.Count, modelClient.CircuitStates(), approvalQueue.ExpiredCount));
});
app.MapGet("/cost", async () =>                                                                    // W2/W5: { today_usd, budget_usd }
{
    // сума рахується в SQL з тим самим created_at::date = CURRENT_DATE, що й у перевірці ментора,
    // щоб "сьогодні" визначала база, а не годинник контейнера сервісу
    try
    {
        return Results.Json(new { today_usd = await CostToday(dbConn), budget_usd = budgetUsd });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "requests table unavailable, GET /cost failed");
        return Results.Json(new { error = "cost unavailable" }, statusCode: 503);
    }
});
app.MapGet("/prompts", async () =>                                                                 // W1/W2: [ { name, version, active, activated_by, created_at, activated_at } ]
{
    // усі версії, не лише активна: консоль показує і поточну, і попередні
    try
    {
        return Results.Json(await ListPrompts(dbConn));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "prompt registry unavailable, GET /prompts failed");
        return Results.Json(new { error = "prompt registry unavailable" }, statusCode: 503);
    }
}).Produces<List<PromptOut>>();
app.MapPost("/prompts/{version}/activate", async (string version, HttpRequest req) =>
{
    // одна атомарна операція: EXISTS перевіряє версію в тому ж UPDATE.
    // немає такої версії — не змінюється жоден рядок, і активна лишається як була.
    // activated_by — мітка, яку клієнт назвав сам, а не перевірена особа. Звідки беремо:
    // тіло { "activated_by": "..." } (браузер не пошле кирилицю в заголовку), інакше
    // заголовок X-Activated-By (для curl), інакше NULL. Довге обрізаємо до 100 символів.
    var by = await ReadLabel(req, "activated_by");
    if (by.Length == 0)
        by = req.Headers["X-Activated-By"].ToString().Trim();
    string? activatedBy = by.Length == 0 ? null : by.Length > 100 ? by[..100] : by;
    try
    {
        var changed = await ActivatePrompt(dbConn, version, activatedBy);
        return changed == 0
            ? Results.Json(new { error = "prompt version not found", version }, statusCode: 404)
            : Results.Json(new { name = "support-system", version, active = true });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "prompt registry unavailable, activation of {Version} failed", version);
        return Results.Json(new { error = "prompt registry unavailable" }, statusCode: 503);
    }
});
// W5: { providers: [ { name, status } ] } — по одному на кожну модель ланцюжків (ModelClient.ChainModels), status: ok | degraded | down.
// правило статусу — див. Observability.ProviderStatuses
app.MapGet("/providers", () => Results.Json(
    Observability.ProvidersResponse(ModelClient.ChainModels(defaultModel), modelClient.CircuitOf,
        modelClient.HasClientErrorStreak)));
// W4 (форма з уроку L08): { pending: [ { id, action, message, created_at } ],
//   done: [ { id, action, result, approved_by, approved_at } ], rejected: [ { id, action, rejected_by, rejected_at, reason } ],
//   expired: [ { id, action, created_at, expired_at } ] }
// у кожному списку найновіші першими; message — замаскований текст (до 200 символів); approved_by / rejected_by /
// reason — null, якщо не названо. консоль читає pending (id, action); done/rejected/expired — для перевірки й аудиту
app.MapGet("/approvals", () => Results.Json(approvalQueue.List()));
// W4: 200 { id, status: "done", result: "TCK-…", approved_by, approved_at };
// 404 { error } — немає такої заявки, вже оброблена, ще обробляється або протермінована (нічого не змінюється);
// 503 { error } — тікет не створився, заявка знову pending
app.MapPost("/approvals/{id}/approve", async (string id, HttpRequest req) =>
{
    // approved_by — мітка, яку назвав клієнт, як activated_by в активації промпта: тіло { "approved_by": "..." },
    // інакше заголовок X-Approved-By, інакше NULL
    var by = DecisionLabel((await ReadLabels(req, "approved_by"))[0], req.Headers["X-Approved-By"]);
    DoneApproval? done;
    try
    {
        done = approvalQueue.Approve(id, by);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "approval {ApprovalId} by {By}: ticket store failed, approval is pending again", id, by ?? "-");
        return Results.Json(new { error = "ticket store unavailable, approval is pending again" }, statusCode: 503);
    }
    if (done == null)
        return Results.Json(new { error = "approval not found or already done" }, statusCode: 404);
    // у лог — id заявки й тікета та хто підтвердив, без тексту заявки
    app.Logger.LogInformation("approval {ApprovalId} approved by {By}: ticket {Ticket}", done.Id, done.ApprovedBy ?? "-", done.Result);
    return Results.Json(new { id = done.Id, status = "done", result = done.Result,
        approved_by = done.ApprovedBy, approved_at = done.ApprovedAt });
});
// W4: 200 { id, status: "rejected", rejected_by, rejected_at, reason }; той самий 404, що й в approve.
// тіло { "rejected_by": "...", "reason": "..." } (обидва необовʼязкові), інакше заголовок X-Rejected-By.
// інструмент не виконується; відхилена заявка звільняє номер замовлення для нової заявки
app.MapPost("/approvals/{id}/reject", async (string id, HttpRequest req) =>
{
    var labels = await ReadLabels(req, "rejected_by", "reason");
    var by = DecisionLabel(labels[0], req.Headers["X-Rejected-By"]);
    string? reason = labels[1].Length == 0 ? null : ApprovalQueue.Clip(labels[1], ApprovalQueue.MaxReasonChars);
    var rejected = approvalQueue.Reject(id, by, reason);
    if (rejected == null)
        return Results.Json(new { error = "approval not found or already done" }, statusCode: 404);
    // причину в лог не пишемо: це вільний текст оператора
    app.Logger.LogInformation("approval {ApprovalId} rejected by {By}", rejected.Id, rejected.RejectedBy ?? "-");
    return Results.Json(new { id = rejected.Id, status = "rejected", rejected_by = rejected.RejectedBy,
        rejected_at = rejected.RejectedAt, reason = rejected.Reason });
});

app.Logger.LogInformation("MODEL={Model}, fallback chains: {Chains}, attempt timeout {Timeout} ms, "
    + "circuit breaker: {Threshold} consecutive failures, probe after {Probe} s, open at most {Open} s",
    defaultModel, ModelClient.DescribeChains(defaultModel), attemptTimeoutMs,
    circuitOptions.FailureThreshold, circuitOptions.ProbeAfter.TotalSeconds, circuitOptions.OpenDuration.TotalSeconds);
app.Run("http://0.0.0.0:8080");

// витрати за сьогодні. COALESCE: немає рядків чи всі cost_usd NULL — це 0, а не null.
// помилку БД не ковтаємо — ендпоінт віддасть 503.
static async Task<decimal> CostToday(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT COALESCE(SUM(cost_usd), 0) FROM requests WHERE created_at::date = CURRENT_DATE", db);
    return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

// та сама сума за сьогодні для budget-політики, що йде на кожен /chat. Діапазон замість
// created_at::date: "сьогодні" так само за часовим поясом сесії БД, але запит може йти
// індексом idx_requests_created_at. помилку БД не ковтаємо — її ловить OverBudget().
static async Task<decimal> BudgetSpentToday(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT COALESCE(SUM(cost_usd), 0) FROM requests "
        + "WHERE created_at >= CURRENT_DATE AND created_at < CURRENT_DATE + 1", db);
    return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

// дістає активний промпт із реєстру.
// читаємо на КОЖЕН запит, а не один раз на старті: інакше активація нової версії чи
// відкат діяли б лише після перезапуску процесу. Кешування з керованим інвалідуванням — Тема 3.
// повертає ("none", резервний текст), якщо запит пройшов, але активної версії немає —
// у резервному тексті свідомо немає маркера support, щоб поломка реєстру була видна
// у відповіді mock-провайдера. Помилку БД НЕ ковтаємо: виняток летить до /chat.
static async Task<(string Version, string Body)> GetActivePrompt(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT version, body FROM prompts WHERE name = 'support-system' AND active = true ORDER BY created_at DESC LIMIT 1", db);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
        return (reader.GetString(0), reader.GetString(1));

    return ("none", "You are an assistant. No active prompt version is configured.");
}

// усі версії промптів для консолі. помилку БД не ковтаємо — ендпоінт віддасть 503.
// activated_by — з ОСТАННЬОЇ активації цієї версії (навіть якщо там NULL): інакше
// пізніша активація без імені показувала б старе імʼя.
static async Task<List<PromptOut>> ListPrompts(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT p.name, p.version, p.active, a.activated_by, p.created_at, a.activated_at FROM prompts p "
        + "LEFT JOIN LATERAL (SELECT pa.activated_by, pa.activated_at FROM prompt_activations pa "
        + "WHERE pa.name = p.name AND pa.version = p.version ORDER BY pa.id DESC LIMIT 1) a ON true "
        + "ORDER BY p.name, p.created_at", db);
    await using var reader = await cmd.ExecuteReaderAsync();
    var list = new List<PromptOut>();
    // обидві колонки timestamptz: DateTimeOffset серіалізується з явним зсувом (+00:00),
    // тож браузер не прочитає час як локальний
    while (await reader.ReadAsync())
        list.Add(new PromptOut(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
    return list;
}

static async Task<string> ReadLabel(HttpRequest req, string field) => (await ReadLabels(req, field))[0];

// дістає рядкові поля (activated_by, approved_by, rejected_by, reason) з необовʼязкового JSON-тіла — за одне
// читання потоку. порожнє тіло, {}, не-JSON, не той Content-Type чи не рядок — це "значення немає" (""),
// а не 400: кнопка без імені шле {}.
static async Task<string[]> ReadLabels(HttpRequest req, params string[] fields)
{
    var values = fields.Select(_ => "").ToArray();
    try
    {
        using var sr = new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true);  // потік тіла належить Kestrel
        var raw = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return values;
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return values;
        for (var i = 0; i < fields.Length; i++)
            if (doc.RootElement.TryGetProperty(fields[i], out var v) && v.ValueKind == JsonValueKind.String)
                values[i] = (v.GetString() ?? "").Trim();
        return values;
    }
    catch (JsonException)
    {
        return values;
    }
}

// хто ухвалив рішення по заявці: з тіла, інакше із заголовка (для curl), інакше NULL.
// довге обрізаємо до 100 символів, не розриваючи емодзі
static string? DecisionLabel(string fromBody, string? header)
{
    var by = fromBody.Length > 0 ? fromBody : (header ?? "").Trim();
    return by.Length == 0 ? null : ApprovalQueue.Clip(by, ApprovalQueue.MaxLabelChars);
}

// перемикає активну версію і пише подію в журнал активацій — в ОДНІЙ транзакції,
// щоб стан реєстру й історія не могли розійтися.
// повертає кількість змінених рядків (0 — версії немає: нічого не пишемо, відкат).
// будь-яка помилка БД летить до ендпоінта (503), а незакомічена транзакція відкочується при dispose.
static async Task<int> ActivatePrompt(string conn, string version, string? activatedBy)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();

    await using var cmd = new NpgsqlCommand(
        "UPDATE prompts SET active = (version = @v) WHERE name = 'support-system' "
        + "AND EXISTS (SELECT 1 FROM prompts WHERE name = 'support-system' AND version = @v)", db, tx);
    cmd.Parameters.AddWithValue("v", version);
    var changed = await cmd.ExecuteNonQueryAsync();
    if (changed == 0)
        return 0;  // без Commit: dispose відкотить транзакцію

    // одна подія на виклик, а не на кожен змінений рядок (UPDATE зачіпає всі версії імені)
    await using var log = new NpgsqlCommand(
        "INSERT INTO prompt_activations (name, version, activated_by) VALUES ('support-system', @v, @by)", db, tx);
    log.Parameters.AddWithValue("v", version);
    log.Parameters.AddWithValue("by", (object?)activatedBy ?? DBNull.Value);
    await log.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return changed;
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
// NULL у prompt_version і токенах — у заблокованого guardrails запиту (до реєстру промптів і моделі він не дійшов).
// NULL лише в токенах — на деградації та на очікуванні невдалого виклику (usage немає); 0 токенів — тільки hit кешу
static async Task LogRequest(string conn, Guid id, string model, string? promptVersion, int latency,
    int? promptTokens, int? completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @pv, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pv", (object?)promptVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", (object?)promptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", (object?)completionTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString(CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

record ChatIn(string Message);
// без JsonPropertyName веб-серіалізатор віддав би activatedBy (camelCase), а консоль читає activated_by
record PromptOut(string Name, string Version, bool Active,
    [property: JsonPropertyName("activated_by")] string? ActivatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("activated_at")] DateTimeOffset? ActivatedAt);
