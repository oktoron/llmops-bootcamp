// observability (W5): агрегати для GET /observability і GET /providers, а також перевірка «сміттєвої» відповіді моделі.
// Окремий файл, а не локальні функції Program.cs: інакше юніт-тест не побачить ні правило сміття,
// ні відображення стану запобіжника на статус провайдера, ні частку hit кешу.

using Npgsql;

// зведення за сьогодні з таблиці requests. «сьогодні» — created_at::date = CURRENT_DATE, як у перевірці ментора й у /cost.
// таксономія помилок — з наявних колонок, без зміни схеми (жодної нової колонки чи таблиці під HW5):
// 429 — ліміт провайдера, 5xx — збій провайдера/gateway (і 503 «усі запобіжники відкриті»), 0 — відповіді не було
// (timeout спроби чи мережа), інший 4xx (не 429) — помилка запиту або конфігурації, яку fallback не рятує (напр. 400
// від закріпленого LiteLLM на хибний аліас моделі; відмова через вміст запиту теж тут, хоч у серію 4xx для /providers
// вона не йде), model = 'guardrail' — заблоковано guardrails (status у таких рядків '200').
// 429 + 5xx + 0 + client_4xx разом покривають кожен рядок зі status <> '200', що входить в error_rate_pct,
// крім екзотичних кодів (3xx, 2xx окрім 200): AttemptAsync зберігає й їх, але жоден кошик їх не рахує.
public sealed record DaySummary(long Requests, decimal ErrorRatePct, long P95Ms,
    long RateLimited429, long Provider5xx, long Timeout, long GuardrailBlocked, long Client4xx);

public static class Observability
{
    // вирази — дослівно ті, що в перевірці ментора, плюс захист від порожнього дня:
    // NULLIF прибирає ділення на нуль (0 рядків — NULL, а не помилка), а NULL ми віддаємо як 0.
    // percentile_cont над нулем рядків — теж NULL, тож p95 на порожньому дні — 0.
    // один запит — один знімок: requests, error rate і p95 рахуються з тих самих рядків.
    public const string TodaySql =
        "SELECT count(*), "
        + "round(100.0 * count(*) FILTER (WHERE status <> '200') / NULLIF(count(*), 0), 1), "
        + "round(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms)), "
        + "count(*) FILTER (WHERE status = '429'), "
        + "count(*) FILTER (WHERE status LIKE '5%'), "
        + "count(*) FILTER (WHERE status = '0'), "
        + "count(*) FILTER (WHERE model = 'guardrail'), "
        + "count(*) FILTER (WHERE status LIKE '4%' AND status <> '429') "
        + "FROM requests WHERE created_at::date = CURRENT_DATE";

