// метрики (HW6, опційно): текстовий формат Prometheus для GET /metrics. читає їх Prometheus із профілю advanced, не консоль.
// пишемо текст вручну, без NuGet-клієнта: ті самі значення вже є в /observability і /cost, а бібліотека завела б
// другий набір лічильників поруч із наявними. форматер чистий, тож юніт-тест бачить його без сервера і бази.
// значення — ті самі, що в /observability і /cost: тут немає власного SQL.

using System.Globalization;
using System.Text;

// Day і CostTodayUsd == null — база недоступна.
// лічильники в пам'яті з моменту старту: перезапуск сервісу їх обнуляє, тож на дашборді — лише increase()/rate()
public sealed record MetricsSnapshot(long FallbackEvents, long CacheHits, long CacheMisses, long GarbageReplies,
    long ToolTimeouts, long ApprovalsExpired, decimal BudgetUsd, IReadOnlyDictionary<string, string> Circuits,
    DaySummary? Day, decimal? CostTodayUsd);

public static class Metrics
{
    // версію вказуємо явно: без неї тіло читалося б за найновішою версією текстового формату, а не за 0.0.4
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Format(MetricsSnapshot s)
    {
        var text = new StringBuilder();

        Counter(text, "llmops_fallback_events_total", "Переходи на наступну модель ланцюжка", s.FallbackEvents);
        Counter(text, "llmops_cache_hits_total", "Відповіді з кешу", s.CacheHits);
        Counter(text, "llmops_cache_misses_total", "Промахи кешу: власний виклик моделі або очікування однакового запиту, що завершилось збоєм", s.CacheMisses);
        Counter(text, "llmops_garbage_replies_total", "Підмінені сміттєві відповіді", s.GarbageReplies);
        Counter(text, "llmops_tool_timeouts_total", "Виклики інструмента, що вийшли за бюджет часу", s.ToolTimeouts);
        Counter(text, "llmops_approvals_expired_total", "Заявки HITL, що протермінувались", s.ApprovalsExpired);

        Gauge(text, "llmops_budget_usd", "Денний бюджет, USD", Number(s.BudgetUsd));
        Header(text, "llmops_circuit_state", "gauge", "Стан запобіжника: 0 closed, 1 half_open, 2 open");
        foreach (var (model, state) in s.Circuits)
            Sample(text, "llmops_circuit_state", Number(CircuitGauge(state)), ("model", model));

        // часткової картини не віддаємо: 0 тут читався б як здоровий error rate, а відсутня серія видна як відсутня
        var dbUp = s.Day is not null && s.CostTodayUsd is not null;
        Gauge(text, "llmops_db_up", "1 якщо зведення за сьогодні прочитано з бази", dbUp ? "1" : "0");
        if (!dbUp)
            return text.ToString();

        // «сьогодні» опівночі падає, тож це gauge, а не counter: rate() прочитав би падіння як перезапуск
        var day = s.Day!;
        Gauge(text, "llmops_requests_today", "Запити за сьогодні", Number(day.Requests));
        Gauge(text, "llmops_error_rate_pct_today", "Частка помилок за сьогодні, %", Number(day.ErrorRatePct));
        Gauge(text, "llmops_latency_p95_ms_today", "p95 затримки за сьогодні, мс", Number(day.P95Ms));
        Gauge(text, "llmops_cost_today_usd", "Витрати за сьогодні, USD", Number(s.CostTodayUsd!.Value));
        // kind — ті самі імена, що в errors /observability
        Header(text, "llmops_errors_today", "gauge", "Помилки за сьогодні за типом");
        Sample(text, "llmops_errors_today", Number(day.RateLimited429), ("kind", "rate_limited_429"));
        Sample(text, "llmops_errors_today", Number(day.Provider5xx), ("kind", "provider_5xx"));
        Sample(text, "llmops_errors_today", Number(day.Timeout), ("kind", "timeout"));
        Sample(text, "llmops_errors_today", Number(day.Client4xx), ("kind", "client_4xx"));
        Sample(text, "llmops_errors_today", Number(day.GuardrailBlocked), ("kind", "guardrail_blocked"));

        return text.ToString();
    }

    // рядки стану — ті самі, що в circuits /observability (CircuitRegistry.Snapshot)
    public static int CircuitGauge(string state) => state switch
    {
        "open" => 2,
        "half_open" => 1,
        _ => 0
    };

    // у значенні мітки формат екранує рівно три символи: \, " і новий рядок
    public static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Counter(StringBuilder text, string name, string help, long value)
    {
        Header(text, name, "counter", help);
        Sample(text, name, Number(value));
    }

    private static void Gauge(StringBuilder text, string name, string help, string value)
    {
        Header(text, name, "gauge", help);
        Sample(text, name, value);
    }

    private static void Header(StringBuilder text, string name, string type, string help) =>
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
            .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    // \n, а не AppendLine: формат вимагає саме його, а AppendLine залежить від платформи
    private static void Sample(StringBuilder text, string name, string value, (string Name, string Value)? label = null)
    {
        text.Append(name);
        if (label is { } l)
            text.Append('{').Append(l.Name).Append("=\"").Append(EscapeLabel(l.Value)).Append("\"}");
        text.Append(' ').Append(value).Append('\n');
    }
}
