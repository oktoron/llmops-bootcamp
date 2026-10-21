// виклик моделі через gateway з fallback (W4). Окремий клас, а не локальна функція Program.cs:
// інакше юніт-тест не підставить свій HttpMessageHandler і не перевірить 503 -> 200 (на mock маркер
// __fail_503 валить кожен крок ланцюжка, тож успіх після fallback живцем не побачити).
// ОБМЕЖЕННЯ: лічильник перемикань і стан circuit breaker (див. CircuitBreaker.cs) живуть у пам'яті процесу —
// перезапуск сервісу обнуляє лічильник і закриває всі запобіжники.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public sealed class ModelClient
{
    // моделі названі за роллю, а не за провайдером — так само, як у gateway/litellm-config.yaml
    public const string MiniModel = "mock-mini";
    public const string MidModel = "mock-mid";
    public const string StrongModel = "mock-strong";

    // graceful degradation: одна стала відповідь, коли ланцюжок вичерпано. HTTP лишається 200.
    // свідомо без цифр і без жодного ключового слова evals (тікет, статус, email, допомог, не можу, cannot, refuse):
    // під час повної відмови жоден eval-кейс не має пройти випадково (HW5). «вибачте» безпечне — його
    // навмисно немає в списку відмов.
    public const string DegradedReply =
        "Вибачте, зараз у нас тимчасові технічні труднощі. Спробуйте, будь ласка, ще раз за кілька хвилин.";

    // бюджет часу на одну спробу. значно більший за __delay mock-провайдера (2 с), щоб повільна,
    // але жива модель не ставала fallback. timeout — це «відповіді не було» (status 0), тобто перемикання.
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _gateway;
    private readonly TimeSpan _attemptTimeout;
    private readonly ILogger? _log;
    private readonly CircuitRegistry _circuits;
    private long _fallbackEvents;

    // knownModels — моделі, чиї запобіжники /observability показує ще до першого запиту.
    // time — годинник запобіжників (тест підставляє свій, щоб не чекати секунди проби)
    public ModelClient(IHttpClientFactory httpFactory, string gatewayUrl, TimeSpan? attemptTimeout = null, ILogger? log = null,
        CircuitOptions? circuit = null, TimeProvider? time = null, IEnumerable<string>? knownModels = null)
    {
        _httpFactory = httpFactory;
        _gateway = gatewayUrl;
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
        _log = log;
        _circuits = new CircuitRegistry(circuit ?? CircuitOptions.Default, time ?? TimeProvider.System, log, knownModels);
    }

    // стан запобіжників для /observability: { "mock-mini": "closed" | "open" | "half_open", ... }
    public SortedDictionary<string, string> CircuitStates() => _circuits.Snapshot();

    public CircuitState CircuitOf(string model) => _circuits.For(model).State;

    // усі моделі, що можуть трапитись у ланцюжках при цьому MODEL
    public static string[] ChainModels(string defaultModel) =>
        new[] { StrongModel, MidModel, MiniModel }
            .SelectMany(m => Chain(defaultModel == "mock" ? m : defaultModel, defaultModel))
            .Distinct().ToArray();

    // скільки разів запит перейшов на наступну модель ланцюжка (перемикання, а не запити):
    // повна відмова strong -> mid -> mini — це 2, mini -> mid — 1. Запити, що дочекались
    // однакового виклику в дорозі (single-flight), нічого не додають: ланцюжок пройшов один раз.
    public long FallbackEvents => Interlocked.Read(ref _fallbackEvents);

    // fallback chain для обраної Route() моделі. Кожен tier падає на дешевший; mock-mini — на mock-mid,
    // бо інакше в нього не було б другого кроку, а саме туди йде дриль __fail_503 без маркерів маршруту.
    // ОБМЕЖЕННЯ: усі mock-аліаси живуть на одному провайдері, тож падіння провайдера цей ланцюжок не переживе.
    // Провайдер з іншим ключем (gemini — закоментований блок у gateway/litellm-config.yaml) став би другим кроком.
    // Реальна модель із MODEL — ланцюжок з одного елемента: запасної моделі її власник не задавав.
    // понад поріг бюджету (overBudget) ланцюжок ніколи не йде на дорожчу модель за обрану: вартість рахується
    // за ціною моделі, що відповіла, тож mini -> mid після збою коштував би 4× і ламав би budget-політику.
    // дорожчає лише крок mock-mini -> mock-mid, тому понад бюджет mock-mini — це mock-mini -> DegradedReply.
    public static string[] Chain(string routedModel, string defaultModel, bool overBudget = false)
    {
        if (defaultModel != "mock")
            return new[] { routedModel };
        return routedModel switch
        {
            StrongModel => new[] { StrongModel, MidModel, MiniModel },
            MidModel => new[] { MidModel, MiniModel },
            MiniModel => overBudget ? new[] { MiniModel } : new[] { MiniModel, MidModel },
            _ => new[] { routedModel }
        };
    }

    // для стартового логу: усі ланцюжки, що можуть трапитись при цьому MODEL (і ланцюжок mock-mini понад бюджет)
    public static string DescribeChains(string defaultModel) =>
        defaultModel != "mock"
            ? $"{defaultModel} -> degraded reply"
            : string.Join("; ", new[] { StrongModel, MidModel, MiniModel }
                .Select(m => string.Join(" -> ", Chain(m, defaultModel)) + " -> degraded reply"))
              + $"; over budget: {string.Join(" -> ", Chain(MiniModel, defaultModel, overBudget: true))} -> degraded reply";

    // чи варто йти на наступну модель після невдалої спроби з цим статусом:
    // 0 — відповіді не було (мережа, timeout), 429 — ліміт, 5xx — збій провайдера чи gateway,
    // 200 — сюди доходить лише 200, що не розпарсився (вдалий повертається раніше).
    // 400/401/404 та інші — помилка конфігурації (404 від LiteLLM — описка в аліасі): перемикання її сховало б.
    public static bool ShouldFallback(int status) => status is 0 or 200 or 429 || status >= 500;

    // один прохід ланцюжком — фабрика для single-flight у ResponseCache.GetOrCallAsync.
    // ніколи не кидає: вичерпаний ланцюжок чи статус без перемикання — це DegradedReply з UsageParsed = false
    // і status останньої спроби. Повторів тієї самої моделі немає (num_retries: 0 у gateway — теж свідомо):
    // лише перехід на іншу модель.
    // без RequestAborted свідомо: результат можуть чекати інші запити, і відключення одного клієнта
    // не має скасувати спільний виклик. timeout спроби — свій на кожну спробу, спільний виклик він не скасовує.
    // circuit breaker: перед кожною спробою питаємо запобіжник моделі. відкриту модель пропускаємо без виклику —
    // це такий самий перехід на наступну модель (hop, +1 до fallback_events), тож після пропуску відповідь
    // не кешується і рахується за ціною моделі, що відповіла. Проба — це сам запит, а не додатковий виклик.
    public async Task<ModelCall> CallAsync(IReadOnlyList<string> chain, ChatMessage[] messages)
    {
        var status = 0;  // 0 = відповіді не було
        var hops = 0;
        var skipped = 0;
        var attempted = false;
        var previousSkipped = false;
        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0)
            {
                hops = i;
                Interlocked.Increment(ref _fallbackEvents);
                if (previousSkipped)
                    _log?.LogWarning("fallback {From} -> {To} (circuit open)", chain[i - 1], chain[i]);
                else
                    _log?.LogWarning("fallback {From} -> {To} after {Status}", chain[i - 1], chain[i], status);
            }

            var breaker = _circuits.For(chain[i]);
            var decision = breaker.TryAcquire(out var generation);
            previousSkipped = decision == CircuitDecision.Skip;
            if (previousSkipped)
            {
                skipped++;
                continue;
            }

            attempted = true;
            ModelCall? call = null;
            // невідомий результат (виняток, що вилетів) — збій: слот проби звільняється в будь-якому разі
            var outcome = CircuitOutcome.Failure;
            try
            {
                (call, status) = await AttemptAsync(chain[i], messages);
                outcome = call != null ? CircuitOutcome.Success
                    : ShouldFallback(status) ? CircuitOutcome.Failure
                    : CircuitOutcome.Neutral;
            }
            finally
            {
                breaker.Record(outcome, decision == CircuitDecision.Probe, generation, status);
            }
            if (call != null)
                return call with { Hops = i };
            if (!ShouldFallback(status))
                break;
        }

        // усі моделі ланцюжка відкриті — у модель не ходили зовсім. 503 (провайдер недоступний), а не 0:
        // 0 у W5 читався б як мережа чи timeout, а тут відмовили ми самі
        if (!attempted)
            status = 503;

        // інструмент до вибачення не виконуємо: відповіді моделі фактично немає (tool call = null).
        // без usage вартість невідома — /chat пише NULL, а не 0.
        // "circuit open" лише коли щось справді пропустили: інакше grep за "circuit open" ловив би кожну деградацію
        if (skipped > 0)
            _log?.LogWarning("model call degraded after {Hops} fallback(s) ({Skipped} skipped, circuit open), last status {Status}",
                hops, skipped, status);
        else
            _log?.LogWarning("model call degraded after {Hops} fallback(s), last status {Status}", hops, status);
        return new ModelCall(new CachedReply(DegradedReply, null, null), status, false, 0, 0, null, hops);
    }

    // одна спроба на одну модель. Call == null — невдача, Status — що відповів gateway (0 — нічого).
    // статус перевіряємо ДО розбору: тіло помилки LiteLLM — інший JSON, і розбирати його як відповідь не можна.
    private async Task<(ModelCall? Call, int Status)> AttemptAsync(string model, ChatMessage[] messages)
    {
        var payload = JsonSerializer.Serialize(new { model, messages });
        var status = 0;
        try
        {
            using var cts = new CancellationTokenSource(_attemptTimeout);
            var http = _httpFactory.CreateClient();
            using var response = await http.PostAsync(
                $"{_gateway}/v1/chat/completions",
                new StringContent(payload, Encoding.UTF8, "application/json"), cts.Token);
            status = (int)response.StatusCode;
            if (status != 200)
                return (null, status);

            var rawJson = await response.Content.ReadAsStringAsync(cts.Token);
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
            return (new ModelCall(new CachedReply(answer, toolCall, toolArgs), status, true,
                usage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32(),
                model, 0), status);
        }
        catch (Exception ex)
        {
            // мережа/gateway недоступні, timeout спроби (status лишається 0) або 200 з тілом, що не розпарсилось
            _log?.LogWarning("model {Model} attempt failed with status {Status}: {Error}", model, status, ex.GetType().Name);
            return (null, status);
        }
    }
}
