// юніт-тести рядка requests: RequestRow.ServedModel (модель, що відповіла) і RequestRow.Compose (правило NULL/0 для токенів і вартості).
// 0/0/0 — лише кеш (hit, очікування вдалого виклику); NULL — відповіді з usage не було
// (деградація, очікування невдалого виклику). без HTTP: ModelCall і CacheLookup будуємо напряму.
// Запуск — у SDK-контейнері (див. CacheAndToolsTests.cs).

public class RequestRowTests
{
    private static readonly CachedReply Ok = new("Вітаю", null, null);
    private static readonly CachedReply Degraded = new(ModelClient.DegradedReply, null, null);

    // делегат ціни, що записує аргументи кожного виклику
    private sealed class CostStub
    {
        private readonly decimal? _result;
        public List<(string Model, int Prompt, int Completion)> Calls { get; } = new();
        public CostStub(decimal? result) => _result = result;
        public decimal? Invoke(string model, int prompt, int completion)
        {
            Calls.Add((model, prompt, completion));
            return _result;
        }
    }

    [Fact]
    public void Hit_200_ZeroTokens_ZeroCost()
    {
        var cost = new CostStub(1.23m);

        var row = RequestRow.Compose(new CacheLookup(CacheSource.Hit, Ok, null), "mock-mini", cost.Invoke);

        Assert.Equal(200, row.Status);
        Assert.Equal(0, row.PromptTokens);
        Assert.Equal(0, row.CompletionTokens);
        Assert.Equal(0m, row.CostUsd);
        Assert.Empty(cost.Calls);
    }

    // очікування вдалого виклику лідера: власного виклику не було — 0/0/0, статус лідера
    [Fact]
    public void JoinedSuccess_LeaderStatus_ZeroTokens_DelegateNotCalled()
    {
        var cost = new CostStub(1.23m);
        var leader = new ModelCall(Ok, 200, UsageParsed: true, 10, 5, ServedModel: "mock-mini");

        var row = RequestRow.Compose(new CacheLookup(CacheSource.Joined, Ok, leader), "mock-mini", cost.Invoke);

        Assert.Equal(leader.Status, row.Status);
        Assert.Equal(0, row.PromptTokens);
        Assert.Equal(0, row.CompletionTokens);
        Assert.Equal(0m, row.CostUsd);
        Assert.Empty(cost.Calls);
    }

    // очікування невдалого виклику: статус лідера, NULL/NULL/NULL — не має виглядати як hit
    [Fact]
    public void JoinedFailed_LeaderStatus_NullTokens_NullCost()
    {
        var cost = new CostStub(1.23m);
        var leader = new ModelCall(Degraded, 503, UsageParsed: false, 0, 0, ServedModel: null, Hops: 1);

        var row = RequestRow.Compose(new CacheLookup(CacheSource.JoinedFailed, Degraded, leader), "mock-mini", cost.Invoke);

        Assert.Equal(503, row.Status);
        Assert.Null(row.PromptTokens);
        Assert.Null(row.CompletionTokens);
        Assert.Null(row.CostUsd);
        Assert.Empty(cost.Calls);
    }

    // деградація (ланцюжок вичерпано / gateway недоступний): токени 0 у ModelCall, але в рядку — NULL.
    // без перевірки UsageParsed тут вийшло б 0/0 — рядок виглядав би як hit
    [Theory]
    [InlineData(503)]
    [InlineData(0)]
    public void DegradedMiss_NullTokens_NullCost_DelegateNotCalled(int code)
    {
        var cost = new CostStub(0m);
        var call = new ModelCall(Degraded, code, UsageParsed: false, 0, 0, ServedModel: null, Hops: 1);

        var row = RequestRow.Compose(new CacheLookup(CacheSource.Miss, Degraded, call), "mock-mini", cost.Invoke);

        Assert.Equal(code, row.Status);
        Assert.Null(row.PromptTokens);
        Assert.Null(row.CompletionTokens);
        Assert.Null(row.CostUsd);
        Assert.Empty(cost.Calls);
    }

