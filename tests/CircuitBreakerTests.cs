// юніт-тести circuit breaker (W4, опційно): відкриття після N збоїв поспіль, пропуск відкритої моделі без виклику,
// одна half-open проба, звільнення слота проби на кожному шляху, single-flight. Годинник — ручний (ManualTimeProvider),
// тож секунди проби не чекаємо. Запуск — у SDK-контейнері (див. CacheAndToolsTests.cs).

public class CircuitBreakerTests
{
    private const string Mini = ModelClient.MiniModel;
    private const string Mid = ModelClient.MidModel;
    private const string Strong = ModelClient.StrongModel;

    // заглушки — спільні (TestStubs.cs): GatewayStub задає поведінку кожної моделі й рахує виклики

    private static readonly string[] OnlyMini = { Mini };          // ланцюжок понад бюджет
    private static readonly string[] MiniMid = { Mini, Mid };
    private static string[] StrongChain => ModelClient.Chain(Strong, "mock");

    // відкриває mini (і mid, якщо ланцюжок mini -> mid) трьома збоями поспіль
    private static async Task OpenAsync(ModelClient client, GatewayStub handler, string[] chain)
    {
        foreach (var m in chain)
            handler.Code(m, 503);
        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
            await client.CallAsync(chain, Stubs.Msgs($"збій {i}"));
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
        var client = Stubs.Client(new GatewayStub(), new ManualTimeProvider());
        var states = client.CircuitStates();

        Assert.Equal(new[] { Mid, Mini, Strong }, states.Keys);
        Assert.All(states.Values, s => Assert.Equal("closed", s));
        Assert.Equal(new[] { "gpt-5-mini" }, ModelClient.ChainModels("gpt-5-mini"));
    }

    // 1. рівно threshold збоїв поспіль відкривають; threshold-1 — ще ні
    [Fact]
    public async Task ExactlyThresholdFailures_Open()
    {
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        handler.Code(Mini, 503);

        await client.CallAsync(OnlyMini, Stubs.Msgs("1"));
        await client.CallAsync(OnlyMini, Stubs.Msgs("2"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        await client.CallAsync(OnlyMini, Stubs.Msgs("3"));

        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
        Assert.Equal("open", client.CircuitStates()[Mini]);
        Assert.Equal(3, handler.Calls(Mini));
    }

    // 1. threshold-1 збоїв, потім успіх — лічильник з нуля: ще два збої не відкривають
    [Fact]
    public async Task SuccessResetsCounter()
    {
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());

        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Stubs.Msgs("1"));
        await client.CallAsync(OnlyMini, Stubs.Msgs("2"));
        handler.Ok(Mini);
        await client.CallAsync(OnlyMini, Stubs.Msgs("3"));
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Stubs.Msgs("4"));
        await client.CallAsync(OnlyMini, Stubs.Msgs("5"));

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
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        if (kind == "throw") handler.Throw(Mini);
        else if (kind == "garbage") handler.Garbage(Mini);
        else handler.Code(Mini, int.Parse(kind));

        for (var i = 0; i < 3; i++)
            await client.CallAsync(OnlyMini, Stubs.Msgs($"{i}"));

        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 2. 4xx — помилка запиту або конфігурації: ні збій, ні успіх
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task ClientErrors_Neutral(int code)
    {
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        handler.Code(Mini, code);

        for (var i = 0; i < 5; i++)
            await client.CallAsync(OnlyMini, Stubs.Msgs($"{i}"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        Assert.Equal(5, handler.Calls(Mini));

        // і лічильник не скидає: 503, 503, 4xx, 503 — це три збої поспіль
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Stubs.Msgs("a"));
        await client.CallAsync(OnlyMini, Stubs.Msgs("b"));
        handler.Code(Mini, code);
        await client.CallAsync(OnlyMini, Stubs.Msgs("c"));
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Stubs.Msgs("d"));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 3. відкриту модель пропускаємо без жодного виклику; відповідає наступна; це hop, +1 fallback_events, без кешу
    [Fact]
    public async Task OpenModel_Skipped_NextServes_NotCached()
    {
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, OnlyMini);
        handler.Ok(Mini, Mid);
        var events = client.FallbackEvents;
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var key = ResponseCache.Key(Mini, "v2", Stubs.Msgs("кеш"));

        var first = await cache.GetOrCallAsync(key, () => client.CallAsync(MiniMid, Stubs.Msgs("кеш")));
        var second = await cache.GetOrCallAsync(key, () => client.CallAsync(MiniMid, Stubs.Msgs("кеш")));

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
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, MiniMid);
        handler.Ok(Mini, Mid);
        var calls = handler.Total;
        var events = client.FallbackEvents;

