// юніт-тести circuit breaker (W4, опційно): відкриття після N збоїв поспіль, пропуск відкритої моделі без виклику,
// одна half-open проба, звільнення слота проби на кожному шляху, single-flight. Годинник — ручний (ManualTimeProvider),
// тож секунди проби не чекаємо. Запуск — у SDK-контейнері (див. CacheAndToolsTests.cs).

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

public class CircuitBreakerTests
{
    private const string Mini = ModelClient.MiniModel;
    private const string Mid = ModelClient.MidModel;
    private const string Strong = ModelClient.StrongModel;

    private static readonly CircuitOptions Opts = new(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

    private static ChatMessage[] Msgs(string text = "Привіт") =>
        new[] { new ChatMessage("system", "support"), new ChatMessage("user", text) };

    // годинник, який рухає лише тест
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // поведінка кожної моделі задається окремо; рахуємо виклики по моделях
    private sealed class ModelHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _behavior = new();

        public int Calls(string model) => _calls.TryGetValue(model, out var n) ? n : 0;
        public int Total => _calls.Values.Sum();

        public void Set(string model, Func<CancellationToken, Task<HttpResponseMessage>> behavior) => _behavior[model] = behavior;
        public void Code(string model, int code) => Set(model, _ => Task.FromResult(Resp(code, model)));
        public void Ok(params string[] models)
        {
            foreach (var m in models)
                Code(m, 200);
        }
        public void Garbage(string model) => Set(model, _ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json at all") }));
        public void Throw(string model) => Set(model, _ => throw new HttpRequestException("connection refused"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            string model;
            using (var doc = JsonDocument.Parse(body))
                model = doc.RootElement.GetProperty("model").GetString()!;
            _calls.AddOrUpdate(model, 1, (_, n) => n + 1);
            return _behavior.TryGetValue(model, out var f) ? await f(ct) : Resp(200, model);
        }

        public static HttpResponseMessage Resp(int code, string model) => new((HttpStatusCode)code)
        {
            Content = new StringContent(code == 200
                ? JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { role = "assistant", content = $"Відповідь {model}" } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5 }
                })
                : "{\"error\":{\"message\":\"boom\"}}", Encoding.UTF8, "application/json")
        };
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static ModelClient Client(ModelHandler handler, ManualTimeProvider time,
        CircuitOptions? options = null, TimeSpan? timeout = null) =>
        new(new Factory(handler), "http://gateway:4000", timeout, null, options ?? Opts, time,
            ModelClient.ChainModels("mock"));

    private static readonly string[] OnlyMini = { Mini };          // ланцюжок понад бюджет
    private static readonly string[] MiniMid = { Mini, Mid };
    private static string[] StrongChain => ModelClient.Chain(Strong, "mock");

    // відкриває mini (і mid, якщо ланцюжок mini -> mid) трьома збоями поспіль
    private static async Task OpenAsync(ModelClient client, ModelHandler handler, string[] chain)
    {
        foreach (var m in chain)
            handler.Code(m, 503);
        for (var i = 0; i < Opts.FailureThreshold; i++)
            await client.CallAsync(chain, Msgs($"збій {i}"));
        foreach (var m in chain)
            Assert.Equal(CircuitState.Open, client.CircuitOf(m));
    }

