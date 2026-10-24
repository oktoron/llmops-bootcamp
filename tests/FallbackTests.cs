// юніт-тести W4: ланцюжок fallback, graceful degradation, лічильник перемикань, без кешу після fallback.
// на mock маркер __fail_503 валить кожен крок ланцюжка, тож успіх після fallback (503 -> 200) живцем не побачити —
// його доводить скриптований HttpMessageHandler. Запуск — у SDK-контейнері (див. CacheAndToolsTests.cs).

using System.Net;
using System.Text;
using System.Text.Json;

public class FallbackTests
{
    // відповідь: статус + тіло, або виняток (немає відповіді), або затримка понад timeout спроби
    private sealed record Step(HttpStatusCode Status = HttpStatusCode.OK, string? Body = null,
        Exception? Throw = null, TimeSpan? Delay = null);

    // тіла відповідей — спільні (TestStubs.cs)
    private static Step Ok(string content) => new(HttpStatusCode.OK, GatewayStub.OkBody(content));
    private static Step Code(int code) => new((HttpStatusCode)code, GatewayStub.ErrorBody);

    // віддає відповіді з черги по черзі й записує, яку модель просив кожен запит
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Step> _steps;
        public List<string> Models { get; } = new();
        public ScriptedHandler(params Step[] steps) => _steps = new Queue<Step>(steps);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using (var doc = JsonDocument.Parse(body))
                Models.Add(doc.RootElement.GetProperty("model").GetString()!);