    // звичайний miss після fallback: токени й статус виклику; Compose рахує ціну за переданим servedModel.
    // сам вибір servedModel (модель, що відповіла, а не обрана) перевіряють тести ServedModel_* нижче
    [Fact]
    public void NormalMiss_CallTokens_CostFromServedModel()
    {
        var cost = new CostStub(0.042m);
        var call = new ModelCall(Ok, 200, UsageParsed: true, 12, 7, ServedModel: "mock-mid", Hops: 1);
        var lookup = new CacheLookup(CacheSource.Miss, Ok, call);

        var row = RequestRow.Compose(lookup, RequestRow.ServedModel(lookup, "mock-strong"), cost.Invoke);

        Assert.Equal(200, row.Status);
        Assert.Equal(12, row.PromptTokens);
        Assert.Equal(7, row.CompletionTokens);
        Assert.Equal(0.042m, row.CostUsd);
        Assert.Equal(new[] { ("mock-mid", 12, 7) }, cost.Calls);
    }

    // реальна модель без ціни: токени лишаються, cost_usd = NULL
    [Fact]
    public void NormalMiss_NoPrice_TokensKept_NullCost()
    {
        var cost = new CostStub(null);
        var call = new ModelCall(Ok, 200, UsageParsed: true, 30, 9, ServedModel: "gpt-5-mini");

        var row = RequestRow.Compose(new CacheLookup(CacheSource.Miss, Ok, call), "gpt-5-mini", cost.Invoke);

        Assert.Equal(200, row.Status);
        Assert.Equal(30, row.PromptTokens);
        Assert.Equal(9, row.CompletionTokens);
        Assert.Null(row.CostUsd);
        Assert.Equal(new[] { ("gpt-5-mini", 30, 9) }, cost.Calls);
    }

    // RequestRow.ServedModel: модель для рядка й ціни. обрана Route() — mock-strong, щоб відрізнялася від тієї, що відповіла

    // miss після fallback: відповіла mock-mid, а не обрана mock-strong
    [Fact]
    public void ServedModel_MissAfterFallback_IsModelThatAnswered()
    {
        var call = new ModelCall(Ok, 200, UsageParsed: true, 12, 7, ServedModel: "mock-mid", Hops: 1);

        Assert.Equal("mock-mid", RequestRow.ServedModel(new CacheLookup(CacheSource.Miss, Ok, call), "mock-strong"));
    }

    // hit: власного виклику не було — обрана
    [Fact]
    public void ServedModel_Hit_IsRouted()
    {
        Assert.Equal("mock-strong", RequestRow.ServedModel(new CacheLookup(CacheSource.Hit, Ok, null), "mock-strong"));
    }

    // деградація: не відповіла жодна — обрана
    [Fact]
    public void ServedModel_DegradedMiss_IsRouted()
    {
        var call = new ModelCall(Degraded, 503, UsageParsed: false, 0, 0, ServedModel: null, Hops: 1);

        Assert.Equal("mock-strong", RequestRow.ServedModel(new CacheLookup(CacheSource.Miss, Degraded, call), "mock-strong"));
    }

    // очікування вдалого виклику: модель, що відповіла лідеру
    [Fact]
    public void ServedModel_JoinedSuccess_IsLeadersServed()
    {
        var leader = new ModelCall(Ok, 200, UsageParsed: true, 10, 5, ServedModel: "mock-mid", Hops: 1);

        Assert.Equal("mock-mid", RequestRow.ServedModel(new CacheLookup(CacheSource.Joined, Ok, leader), "mock-strong"));
    }

    // очікування невдалого виклику: лідеру не відповіла жодна — обрана
    [Fact]
    public void ServedModel_JoinedFailed_IsRouted()
    {
        var leader = new ModelCall(Degraded, 503, UsageParsed: false, 0, 0, ServedModel: null, Hops: 1);

        Assert.Equal("mock-strong", RequestRow.ServedModel(new CacheLookup(CacheSource.JoinedFailed, Degraded, leader), "mock-strong"));
    }
}