    [Fact]
    public void Defaults_3_30_5()
    {
        Assert.Equal(3, CircuitOptions.Default.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), CircuitOptions.Default.OpenDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), CircuitOptions.Default.ProbeInterval);
    }

    // /observability показує всі моделі ланцюжків ще до першого запиту
    [Fact]
    public void Snapshot_KnownModelsClosedFromStart()
    {
        var client = Client(new ModelHandler(), new ManualTimeProvider());
        var states = client.CircuitStates();

        Assert.Equal(new[] { Mid, Mini, Strong }, states.Keys);
        Assert.All(states.Values, s => Assert.Equal("closed", s));
        Assert.Equal(new[] { "gpt-5-mini" }, ModelClient.ChainModels("gpt-5-mini"));
    }

    // 1. рівно threshold збоїв поспіль відкривають; threshold-1 — ще ні
    [Fact]
    public async Task ExactlyThresholdFailures_Open()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        handler.Code(Mini, 503);

        await client.CallAsync(OnlyMini, Msgs("1"));
        await client.CallAsync(OnlyMini, Msgs("2"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        await client.CallAsync(OnlyMini, Msgs("3"));

        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
        Assert.Equal("open", client.CircuitStates()[Mini]);
        Assert.Equal(3, handler.Calls(Mini));
    }

    // 1. threshold-1 збоїв, потім успіх — лічильник з нуля: ще два збої не відкривають
    [Fact]
    public async Task SuccessResetsCounter()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());

        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Msgs("1"));
        await client.CallAsync(OnlyMini, Msgs("2"));
        handler.Ok(Mini);
        await client.CallAsync(OnlyMini, Msgs("3"));
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Msgs("4"));
        await client.CallAsync(OnlyMini, Msgs("5"));

        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        Assert.Equal(5, handler.Calls(Mini));
    }

    // 2. збоєм рахуються 0 (немає відповіді), 429, 5xx і 200, що не розпарсився
    [Theory]
    [InlineData("throw")]
    [InlineData("garbage")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    public async Task FailureKinds_Open(string kind)
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        if (kind == "throw") handler.Throw(Mini);
        else if (kind == "garbage") handler.Garbage(Mini);
        else handler.Code(Mini, int.Parse(kind));

        for (var i = 0; i < 3; i++)
            await client.CallAsync(OnlyMini, Msgs($"{i}"));

        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 2. 4xx — помилка конфігурації: ні збій, ні успіх
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task ClientErrors_Neutral(int code)
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        handler.Code(Mini, code);

        for (var i = 0; i < 5; i++)
            await client.CallAsync(OnlyMini, Msgs($"{i}"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        Assert.Equal(5, handler.Calls(Mini));

        // і лічильник не скидає: 503, 503, 4xx, 503 — це три збої поспіль
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Msgs("a"));
        await client.CallAsync(OnlyMini, Msgs("b"));
        handler.Code(Mini, code);
        await client.CallAsync(OnlyMini, Msgs("c"));
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Msgs("d"));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 3. відкриту модель пропускаємо без жодного виклику; відповідає наступна; це hop, +1 fallback_events, без кешу
    [Fact]
    public async Task OpenModel_Skipped_NextServes_NotCached()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, OnlyMini);
        handler.Ok(Mini, Mid);
        var events = client.FallbackEvents;
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var key = ResponseCache.Key(Mini, "v2", Msgs("кеш"));

        var first = await cache.GetOrCallAsync(key, () => client.CallAsync(MiniMid, Msgs("кеш")));
        var second = await cache.GetOrCallAsync(key, () => client.CallAsync(MiniMid, Msgs("кеш")));

        Assert.Equal(3, handler.Calls(Mini));            // лише ті, що відкрили
        Assert.Equal(2, handler.Calls(Mid));
        Assert.Equal(Mid, first.Call!.ServedModel);
        Assert.Equal(1, first.Call.Hops);
        Assert.Equal("Відповідь mock-mid", first.Reply.Content);
        Assert.Equal(CacheSource.Miss, first.Source);
        Assert.Equal(CacheSource.Miss, second.Source);
        Assert.False(cache.TryGet(key, out _));
        Assert.Equal(events + 2, client.FallbackEvents);
    }

    // 4. усі моделі ланцюжка відкриті — заглушка без жодного виклику, статус 503
    [Fact]
    public async Task AllOpen_Degraded_NoCalls_Status503()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, MiniMid);
        handler.Ok(Mini, Mid);
        var calls = handler.Total;
        var events = client.FallbackEvents;

        var call = await client.CallAsync(MiniMid, Msgs("далі"));

        Assert.Equal(calls, handler.Total);
        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
        Assert.Equal(503, call.Status);
        Assert.False(call.UsageParsed);
        Assert.Null(call.ServedModel);
        Assert.Equal(1, call.Hops);
        Assert.Equal(events + 1, client.FallbackEvents);
    }

    // 4. понад бюджет ([mock-mini]) з відкритою mini — одразу заглушка, нуль викликів, без перемикань
    [Fact]
    public async Task OverBudget_MiniOpen_StubWithoutCall()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, OnlyMini);
        handler.Ok(Mini);
        var events = client.FallbackEvents;

        var call = await client.CallAsync(ModelClient.Chain(Mini, "mock", overBudget: true), Msgs("бюджет"));

        Assert.Equal(3, handler.Calls(Mini));
        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
        Assert.Equal(503, call.Status);
        Assert.Equal(0, call.Hops);
        Assert.Equal(events, client.FallbackEvents);
    }

    // 4. була справжня спроба, а решту пропущено — статус останньої справжньої спроби, а не синтетичний 503
    [Fact]
    public async Task RealAttemptThenOpen_KeepsRealStatus()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, MiniMid);
        handler.Code(Strong, 429);

        var call = await client.CallAsync(StrongChain, Msgs("сильна"));

        Assert.Equal(429, call.Status);
        Assert.Equal(1, handler.Calls(Strong));
        Assert.Equal(2, call.Hops);
        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
    }

    // 5. до інтервалу проби — пропуск; після — рівно один виклик; успіх закриває
    [Fact]
    public async Task Probe_AfterInterval_SuccessCloses()
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        var client = Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);
        handler.Ok(Mini);

        time.Advance(TimeSpan.FromSeconds(4.9));
        await client.CallAsync(OnlyMini, Msgs("рано"));
        Assert.Equal(3, handler.Calls(Mini));

        time.Advance(TimeSpan.FromSeconds(0.1));
        var call = await client.CallAsync(OnlyMini, Msgs("проба"));

        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(Mini, call.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 5. невдала проба — знову Open, таймер з нуля: другого виклику немає до наступного інтервалу
    [Fact]
    public async Task Probe_Failure_ReopensAndRearms()
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        var client = Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);

        time.Advance(TimeSpan.FromSeconds(5));
        await client.CallAsync(OnlyMini, Msgs("проба 1"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));

        await client.CallAsync(OnlyMini, Msgs("одразу"));
        time.Advance(TimeSpan.FromSeconds(4));
        await client.CallAsync(OnlyMini, Msgs("за 4 с"));
        Assert.Equal(4, handler.Calls(Mini));

        handler.Ok(Mini);
        time.Advance(TimeSpan.FromSeconds(1));
        await client.CallAsync(OnlyMini, Msgs("проба 2"));
        Assert.Equal(5, handler.Calls(Mini));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 6. K різних паралельних запитів, поки проба в дорозі — до моделі доходить рівно один
    [Fact]
    public async Task Concurrent_OnlyOneProbeReachesModel()
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        var client = Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);
        time.Advance(TimeSpan.FromSeconds(5));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Set(Mini, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ModelHandler.Resp(200, Mini);
        });

        const int k = 16;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, k)
            .Select(i => Task.Run(async () =>
            {
                start.Wait();
                return await client.CallAsync(OnlyMini, Msgs($"паралельно {i}"));
            }))
            .ToArray();
        start.Set();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("half_open", client.CircuitStates()[Mini]);
        // усі, крім проби, завершуються заглушкою, не чекаючи її
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tasks.Count(t => t.IsCompleted) < k - 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(k - 1, tasks.Count(t => t.IsCompleted));

        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(4, handler.Calls(Mini));            // 3 відкрили + 1 проба
        Assert.Equal(1, results.Count(r => r.ServedModel == Mini));
        Assert.Equal(k - 1, results.Count(r => r.Reply.Content == ModelClient.DegradedReply && r.Status == 503));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 7. слот проби звільняється після timeout (status 0), винятку і 4xx — HalfOpen не застрягає
    [Theory]
    [InlineData("timeout")]
    [InlineData("throw")]
    [InlineData("404")]
    public async Task ProbeSlot_ReleasedOnEveryPath(string kind)
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        var client = Client(handler, time, timeout: TimeSpan.FromMilliseconds(100));
        await OpenAsync(client, handler, OnlyMini);
        if (kind == "timeout")
            handler.Set(Mini, async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return ModelHandler.Resp(200, Mini);
            });
        else if (kind == "throw") handler.Throw(Mini);
        else handler.Code(Mini, 404);

        time.Advance(TimeSpan.FromSeconds(5));
        await client.CallAsync(OnlyMini, Msgs("проба"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));

        handler.Ok(Mini);
        time.Advance(TimeSpan.FromSeconds(5));
        var call = await client.CallAsync(OnlyMini, Msgs("наступна проба"));
        Assert.Equal(5, handler.Calls(Mini));
        Assert.Equal(Mini, call.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 7a. запити, пущені в попередньому Closed, завершуються збоєм уже після того, як проба закрила запобіжник:
    // їхні результати застарілі (інше покоління) і здорову модель знову не відкривають
    [Theory]
    [InlineData("timeout")]
    [InlineData("503")]
    public async Task StaleResults_AfterProbeClosed_DoNotReopen(string kind)
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        var client = Client(handler, time);

        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Set(Mini, async _ =>
        {
            if (Interlocked.Increment(ref entered) == 3)
                allEntered.TrySetResult();
            await release.Task;
            // timeout спроби: AttemptAsync ловить скасування і повертає status 0
            if (kind == "timeout")
                throw new TaskCanceledException("attempt timeout");
            return ModelHandler.Resp(503, Mini);
        });
        var held = Enumerable.Range(0, 3)
            .Select(i => client.CallAsync(OnlyMini, Msgs($"завис {i}"))).ToArray();
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // ще три запити падають одразу і відкривають mini
        await OpenAsync(client, handler, OnlyMini);
        time.Advance(TimeSpan.FromSeconds(5));
        handler.Ok(Mini);
        var probe = await client.CallAsync(OnlyMini, Msgs("проба"));
        Assert.Equal(Mini, probe.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));

        // три запізнілі збої з минулого Closed
        release.SetResult();
        var stale = await Task.WhenAll(held);
        Assert.All(stale, r => Assert.Equal(ModelClient.DegradedReply, r.Reply.Content));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));

        // і лічильник поточного Closed вони не зачепили: два свіжі збої ще не відкривають, третій — відкриває
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Msgs("свіжий 1"));
        await client.CallAsync(OnlyMini, Msgs("свіжий 2"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        await client.CallAsync(OnlyMini, Msgs("свіжий 3"));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 8. після OpenDuration без трафіку — одна проба, а не повне закриття: один збій знову відкриває
    [Fact]
    public async Task AfterOpenDuration_SingleProbe_NotFullClose()
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        // проба рідша за верхню межу, тож пробу тут дозволяє саме OpenDuration
        var options = new CircuitOptions(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100));
        var client = Client(handler, time, options);
        handler.Code(Mini, 503);
        for (var i = 0; i < 3; i++)
            await client.CallAsync(OnlyMini, Msgs($"{i}"));

        time.Advance(TimeSpan.FromSeconds(29));
        await client.CallAsync(OnlyMini, Msgs("рано"));
        Assert.Equal(3, handler.Calls(Mini));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("open", client.CircuitStates()[Mini]);   // таймер сам нічого не закриває
        await client.CallAsync(OnlyMini, Msgs("проба"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 9. запобіжники незалежні: strong закритий, поки mini і mid відкриті
    [Fact]
    public async Task Breakers_AreIndependent()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, MiniMid);
        handler.Ok(Strong);

        var call = await client.CallAsync(StrongChain, Msgs("сильна"));

        Assert.Equal(Strong, call.ServedModel);
        Assert.Equal(0, call.Hops);
        Assert.Equal(1, handler.Calls(Strong));
        var states = client.CircuitStates();
        Assert.Equal("closed", states[Strong]);
        Assert.Equal("open", states[Mid]);
        Assert.Equal("open", states[Mini]);
    }

    // 10. N однакових запитів, об'єднаних single-flight, — один виклик і один збій на лічильнику
    [Fact]
    public async Task SingleFlight_JoinedRequests_OneObservation()
    {
        var handler = new ModelHandler();
        var client = Client(handler, new ManualTimeProvider());
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Set(Mini, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ModelHandler.Resp(503, Mini);
        });
        var msgs = Msgs("однаковий");
        var key = ResponseCache.Key(Mini, "v2", msgs);

        var leader = cache.GetOrCallAsync(key, () => client.CallAsync(OnlyMini, msgs));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var joined = Enumerable.Range(0, 5)
            .Select(_ => cache.GetOrCallAsync(key, () => client.CallAsync(OnlyMini, msgs))).ToArray();
        release.SetResult();
        await leader;
        await Task.WhenAll(joined);

        Assert.Equal(1, handler.Calls(Mini));
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Msgs("другий"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));   // 2 спостереження, а не 7
        await client.CallAsync(OnlyMini, Msgs("третій"));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 11. дриль: 3× __fail_503 на mini -> mid відкривають обидві; 4-й запит — нуль викликів;
    // через 5 с проба mini вдається
    [Fact]
    public async Task Drill_ThreeFail503_ThenProbeRecovers()
    {
        var handler = new ModelHandler();
        var time = new ManualTimeProvider();
        var client = Client(handler, time);
        var chain = ModelClient.Chain(Mini, "mock");
        handler.Code(Mini, 503);
        handler.Code(Mid, 503);
        handler.Code(Strong, 503);

        for (var i = 0; i < 3; i++)
        {
            var drill = await client.CallAsync(chain, Msgs($"__fail_503 {i}"));
            Assert.Equal(ModelClient.DegradedReply, drill.Reply.Content);
        }
        Assert.Equal(3, client.FallbackEvents);
        Assert.Equal("open", client.CircuitStates()[Mini]);
        Assert.Equal("open", client.CircuitStates()[Mid]);
        Assert.Equal("closed", client.CircuitStates()[Strong]);

        handler.Ok(Mini, Mid, Strong);
        var calls = handler.Total;
        var fourth = await client.CallAsync(chain, Msgs("звичайне питання"));
        Assert.Equal(calls, handler.Total);
        Assert.Equal(ModelClient.DegradedReply, fourth.Reply.Content);
        Assert.Equal(503, fourth.Status);
        Assert.Equal(4, client.FallbackEvents);

        time.Advance(TimeSpan.FromSeconds(5));
        var recovered = await client.CallAsync(chain, Msgs("ще одне питання"));
        Assert.Equal(Mini, recovered.ServedModel);
        Assert.Equal(0, recovered.Hops);
        Assert.Equal(calls + 1, handler.Total);
        Assert.Equal("closed", client.CircuitStates()[Mini]);
        Assert.Equal("open", client.CircuitStates()[Mid]);  // mid ще не пробували
        Assert.Equal(4, client.FallbackEvents);
    }
}