            var step = _steps.Dequeue();
            if (step.Delay is { } delay)
                await Task.Delay(delay, ct);
            if (step.Throw != null)
                throw step.Throw;
            return new HttpResponseMessage(step.Status)
            {
                Content = new StringContent(step.Body ?? "", Encoding.UTF8, "application/json")
            };
        }
    }

    // фабрика HttpClient — спільна (StubHttpClientFactory у TestStubs.cs). не Stubs.Client, бо тут свій ScriptedHandler
    // (відповідає по черзі, а не за моделлю), опції за замовчуванням і системний годинник; запобіжники є (поріг 3)
    private static ModelClient Client(ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(new StubHttpClientFactory(handler), "http://gateway:4000", timeout);

    private static string[] MockChain(string routed) => ModelClient.Chain(routed, "mock");

    [Fact]
    public async Task Fallback_503Then200_ServedByNextModel()
    {
        var handler = new ScriptedHandler(Code(503), Ok("Відповідь mock-mid"));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Equal("Відповідь mock-mid", call.Reply.Content);
        Assert.Equal(200, call.Status);
        Assert.True(call.UsageParsed);
        Assert.Equal("mock-mid", call.ServedModel);
        Assert.Equal(1, call.Hops);
        Assert.Equal(1, client.FallbackEvents);
        Assert.Equal(new[] { "mock-strong", "mock-mid" }, handler.Models);
    }

    [Fact]
    public async Task FirstModelOk_NoFallback()
    {
        var handler = new ScriptedHandler(Ok("Вітаю"));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Equal("mock-strong", call.ServedModel);
        Assert.Equal(0, call.Hops);
        Assert.Equal(0, client.FallbackEvents);
        Assert.Single(handler.Models);
    }

    [Fact]
    public async Task AllFail503_Degrades_CountsTwoSwitchesOnStrongChain()
    {
        var handler = new ScriptedHandler(Code(503), Code(503), Code(503));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
        Assert.Null(call.Reply.ToolName);
        Assert.Equal(503, call.Status);
        Assert.False(call.UsageParsed);
        Assert.Null(call.ServedModel);
        Assert.Equal(2, call.Hops);
        Assert.Equal(2, client.FallbackEvents);
        Assert.Equal(new[] { "mock-strong", "mock-mid", "mock-mini" }, handler.Models);
    }

    // drill ментора: __fail_503 без маркерів маршруту йде на mock-mini, ланцюжок mini -> mid — рівно +1
    [Fact]
    public async Task AllFail503_OnMini_CountsExactlyOne()
    {
        var handler = new ScriptedHandler(Code(503), Code(503));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-mini"), Stubs.Msgs());

        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
        Assert.Equal(1, client.FallbackEvents);
        Assert.Equal(new[] { "mock-mini", "mock-mid" }, handler.Models);
    }

    // лічильник накопичується між викликами одного клієнта
    [Fact]
    public async Task Counter_AccumulatesAcrossCalls()
    {
        var handler = new ScriptedHandler(Code(503), Code(503), Code(503), Code(503), Code(503));
        var client = Client(handler);

        await client.CallAsync(MockChain("mock-mini"), Stubs.Msgs());
        await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Equal(3, client.FallbackEvents);
    }

    // 4xx — помилка запиту або конфігурації, а не провайдера (описка в аліасі — LiteLLM віддає 400 Invalid model name):
    // перемикання її сховало б
    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(401)]
    public async Task ClientError_NoSwitch_Degrades(int code)
    {
        var handler = new ScriptedHandler(Code(code));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Single(handler.Models);
        Assert.Equal(0, client.FallbackEvents);
        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
        Assert.Equal(code, call.Status);
        Assert.False(call.UsageParsed);
        Assert.Equal(0, call.Hops);
    }

    // 4xx на другому кроці теж зупиняє ланцюжок: уже зроблене перемикання лишається порахованим
    [Fact]
    public async Task ClientErrorAfterSwitch_StopsChain()
    {
        var handler = new ScriptedHandler(Code(503), Code(404));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Equal(new[] { "mock-strong", "mock-mid" }, handler.Models);
        Assert.Equal(404, call.Status);
        Assert.Equal(1, client.FallbackEvents);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task FallbackWorthyStatus_Switches(int code)
    {
        var handler = new ScriptedHandler(Code(code), Ok("ok"));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-mid"), Stubs.Msgs());

        Assert.Equal("mock-mini", call.ServedModel);
        Assert.Equal(1, client.FallbackEvents);
    }

    // мережа/gateway недоступні: відповіді немає (status 0) — перемикання
    [Fact]
    public async Task NetworkError_Status0_Switches()
    {
        var handler = new ScriptedHandler(new Step(Throw: new HttpRequestException("connection refused")), Ok("ok"));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-mid"), Stubs.Msgs());

        Assert.Equal("mock-mini", call.ServedModel);
        Assert.Equal(1, client.FallbackEvents);
    }

    [Fact]
    public async Task NetworkError_AllFail_DegradesWithStatus0()
    {
        var handler = new ScriptedHandler(
            new Step(Throw: new HttpRequestException("down")), new Step(Throw: new HttpRequestException("down")));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-mini"), Stubs.Msgs());

        Assert.Equal(0, call.Status);
        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
    }

    // 200 з тілом, що не розпарсилось — перемикання (mock-маркером не відтворюється: __garbage віддає коректний JSON)
    [Fact]
    public async Task GarbageBody200_Switches()
    {
        var handler = new ScriptedHandler(new Step(HttpStatusCode.OK, "not json at all"), Ok("ok"));
        var client = Client(handler);

        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());

        Assert.Equal("ok", call.Reply.Content);
        Assert.Equal("mock-mid", call.ServedModel);
        Assert.Equal(1, client.FallbackEvents);
    }

    // timeout спроби — теж «відповіді не було» (status 0): перемикання, а не очікування до кінця
    [Fact]
    public async Task AttemptTimeout_Switches()
    {
        var handler = new ScriptedHandler(new Step(Delay: TimeSpan.FromSeconds(10)), Ok("ok"));
        var client = Client(handler, TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var call = await client.CallAsync(MockChain("mock-strong"), Stubs.Msgs());
        sw.Stop();

        Assert.Equal("mock-mid", call.ServedModel);
        Assert.Equal(1, client.FallbackEvents);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Chain_Table()
    {
        Assert.Equal(new[] { "mock-strong", "mock-mid", "mock-mini" }, ModelClient.Chain("mock-strong", "mock"));
        Assert.Equal(new[] { "mock-mid", "mock-mini" }, ModelClient.Chain("mock-mid", "mock"));
        Assert.Equal(new[] { "mock-mini", "mock-mid" }, ModelClient.Chain("mock-mini", "mock"));
        foreach (var tier in new[] { "mock-strong", "mock-mid", "mock-mini" })
            Assert.True(ModelClient.Chain(tier, "mock").Length >= 2, tier);
        // реальна модель: ланцюжок з одного елемента (задокументоване обмеження)
        Assert.Equal(new[] { "gpt-5-mini" }, ModelClient.Chain("gpt-5-mini", "gpt-5-mini"));
    }

    // понад бюджет ланцюжок mock-mini не йде на дорожчу mock-mid
    [Fact]
    public void Chain_OverBudget_MiniStaysOnMini()
    {
        Assert.Equal(new[] { "mock-mini" }, ModelClient.Chain(ModelClient.MiniModel, "mock", overBudget: true));
        // реальна модель і понад бюджет — ланцюжок з одного елемента
        Assert.Equal(new[] { "gpt-5-mini" }, ModelClient.Chain("gpt-5-mini", "gpt-5-mini", overBudget: true));
    }

    // правило бюджету: жоден крок після першого не дорожчий за обрану модель.
    // порядок цін — локально в тесті (mini < mid < strong, як у CostUsd() у Program.cs)
    [Theory]
    [InlineData("mock-strong")]
    [InlineData("mock-mid")]
    [InlineData("mock-mini")]
    public void Chain_OverBudget_NeverClimbsInPrice(string routed)
    {
        var rank = new Dictionary<string, int> { ["mock-mini"] = 0, ["mock-mid"] = 1, ["mock-strong"] = 2 };
        var chain = ModelClient.Chain(routed, "mock", overBudget: true);

        Assert.Equal(routed, chain[0]);
        foreach (var next in chain.Skip(1))
            Assert.True(rank[next] <= rank[chain[0]], $"{routed}: {string.Join(" -> ", chain)}");
    }

    // понад бюджет збій mini — одразу DegradedReply: одна спроба, без перемикання, лічильник не росте
    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(0)]
    public async Task OverBudget_MiniFails_DegradesWithoutSwitch(int code)
    {
        var step = code == 0 ? new Step(Throw: new HttpRequestException("down")) : Code(code);
        var handler = new ScriptedHandler(step, Ok("не має бути запитано"));
        var client = Client(handler);

        var call = await client.CallAsync(ModelClient.Chain(ModelClient.MiniModel, "mock", overBudget: true), Stubs.Msgs());

        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
        Assert.Equal(code, call.Status);
        Assert.Equal(0, call.Hops);
        Assert.False(call.UsageParsed);
        Assert.Equal(0, client.FallbackEvents);
        Assert.Equal(new[] { "mock-mini" }, handler.Models);
    }

    // контроль: у межах бюджету поведінка та сама — mini -> mid після 503
    [Fact]
    public async Task UnderBudget_MiniFails_FallsBackToMid()
    {
        var handler = new ScriptedHandler(Code(503), Ok("Відповідь mock-mid"));
        var client = Client(handler);

        var call = await client.CallAsync(ModelClient.Chain(ModelClient.MiniModel, "mock", overBudget: false), Stubs.Msgs());

        Assert.Equal("mock-mid", call.ServedModel);
        Assert.Equal(1, call.Hops);
        Assert.Equal(1, client.FallbackEvents);
        Assert.Equal(new[] { "mock-mini", "mock-mid" }, handler.Models);
    }

    [Fact]
    public void DegradedReply_HasNoDigitsAndNoEvalKeywords()
    {
        var text = ModelClient.DegradedReply;
        Assert.False(text.Any(c => char.IsDigit(c)), text);
        foreach (var word in new[] { "тікет", "статус", "email", "допомог", "не можу", "cannot", "refuse" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    // відповідь після fallback не лягає в кеш: другий такий самий запит знову йде в модель
    [Fact]
    public async Task FallbackReply_NotCached()
    {
        var handler = new ScriptedHandler(Code(503), Ok("Відповідь mock-mid"), Code(503), Ok("Відповідь mock-mid"));
        var client = Client(handler);
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var key = ResponseCache.Key("mock-strong", "v2", Stubs.Msgs());

        var first = await cache.GetOrCallAsync(key, () => client.CallAsync(MockChain("mock-strong"), Stubs.Msgs()));
        var second = await cache.GetOrCallAsync(key, () => client.CallAsync(MockChain("mock-strong"), Stubs.Msgs()));

        Assert.Equal(CacheSource.Miss, first.Source);
        Assert.Equal(CacheSource.Miss, second.Source);
        Assert.Equal(4, handler.Models.Count);
        Assert.False(cache.TryGet(key, out _));
    }

    // контроль: відповідь без перемикання кешується, як і раніше
    [Fact]
    public async Task DirectReply_StillCached()
    {
        var handler = new ScriptedHandler(Ok("Вітаю"));
        var client = Client(handler);
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var key = ResponseCache.Key("mock-mini", "v2", Stubs.Msgs());

        await cache.GetOrCallAsync(key, () => client.CallAsync(MockChain("mock-mini"), Stubs.Msgs()));
        var second = await cache.GetOrCallAsync(key, () => client.CallAsync(MockChain("mock-mini"), Stubs.Msgs()));

        Assert.Equal(CacheSource.Hit, second.Source);
        Assert.Single(handler.Models);
    }
}
