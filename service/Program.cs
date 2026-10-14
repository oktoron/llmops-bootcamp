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

// один кеш і один виконавець інструментів на процес (див. ResponseCache.cs, Tools.cs)
var responseCache = new ResponseCache(TimeSpan.FromSeconds(cacheTtlSeconds));
var toolRunner = new ToolRunner(new FakeOrderStore(), TimeSpan.FromMilliseconds(toolTimeoutMs));

// routing (W2): моделі названі за роллю, а не за провайдером — так само, як у gateway/litellm-config.yaml
const string MiniModel = "mock-mini";
const string StrongModel = "mock-strong";
// третій маршрут (W2, опційно): середній tier для питань про замовлення й доставку —
// там модель має впевнено викликати інструмент, але це ще не ескалація
const string MidModel = "mock-mid";

// fallback chain: mock-strong -> mock-mid -> mock-mini -> контрольована помилка. Поки це лише рядок — на W4 він стане кодом.
// кожен tier падає на наступний дешевший; mock-mid стартує з другого кроку, mock-mini — з третього.
// mock-mini і mock-mid живуть на тому ж провайдері, що й mock-strong, тож падіння провайдера цей ланцюжок не переживе.
// Провайдер з іншим ключем (gemini — закоментований блок у gateway/litellm-config.yaml) стає
// другим кроком: mock-strong -> gemini -> mock-mid -> mock-mini -> контрольована помилка.
const string FallbackChain = "mock-strong -> mock-mid -> mock-mini -> контрольована помилка";

// budget-політика (W2, опційно): з 80% денного бюджету (BUDGET_USD) — алерт у лог і деградація на mock-mini
const decimal BudgetAlertShare = 0.8m;

// обирає модель за задачею. Порядок: реальна модель із MODEL -> бюджет -> ескалація -> замовлення -> решта.
// Реальну модель із MODEL не чіпаємо: її обрав власник конфігурації.
// понад поріг бюджету все йде на найдешевшу mock-mini — навіть ескалації: бюджет важливіший за tier.
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