        var call = await client.CallAsync(MiniMid, Stubs.Msgs("далі"));

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
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, OnlyMini);
        handler.Ok(Mini);
        var events = client.FallbackEvents;

        var call = await client.CallAsync(ModelClient.Chain(Mini, "mock", overBudget: true), Stubs.Msgs("бюджет"));

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
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, MiniMid);
        handler.Code(Strong, 429);

        var call = await client.CallAsync(StrongChain, Stubs.Msgs("сильна"));

        Assert.Equal(429, call.Status);
        Assert.Equal(1, handler.Calls(Strong));
        Assert.Equal(2, call.Hops);
        Assert.Equal(ModelClient.DegradedReply, call.Reply.Content);
    }

    // 5. до інтервалу проби — пропуск; після — рівно один виклик; успіх закриває
    [Fact]
    public async Task Probe_AfterInterval_SuccessCloses()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);
        handler.Ok(Mini);

        time.Advance(TimeSpan.FromSeconds(4.9));
        await client.CallAsync(OnlyMini, Stubs.Msgs("рано"));
        Assert.Equal(3, handler.Calls(Mini));

        time.Advance(TimeSpan.FromSeconds(0.1));
        var call = await client.CallAsync(OnlyMini, Stubs.Msgs("проба"));

        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(Mini, call.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 5. невдала проба — знову Open, таймер з нуля: другого виклику немає до наступного інтервалу
    [Fact]
    public async Task Probe_Failure_ReopensAndRearms()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);

        time.Advance(TimeSpan.FromSeconds(5));
        await client.CallAsync(OnlyMini, Stubs.Msgs("проба 1"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));

        await client.CallAsync(OnlyMini, Stubs.Msgs("одразу"));
        time.Advance(TimeSpan.FromSeconds(4));
        await client.CallAsync(OnlyMini, Stubs.Msgs("за 4 с"));
        Assert.Equal(4, handler.Calls(Mini));

        handler.Ok(Mini);
        time.Advance(TimeSpan.FromSeconds(1));
        await client.CallAsync(OnlyMini, Stubs.Msgs("проба 2"));
        Assert.Equal(5, handler.Calls(Mini));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 6. K різних паралельних запитів, поки проба в дорозі — до моделі доходить рівно один
    [Fact]
    public async Task Concurrent_OnlyOneProbeReachesModel()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);
        time.Advance(TimeSpan.FromSeconds(5));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Set(Mini, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return GatewayStub.Resp(200, Mini);
        });

        const int k = 16;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, k)
            .Select(i => Task.Run(async () =>
            {
                start.Wait();
                return await client.CallAsync(OnlyMini, Stubs.Msgs($"паралельно {i}"));
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
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time, timeout: TimeSpan.FromMilliseconds(100));
        await OpenAsync(client, handler, OnlyMini);
        if (kind == "timeout")
            handler.Set(Mini, async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return GatewayStub.Resp(200, Mini);
            });
        else if (kind == "throw") handler.Throw(Mini);
        else handler.Code(Mini, 404);

        time.Advance(TimeSpan.FromSeconds(5));
        await client.CallAsync(OnlyMini, Stubs.Msgs("проба"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));

        handler.Ok(Mini);
        time.Advance(TimeSpan.FromSeconds(5));
        var call = await client.CallAsync(OnlyMini, Stubs.Msgs("наступна проба"));
        Assert.Equal(5, handler.Calls(Mini));
        Assert.Equal(Mini, call.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 7b. Ignored на пробі лише звільняє слот: таймери й покоління не чіпає, тож наступний запит — одразу нова проба.
    // закрити запобіжник Ignored не може; закриває наступна вдала проба
    [Fact]
    public void Probe_Ignored_ReleasesSlot_NextRequestProbesAtOnce()
    {
        var time = new ManualTimeProvider();
        var breaker = new CircuitBreaker(Mini, Stubs.Opts, time);
        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
        {
            Assert.Equal(CircuitDecision.Allow, breaker.TryAcquire(out var g));
            breaker.Record(CircuitOutcome.Failure, wasProbe: false, g, 503);
        }
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(Stubs.Opts.ProbeAfter);
        Assert.Equal(CircuitDecision.Probe, breaker.TryAcquire(out var probeGen));
        breaker.Record(CircuitOutcome.Ignored, wasProbe: true, probeGen, 400);
        Assert.NotEqual(CircuitState.Closed, breaker.State);

        // годинник не рухаємо
        Assert.Equal(CircuitDecision.Probe, breaker.TryAcquire(out var nextGen));
        Assert.Equal(probeGen, nextGen);
        breaker.Record(CircuitOutcome.Success, wasProbe: true, nextGen, 200);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    // 7c. відмова через вміст (задовгий контекст) на пробі: та сама заглушка зі status 400, без перемикання,
    // але запобіжник не перевідкривається на ProbeInterval — наступний запит без руху годинника доходить до моделі
    [Fact]
    public async Task HalfOpenProbe_InputRejection400_DoesNotReopen()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);

        time.Advance(Stubs.Opts.ProbeAfter);
        handler.BadRequest(Mini, GatewayStub.ContextWindowBody);
        var rejected = await client.CallAsync(OnlyMini, Stubs.Msgs("задовгий текст"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(400, rejected.Status);
        Assert.Equal(ModelClient.DegradedReply, rejected.Reply.Content);
        Assert.Equal("open", client.CircuitStates()[Mini]);

        handler.Ok(Mini);
        var call = await client.CallAsync(OnlyMini, Stubs.Msgs("звичайне питання"));
        Assert.Equal(5, handler.Calls(Mini));
        Assert.Equal(Mini, call.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
    }

    // 7d. а 400 конфігурації (описка в аліасі) на пробі, як і раніше, перевідкриває з таймерами від нуля:
    // до наступного ProbeInterval модель пропускаємо без виклику
    [Fact]
    public async Task HalfOpenProbe_Config400_StillReopens()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);
        await OpenAsync(client, handler, OnlyMini);

        time.Advance(Stubs.Opts.ProbeAfter);
        handler.BadRequest(Mini, GatewayStub.InvalidModelBody);
        var probe = await client.CallAsync(OnlyMini, Stubs.Msgs("проба"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(400, probe.Status);
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));

        handler.Ok(Mini);
        var skipped = await client.CallAsync(OnlyMini, Stubs.Msgs("одразу"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(503, skipped.Status);

        time.Advance(Stubs.Opts.ProbeAfter);
        var call = await client.CallAsync(OnlyMini, Stubs.Msgs("наступна проба"));
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
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);

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
            return GatewayStub.Resp(503, Mini);
        });
        var held = Enumerable.Range(0, 3)
            .Select(i => client.CallAsync(OnlyMini, Stubs.Msgs($"завис {i}"))).ToArray();
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // ще три запити падають одразу і відкривають mini
        await OpenAsync(client, handler, OnlyMini);
        time.Advance(TimeSpan.FromSeconds(5));
        handler.Ok(Mini);
        var probe = await client.CallAsync(OnlyMini, Stubs.Msgs("проба"));
        Assert.Equal(Mini, probe.ServedModel);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));

        // три запізнілі збої з минулого Closed
        release.SetResult();
        var stale = await Task.WhenAll(held);
        Assert.All(stale, r => Assert.Equal(ModelClient.DegradedReply, r.Reply.Content));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));

        // і лічильник поточного Closed вони не зачепили: два свіжі збої ще не відкривають, третій — відкриває
        handler.Code(Mini, 503);
        await client.CallAsync(OnlyMini, Stubs.Msgs("свіжий 1"));
        await client.CallAsync(OnlyMini, Stubs.Msgs("свіжий 2"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));
        await client.CallAsync(OnlyMini, Stubs.Msgs("свіжий 3"));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 8. після OpenDuration без трафіку — одна проба, а не повне закриття: один збій знову відкриває
    [Fact]
    public async Task AfterOpenDuration_SingleProbe_NotFullClose()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        // проба рідша за верхню межу, тож пробу тут дозволяє саме OpenDuration
        var options = new CircuitOptions(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100));
        var client = Stubs.Client(handler, time, options);
        handler.Code(Mini, 503);
        for (var i = 0; i < 3; i++)
            await client.CallAsync(OnlyMini, Stubs.Msgs($"{i}"));

        time.Advance(TimeSpan.FromSeconds(29));
        await client.CallAsync(OnlyMini, Stubs.Msgs("рано"));
        Assert.Equal(3, handler.Calls(Mini));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("open", client.CircuitStates()[Mini]);   // таймер сам нічого не закриває
        await client.CallAsync(OnlyMini, Stubs.Msgs("проба"));
        Assert.Equal(4, handler.Calls(Mini));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 9. запобіжники незалежні: strong закритий, поки mini і mid відкриті
    [Fact]
    public async Task Breakers_AreIndependent()
    {
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        await OpenAsync(client, handler, MiniMid);
        handler.Ok(Strong);

        var call = await client.CallAsync(StrongChain, Stubs.Msgs("сильна"));

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
        var handler = new GatewayStub();
        var client = Stubs.Client(handler, new ManualTimeProvider());
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Set(Mini, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return GatewayStub.Resp(503, Mini);
        });
        var msgs = Stubs.Msgs("однаковий");
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
        await client.CallAsync(OnlyMini, Stubs.Msgs("другий"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(Mini));   // 2 спостереження, а не 7
        await client.CallAsync(OnlyMini, Stubs.Msgs("третій"));
        Assert.Equal(CircuitState.Open, client.CircuitOf(Mini));
    }

    // 11. drill: 3× __fail_503 на mini -> mid відкривають обидві; 4-й запит — нуль викликів;
    // через 5 с проба mini вдається
    [Fact]
    public async Task Drill_ThreeFail503_ThenProbeRecovers()
    {
        var handler = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(handler, time);
        var chain = ModelClient.Chain(Mini, "mock");
        handler.Code(Mini, 503);
        handler.Code(Mid, 503);
        handler.Code(Strong, 503);

        for (var i = 0; i < 3; i++)
        {
            var drill = await client.CallAsync(chain, Stubs.Msgs($"__fail_503 {i}"));
            Assert.Equal(ModelClient.DegradedReply, drill.Reply.Content);
        }
        Assert.Equal(3, client.FallbackEvents);
        Assert.Equal("open", client.CircuitStates()[Mini]);
        Assert.Equal("open", client.CircuitStates()[Mid]);
        Assert.Equal("closed", client.CircuitStates()[Strong]);

        handler.Ok(Mini, Mid, Strong);
        var calls = handler.Total;
        var fourth = await client.CallAsync(chain, Stubs.Msgs("звичайне питання"));
        Assert.Equal(calls, handler.Total);
        Assert.Equal(ModelClient.DegradedReply, fourth.Reply.Content);
        Assert.Equal(503, fourth.Status);
        Assert.Equal(4, client.FallbackEvents);

        time.Advance(TimeSpan.FromSeconds(5));
        var recovered = await client.CallAsync(chain, Stubs.Msgs("ще одне питання"));
        Assert.Equal(Mini, recovered.ServedModel);
        Assert.Equal(0, recovered.Hops);
        Assert.Equal(calls + 1, handler.Total);
        Assert.Equal("closed", client.CircuitStates()[Mini]);
        Assert.Equal("open", client.CircuitStates()[Mid]);  // mid ще не пробували
        Assert.Equal(4, client.FallbackEvents);
    }
}
