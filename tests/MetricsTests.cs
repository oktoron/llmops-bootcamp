// юніт-тести HW6 (опційно): текстовий формат /metrics — рядки # TYPE, екранування міток, числа незалежно від культури,
// відсутність серій із бази, коли база недоступна. запит до бази й живий скрейп Prometheus тут не перевіряються.
// Запуск — у SDK-контейнері (див. CacheAndToolsTests.cs).

using System.Globalization;

public class MetricsTests
{
    private static readonly DaySummary Day = new(40, 12.5m, 123, 1, 2, 3, 4, 5);

    private static MetricsSnapshot Snapshot(DaySummary? day = null, decimal? cost = 0.0125m,
        IReadOnlyDictionary<string, string>? circuits = null) =>
        new(6, 7, 8, 9, 10, 11, 2.5m,
            circuits ?? new Dictionary<string, string> { ["mock-mini"] = "closed", ["mock-mid"] = "half_open", ["mock-strong"] = "open" },
            day, cost);

    private static string[] Lines(string text) => text.Split('\n');

    // лічильники і «сьогодні» розведені: значення, що опівночі падає, не можна типізувати як counter
    [Theory]
    [InlineData("llmops_fallback_events_total", "counter")]
    [InlineData("llmops_cache_hits_total", "counter")]
    [InlineData("llmops_cache_misses_total", "counter")]
    [InlineData("llmops_garbage_replies_total", "counter")]
    [InlineData("llmops_tool_timeouts_total", "counter")]
    [InlineData("llmops_approvals_expired_total", "counter")]
    [InlineData("llmops_budget_usd", "gauge")]
    [InlineData("llmops_circuit_state", "gauge")]
    [InlineData("llmops_db_up", "gauge")]
    [InlineData("llmops_requests_today", "gauge")]
    [InlineData("llmops_error_rate_pct_today", "gauge")]
    [InlineData("llmops_latency_p95_ms_today", "gauge")]
    [InlineData("llmops_cost_today_usd", "gauge")]
    [InlineData("llmops_errors_today", "gauge")]
    public void Format_DeclaresEachMetricOnce(string name, string type)
    {
        var lines = Lines(Metrics.Format(Snapshot(Day)));
        Assert.Single(lines, l => l == $"# TYPE {name} {type}");
        Assert.Single(lines, l => l.StartsWith($"# HELP {name} ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("llmops_fallback_events_total 6")]
    [InlineData("llmops_cache_hits_total 7")]
    [InlineData("llmops_cache_misses_total 8")]
    [InlineData("llmops_garbage_replies_total 9")]
    [InlineData("llmops_tool_timeouts_total 10")]
    [InlineData("llmops_approvals_expired_total 11")]
    [InlineData("llmops_budget_usd 2.5")]
    [InlineData("llmops_db_up 1")]
    [InlineData("llmops_requests_today 40")]
    [InlineData("llmops_error_rate_pct_today 12.5")]
    [InlineData("llmops_latency_p95_ms_today 123")]
    [InlineData("llmops_cost_today_usd 0.0125")]
    [InlineData("llmops_errors_today{kind=\"rate_limited_429\"} 1")]
    [InlineData("llmops_errors_today{kind=\"provider_5xx\"} 2")]
    [InlineData("llmops_errors_today{kind=\"timeout\"} 3")]
    [InlineData("llmops_errors_today{kind=\"client_4xx\"} 5")]
    [InlineData("llmops_errors_today{kind=\"guardrail_blocked\"} 4")]
    [InlineData("llmops_circuit_state{model=\"mock-mini\"} 0")]
    [InlineData("llmops_circuit_state{model=\"mock-mid\"} 1")]
    [InlineData("llmops_circuit_state{model=\"mock-strong\"} 2")]
    public void Format_MapsEachValue(string sample) => Assert.Contains(sample, Lines(Metrics.Format(Snapshot(Day))));

    // формат вимагає \n, зокрема після останнього рядка
    [Fact]
    public void Format_UsesLfAndEndsWithNewline()
    {
        var text = Metrics.Format(Snapshot(Day));
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("\n", text);
    }

    // база лежить: серій із бази немає, а не нулі — 0 читався б як здоровий error rate
    [Theory]
    [InlineData("llmops_requests_today")]
    [InlineData("llmops_error_rate_pct_today")]
    [InlineData("llmops_latency_p95_ms_today")]
    [InlineData("llmops_cost_today_usd")]
    [InlineData("llmops_errors_today")]
    public void Format_DbDown_OmitsDbSeries(string name) =>
        Assert.DoesNotContain(name, Metrics.Format(Snapshot(day: null, cost: null)));

    [Fact]
    public void Format_DbDown_KeepsInMemorySeries()
    {
        var lines = Lines(Metrics.Format(Snapshot(day: null, cost: null)));
        Assert.Contains("llmops_db_up 0", lines);
        Assert.Contains("llmops_fallback_events_total 6", lines);
        Assert.Contains("llmops_budget_usd 2.5", lines);
        Assert.Contains("llmops_circuit_state{model=\"mock-strong\"} 2", lines);
    }

    // другий запит упав: половина зведення без вартості теж не віддається
    [Fact]
    public void Format_CostUnavailable_TreatedAsDbDown()
    {
        var text = Metrics.Format(Snapshot(Day, cost: null));
        Assert.Contains("llmops_db_up 0", Lines(text));
        Assert.DoesNotContain("llmops_requests_today", text);
    }

    [Theory]
    [InlineData("closed", 0)]
    [InlineData("half_open", 1)]
    [InlineData("open", 2)]
    public void CircuitGauge_MapsState(string state, int expected) => Assert.Equal(expected, Metrics.CircuitGauge(state));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("\\\"\n", "\\\\\\\"\\n")]
    public void EscapeLabel_EscapesThreeCharacters(string value, string expected) =>
        Assert.Equal(expected, Metrics.EscapeLabel(value));

    // реальна модель з MODEL може мати будь-яке ім'я: мітка не має ламати весь скрейп
    [Fact]
    public void Format_EscapesModelLabel()
    {
        var circuits = new Dictionary<string, string> { ["we\"ird\\m"] = "open" };
        Assert.Contains("llmops_circuit_state{model=\"we\\\"ird\\\\m\"} 2", Lines(Metrics.Format(Snapshot(Day, circuits: circuits))));
    }

    // uk-UA пише кому в дробах, а для Prometheus «12,5» — зламаний рядок і втрачений скрейп
    [Fact]
    public void Format_NumbersIgnoreCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
        try
        {
            var lines = Lines(Metrics.Format(Snapshot(Day)));
            Assert.Contains("llmops_error_rate_pct_today 12.5", lines);
            Assert.Contains("llmops_cost_today_usd 0.0125", lines);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