// один виклик моделі через gateway — фабрика для single-flight у ResponseCache.GetOrCallAsync.
// ніколи не кидає: збій — це вибачення з UsageParsed = false (status лишиться 0/5xx).
// без RequestAborted свідомо: результат можуть чекати інші запити, і відключення одного клієнта
// не має скасувати спільний виклик.
async Task<ModelCall> CallModel(IHttpClientFactory httpFactory, string model, ChatMessage[] messages)
{
    // fallback (W4): якщо тут 429/5xx — піти на іншого провайдера. поки один виклик.
    // TODO(student, W4)
    var payload = JsonSerializer.Serialize(new { model, messages });
    var status = 0;  // 0 = відповіді не було

    var http = httpFactory.CreateClient();
    try
    {
        var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        status = (int)response.StatusCode;
        var rawJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(rawJson);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var answer = message.GetProperty("content").GetString() ?? "";

        // tools (W3): читаємо назву і аргументи першого tool call; виконання — у /chat, спільне для hit, miss і очікування
        string? toolCall = null, toolArgs = null;
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            var function = tools[0].GetProperty("function");
            toolCall = function.GetProperty("name").GetString();
            toolArgs = function.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
        }

        var usage = doc.RootElement.GetProperty("usage");
        return new ModelCall(new CachedReply(answer, toolCall, toolArgs), status, true,
            usage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
    }
    catch
    {
        // мережа/gateway недоступні або відповідь не розпарсилась. без usage вартість невідома — NULL, а не 0.
        // TODO(student, W4): тут краще graceful degradation
        // інструмент до вибачення не виконуємо: відповіді моделі фактично немає (tool call = null)
        return new ModelCall(new CachedReply("Сервіс тимчасово недоступний.", null, null), status, false, 0, 0);
    }
}

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // routing (W2): модель обираємо за задачею і бюджетом (див. Route(), OverBudget())
    var model = Route(body.Message, defaultModel, await OverBudget());

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
    var messages = new[] { new ChatMessage("system", systemPrompt), new ChatMessage("user", body.Message) };

    int promptTokens = 0, completionTokens = 0, status;
    decimal? costUsd;

    // cache (W3): повтор того самого запиту (модель + промпт + повідомлення) після відповіді в модель не ходить —
    // його віддає кеш. однакові запити, що прийшли одночасно, теж не множать викликів: модель викликає лише
    // перший, решта чекають його результат (single-flight, див. ResponseCache.GetOrCallAsync).
    // hit і очікування пишемо в requests з 0 токенів — власного виклику моделі в них не було.
    var cacheKey = ResponseCache.Key(model, promptVersion, messages);
    var lookup = await responseCache.GetOrCallAsync(cacheKey, () => CallModel(httpFactory, model, messages));
    app.Logger.LogInformation("cache {Result} for request {RequestId}: hits={Hits} misses={Misses}",
        lookup.Source switch
        {
            CacheSource.Hit => "hit",
            CacheSource.Joined => "hit (waited for an identical in-flight request)",
            CacheSource.JoinedFailed => "miss (waited for an identical in-flight request that failed)",
            _ => "miss"
        }, requestId, responseCache.Hits, responseCache.Misses);

    var (answer, toolCall, toolArgs) = (lookup.Reply.Content, lookup.Reply.ToolName, lookup.Reply.ToolArguments);
    if (lookup.Source == CacheSource.Miss && lookup.Call is { } call)
    {
        // токени й вартість пише лише той запит, що сам викликав модель
        status = call.Status;
        (promptTokens, completionTokens) = (call.PromptTokens, call.CompletionTokens);
        // cost (W2): tokens * ціна моделі (див. CostUsd()); NULL, якщо ціни чи usage немає
        costUsd = call.UsageParsed ? CostUsd(model, promptTokens, completionTokens) : null;
    }
    else
    {
        // hit або очікування вдалого виклику (Joined): 200 і cost_usd = 0.
        // очікування невдалого виклику (JoinedFailed, у лічильниках це miss): статус лідера і cost_usd = NULL —
        // щоб збій у W5 не порахувався як hit. правило збою одне — ResponseCache.Succeeded().
        status = lookup.Call?.Status ?? 200;
        costUsd = lookup.Source == CacheSource.JoinedFailed ? null : 0m;
    }

    // tools + HITL (W3/W4): lookup_order виконуємо (з timeout і валідацією номера, див. Tools.cs) і
    // дописуємо результат до відповіді. виконується на кожному запиті — і на hit, і на очікуванні:
    // single-flight об'єднує лише виклики моделі, не інструменти.
    // create_ticket — незворотна дія: перед нею спитати людину.
    // TODO(student, W4)
    if (toolCall != null)
    {
        var tool = await toolRunner.RunAsync(toolCall, toolArgs, body.Message);
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
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
app.MapGet("/observability", () => Results.Json(new { todo = "aggregate from requests table" }));  // W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events }
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
    var by = await ReadActivatedBy(req);
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
app.MapGet("/providers", () => Results.Json(new { todo = "provider health" }));                    // W5: { providers: [ { name, status } ] }
app.MapGet("/approvals", () => Results.Json(new { todo = "pending HITL approvals" }));              // W4: { pending: [ { id, action } ] }

app.Logger.LogInformation("MODEL={Model}, fallback chain: {Chain}", defaultModel, FallbackChain);
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

// дістає activated_by з необовʼязкового JSON-тіла. порожнє тіло, {}, не-JSON, не той
// Content-Type чи не рядок — це "імені немає" (""), а не 400: кнопка без імені шле {}.
static async Task<string> ReadActivatedBy(HttpRequest req)
{
    try
    {
        using var sr = new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true);  // потік тіла належить Kestrel
        var raw = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("activated_by", out var v)
            && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";
    }
    catch (JsonException)
    {
        return "";
    }
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
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
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
        cmd.Parameters.AddWithValue("pv", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
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