    // помилку БД не ковтаємо — /observability її ловить і віддає поля з пам'яті
    public static async Task<DaySummary> TodayAsync(string conn)
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(TodaySql, db);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();  // агрегат без GROUP BY завжди дає рівно один рядок
        return new DaySummary(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? 0m : reader.GetDecimal(1),
            // round(double precision) — теж double, але значення вже ціле: віддаємо число без дробу
            reader.IsDBNull(2) ? 0 : (long)reader.GetDouble(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    // частка hit кешу у відсотках, з одним знаком після коми. звернень ще не було — 0, а не ділення на нуль.
    // що рахується (див. ResponseCache.GetOrCallAsync): hit — відповідь з кешу і Joined (дочекався вдалого
    // однакового виклику в дорозі); miss — власний виклик моделі (зокрема деградація) і JoinedFailed
    // (дочекався невдалого). запити, заблоковані guardrails, і 503 реєстру промптів кешу не торкаються —
    // тож знаменник тут менший за requests у таблиці. лічильники в пам'яті: перезапуск сервісу їх обнуляє.
    public static double HitPct(long hits, long misses)
    {
        var total = hits + misses;
        return total <= 0 ? 0 : Math.Round(100.0 * hits / total, 1);
    }

    // статус одного запобіжника — з circuit breaker моделі, без активного пінгу:
    // /health mock-провайдера лишається ok і під час __fail_503, тож пінг показував би «ok» посеред інциденту.
    // closed — ok; half_open (проба в дорозі) — degraded; open — down.
    public static string ProviderStatus(CircuitState state) => state switch
    {
        CircuitState.Open => "down",
        CircuitState.HalfOpen => "degraded",
        _ => "ok"
    };

    // який бекенд стоїть за кожним аліасом ланцюжка. джерело — gateway/litellm-config.yaml: усі три mock-аліаси
    // ведуть на той самий openai/mock (mock-provider). тож збій одного аліасу — свідчення і про решту на тому ж бекенді.
    // новий аліас на іншому провайдері (напр. gemini) — новий рядок тут з іншим бекендом.
    // аліаса тут немає (реальна модель з MODEL) — він сам собі бекенд.
    public static readonly IReadOnlyDictionary<string, string> AliasBackends = new Dictionary<string, string>
    {
        [ModelClient.MiniModel] = "mock-provider",
        [ModelClient.MidModel] = "mock-provider",
        [ModelClient.StrongModel] = "mock-provider",
    };

    public static string BackendOf(string model) => AliasBackends.TryGetValue(model, out var backend) ? backend : model;

    // статуси для GET /providers, по запису на аліас: відкритий / у пробі запобіжник — down / degraded (ProviderStatus).
    // закритий, але бекенд нездоровий (інший його аліас не закритий або аліас має серію 4xx,
    // ModelClient.HasClientErrorStreak) — degraded; ok — лише коли весь бекенд закритий і без серій.
    public static ProviderRow[] ProviderStatuses(IEnumerable<string> models, Func<string, CircuitState> stateOf,
        Func<string, bool>? clientErrorStreak = null)
    {
        // один знімок: кожен запобіжник і кожну серію читаємо рівно раз
        var states = models.Select(m => (Model: m, State: stateOf(m), Streak: clientErrorStreak?.Invoke(m) ?? false))
            .ToArray();
        var unhealthyBackends = states.Where(s => s.State != CircuitState.Closed || s.Streak)
            .Select(s => BackendOf(s.Model)).ToHashSet();
        return states.Select(s => new ProviderRow(s.Model,
            s.State == CircuitState.Closed && unhealthyBackends.Contains(BackendOf(s.Model))
                ? "degraded"
                : ProviderStatus(s.State))).ToArray();
    }

    // тіло GET /providers. форма — у коментарі біля ендпоінта в Program.cs; тут — щоб тест закріпив імена полів
    public static object ProvidersResponse(IEnumerable<string> models, Func<string, CircuitState> stateOf,
        Func<string, bool>? clientErrorStreak = null) =>
        new { providers = ProviderStatuses(models, stateOf, clientErrorStreak) };

    // тіло GET /observability. day == null — база недоступна: поля з requests — null, решта (з пам'яті) є.
    // імена полів читає консоль (p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events) — їх закріплює тест
    public static object ObservabilityResponse(DaySummary? day, double cacheHitPct, long fallbackEvents, long toolTimeouts,
        long garbageReplies, IReadOnlyDictionary<string, string> circuits, long approvalsExpired) => new
    {
        p95_ms = day?.P95Ms,
        requests = day?.Requests,
        cache_hit_pct = cacheHitPct,
        error_rate_pct = day?.ErrorRatePct,
        fallback_events = fallbackEvents,
        errors = new
        {
            rate_limited_429 = day?.RateLimited429,
            provider_5xx = day?.Provider5xx,
            timeout = day?.Timeout,
            client_4xx = day?.Client4xx,
            guardrail_blocked = day?.GuardrailBlocked,
            tool_timeout = toolTimeouts,
            garbage_reply = garbageReplies
        },
        circuits,
        approvals_expired = approvalsExpired
    };
}

// рядок GET /providers. веб-серіалізатор віддає name і status (camelCase від Name і Status)
public sealed record ProviderRow(string Name, string Status);

// «сміттєва» відповідь моделі: 200 з usage, але без змісту — напр. "..." (маркер __garbage у mock).
// правило — в IsGarbage нижче; користувач отримує GarbageReply, статус рядка лишається '200', токени й вартість — як були.
// перевірка одна на два місця: /chat після відповіді моделі і ResponseCache.IsCacheable (сміття не кешуємо).
// у ModelClient її свідомо немає: це не збій провайдера, тож ні fallback, ні circuit breaker вона не чіпає.
public static class ReplyQuality
{
    // чесна відповідь замість сміття. свідомо без фрагментів DegradedReply («технічні труднощі»):
    // це не збій сервісу, а відповідь без змісту
    public const string GarbageReply =
        "Не вдалося сформувати змістовну відповідь на ваше запитання. Спробуйте поставити його інакше.";

    // сміття — у тексті немає жодної літери чи цифри (будь-якої абетки): "...", "", " ", "?!".
    // коротка справжня відповідь («Так», «Ні», «OK», «42») сміттям не є — довжину свідомо не перевіряємо.
    public static bool IsGarbage(string? content) => content == null || !content.Any(char.IsLetterOrDigit);

    // чи підмінювати відповідь у /chat. з tool call порожній текст — норма (реальна модель часто шле content = null,
    // а відповідь складає інструмент), тож сміттям вважаємо лише відповідь без tool call
    public static bool NeedsHonestReply(string? toolCall, string? content) => toolCall == null && IsGarbage(content);
}

// лічильник підмінених сміттєвих відповідей для errors.garbage_reply (W5) — єдине місце, де сміття видно:
// статус '200', тож error rate, /providers і SLO його не бачать. у пам'яті, як tool_timeout; рахується кожна
// підміна в /chat, зокрема в чекача single-flight (Joined), що отримав сміття лідера.
public sealed class GarbageReplyCounter
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    // рішення і підрахунок разом: /chat не може підмінити відповідь і забути порахувати.
    // true — відповідь сміттєва (див. ReplyQuality.NeedsHonestReply), reply — чесний текст, лічильник +1;
    // false — reply = content без змін, лічильник стоїть
    public bool TryReplace(string? toolCall, string content, out string reply)
    {
        if (!ReplyQuality.NeedsHonestReply(toolCall, content))
        {
            reply = content;
            return false;
        }
        Interlocked.Increment(ref _count);
        reply = ReplyQuality.GarbageReply;
        return true;
    }
}
