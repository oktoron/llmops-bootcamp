// юніт-тести W5: правило сміття (ReplyQuality) і кеш, лічильник сміттєвих відповідей, статус провайдера зі стану
// circuit breaker і серії 4xx з агрегацією за бекендом, контракт полів консолі, частка hit кешу, лічильник timeout
// інструмента. SQL /observability тут не перевіряється — лише живцем: картки консолі порівнюються з трьома
// запитами ментора в psql (homework/hw5.md). Запуск — у SDK-контейнері (див. CacheAndToolsTests.cs).

using System.Text.Json;

public class ReplyQualityTests
{
    [Theory]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    [InlineData(" … — ")]
    [InlineData(null)]
    public void Garbage_NoLetterOrDigit(string? content) => Assert.True(ReplyQuality.IsGarbage(content));

    // коротка справжня відповідь — не сміття: довжину не перевіряємо
    [Theory]
    [InlineData("Так")]
    [InlineData("Ні.")]
    [InlineData("OK")]
    [InlineData("42")]
    [InlineData("... так")]
    [InlineData("Вітаю! Розкажіть, що сталося — допоможу розібратися.")]
    public void NotGarbage_AnyLetterOrDigit(string content) => Assert.False(ReplyQuality.IsGarbage(content));

    // чесний текст — не стаб деградації (без «технічні труднощі»): eval garbage-1 відрізняє їх за «змістовн»
    [Fact]
    public void GarbageReply_HonestText_NotDegradedStub()
    {
        Assert.NotEqual(ModelClient.DegradedReply, ReplyQuality.GarbageReply);
        Assert.Contains("змістовн", ReplyQuality.GarbageReply);
        Assert.DoesNotContain("технічні труднощі", ReplyQuality.GarbageReply);
        Assert.False(ReplyQuality.IsGarbage(ReplyQuality.GarbageReply));
    }

    // гілка /chat: підміна лише без tool call. з tool call порожній content — норма, відповідь складає інструмент
    [Theory]
    [InlineData(null, "...", true)]
    [InlineData(null, null, true)]
    [InlineData(null, "Так", false)]
    [InlineData("lookup_order", null, false)]
    [InlineData("create_ticket", "", false)]
    [InlineData("lookup_order", "...", false)]
    public void NeedsHonestReply_OnlyWithoutToolCall(string? toolCall, string? content, bool expected) =>
        Assert.Equal(expected, ReplyQuality.NeedsHonestReply(toolCall, content));

    [Fact]
    public void IsCacheable_RejectsGarbage()
    {
        Assert.False(ResponseCache.IsCacheable(200, true, new CachedReply("...", null, null)));
        Assert.False(ResponseCache.IsCacheable(200, true, new CachedReply(" ?! ", null, null)));
        Assert.True(ResponseCache.IsCacheable(200, true, new CachedReply("Так", null, null)));
    }

    // сміття не лягає в кеш: повтор того самого питання знову йде в модель (miss, а не hit)
    [Fact]
    public async Task GarbageReply_NotCached_RepeatCallsModel()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var calls = 0;
        Task<ModelCall> Call()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ModelCall(new CachedReply("...", null, null), 200, true, 10, 5, ModelClient.MiniModel));
        }

        var first = await cache.GetOrCallAsync("k", Call);
        var second = await cache.GetOrCallAsync("k", Call);

        Assert.Equal(CacheSource.Miss, first.Source);
        Assert.Equal(CacheSource.Miss, second.Source);
        Assert.Equal(2, calls);
        Assert.Equal(0, cache.Hits);
    }
}

public class ProviderStatusTests
{
    // заглушки — спільні (TestStubs.cs)
    private static readonly string[] Models = ModelClient.ChainModels("mock");

    // так само, як GET /providers у Program.cs: стан запобіжника і серія 4xx
    private static Dictionary<string, string> Statuses(ModelClient client) =>
        Observability.ProviderStatuses(Models, client.CircuitOf, client.HasClientErrorStreak)
            .ToDictionary(r => r.Name, r => r.Status);

    [Theory]
    [InlineData(CircuitState.Closed, "ok")]
    [InlineData(CircuitState.HalfOpen, "degraded")]
    [InlineData(CircuitState.Open, "down")]
    public void Mapping(CircuitState state, string expected) => Assert.Equal(expected, Observability.ProviderStatus(state));

    // увесь цикл запобіжника: closed -> open (3 збої) -> half_open (проба в дорозі) -> closed (вдала проба)
    [Fact]
    public void BreakerLifecycle_OkDownDegradedOk()
    {
        var time = new ManualTimeProvider();
        var breaker = new CircuitBreaker(ModelClient.MiniModel, Stubs.Opts, time);
        Assert.Equal("ok", Observability.ProviderStatus(breaker.State));

        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
        {
            Assert.Equal(CircuitDecision.Allow, breaker.TryAcquire(out var g));
            breaker.Record(CircuitOutcome.Failure, false, g, 503);
        }
        Assert.Equal("down", Observability.ProviderStatus(breaker.State));

        time.Advance(Stubs.Opts.ProbeAfter);
        Assert.Equal(CircuitDecision.Probe, breaker.TryAcquire(out var probeGen));
        Assert.Equal("degraded", Observability.ProviderStatus(breaker.State));

        breaker.Record(CircuitOutcome.Success, true, probeGen, 200);
        Assert.Equal("ok", Observability.ProviderStatus(breaker.State));
    }

    // /providers віддає по одному запису на кожну модель, що має запобіжник
    [Fact]
    public void ChainModels_CoverAllMockTiers()
    {
        Assert.Equal(3, Models.Length);
        Assert.Contains(ModelClient.MiniModel, Models);
        Assert.Contains(ModelClient.MidModel, Models);
        Assert.Contains(ModelClient.StrongModel, Models);
    }

    // усі mock-аліаси — один бекенд (як у gateway/litellm-config.yaml); невідома модель — сама собі бекенд
    [Fact]
    public void AliasBackends_AllMockAliasesShareOneBackend()
    {
        Assert.Single(Models.Select(Observability.BackendOf).Distinct());
        Assert.Equal("gpt-5-mini", Observability.BackendOf("gpt-5-mini"));
    }

    // drill ментора: 3× __fail_503 — ланцюжок mini -> mid, mock-strong не викликається жодного разу.
    // його запобіжник закритий, але бекенд той самий, що впав, тож статус — degraded (не ok). потім відновлення назад до ok
    [Fact]
    public async Task MiniMidFail_StrongNeverCalled_NotOk_ThenRecoversToOk()
    {
        var stub = new GatewayStub();
        var time = new ManualTimeProvider();
        var client = Stubs.Client(stub, time);
        var miniChain = ModelClient.Chain(ModelClient.MiniModel, "mock");
        stub.Code(ModelClient.MiniModel, 503);
        stub.Code(ModelClient.MidModel, 503);

        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
            await client.CallAsync(miniChain, Stubs.Msgs($"__fail_503 {i}"));

        Assert.Equal(0, stub.Calls(ModelClient.StrongModel));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(ModelClient.StrongModel));
        var during = Statuses(client);
        Assert.Equal("down", during[ModelClient.MiniModel]);
        Assert.Equal("down", during[ModelClient.MidModel]);
        Assert.Equal("degraded", during[ModelClient.StrongModel]);
        Assert.All(during.Values, s => Assert.NotEqual("ok", s));

        // провайдер ожив; mini закривається вдалою пробою, а mid ще відкритий (звичайні повідомлення на нього не йдуть).
        // mini уже обслуговує — не down, але й не ok, поки бекенд не здоровий; mid — власний down
        stub.Ok(ModelClient.MiniModel, ModelClient.MidModel);
        time.Advance(Stubs.Opts.ProbeAfter);
        await client.CallAsync(new[] { ModelClient.MiniModel }, Stubs.Msgs("проба mini"));
        Assert.Equal(CircuitState.Closed, client.CircuitOf(ModelClient.MiniModel));
        var afterMini = Statuses(client);
        Assert.Equal("degraded", afterMini[ModelClient.MiniModel]);
        Assert.Equal("down", afterMini[ModelClient.MidModel]);
        Assert.Equal("degraded", afterMini[ModelClient.StrongModel]);

        // закрився й останній запобіжник бекенду — усі знову ok
        await client.CallAsync(new[] { ModelClient.MidModel }, Stubs.Msgs("проба mid"));
        Assert.All(Statuses(client).Values, s => Assert.Equal("ok", s));
    }

    // власний open/half_open — власний статус; закритий аліас при нездоровому сусіді по бекенду — degraded, не ok
    [Fact]
    public void Aggregation_OwnStateOrDegradedPerBackend()
    {
        string Of(Func<string, CircuitState> stateOf, string model) =>
            Observability.ProviderStatuses(Models, stateOf).Single(r => r.Name == model).Status;

        CircuitState HalfMini(string m) => m == ModelClient.MiniModel ? CircuitState.HalfOpen : CircuitState.Closed;
        Assert.All(Models, m => Assert.Equal("degraded", Of(HalfMini, m)));

        CircuitState OpenMini(string m) => m == ModelClient.MiniModel ? CircuitState.Open : CircuitState.Closed;
        Assert.Equal("down", Of(OpenMini, ModelClient.MiniModel));
        Assert.Equal("degraded", Of(OpenMini, ModelClient.MidModel));
        Assert.Equal("degraded", Of(OpenMini, ModelClient.StrongModel));

        CircuitState OpenMidHalfMini(string m) => m == ModelClient.MidModel ? CircuitState.Open
            : m == ModelClient.MiniModel ? CircuitState.HalfOpen : CircuitState.Closed;
        Assert.Equal("down", Of(OpenMidHalfMini, ModelClient.MidModel));
        Assert.Equal("degraded", Of(OpenMidHalfMini, ModelClient.MiniModel));
        Assert.Equal("degraded", Of(OpenMidHalfMini, ModelClient.StrongModel));

        Assert.All(Models, m => Assert.Equal("ok", Of(_ => CircuitState.Closed, m)));
    }

    // реальна модель з MODEL — свій бекенд: її стан не змішується з mock-аліасами
    [Fact]
    public void Aggregation_UnknownModelIsItsOwnBackend()
    {
        var rows = Observability.ProviderStatuses(new[] { "gpt-5-mini", ModelClient.MiniModel },
            m => m == ModelClient.MiniModel ? CircuitState.Open : CircuitState.Closed);
        Assert.Equal("ok", rows.Single(r => r.Name == "gpt-5-mini").Status);
        Assert.Equal("down", rows.Single(r => r.Name == ModelClient.MiniModel).Status);
    }

    // «усі запити — 4xx» (описка в аліасі — LiteLLM віддає 400 Invalid model name): запобіжник 4xx не рахує і лишається закритим,
    // fallback не вмикається, але 3 спроби поспіль з 4xx — degraded, а не ok. сусіди по бекенду — теж degraded
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task ClientErrorStreak_Threshold_Degraded(int code)
    {
        var stub = new GatewayStub();
        var client = Stubs.Client(stub, new ManualTimeProvider());
        var miniChain = ModelClient.Chain(ModelClient.MiniModel, "mock");
        stub.Code(ModelClient.MiniModel, code);

        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
            await client.CallAsync(miniChain, Stubs.Msgs($"описка {i}"));

        // поведінка запобіжника і fallback не змінилась: закритий, без перемикань на mid
        Assert.Equal(CircuitState.Closed, client.CircuitOf(ModelClient.MiniModel));
        Assert.Equal(0, stub.Calls(ModelClient.MidModel));
        Assert.Equal(0, client.FallbackEvents);
        Assert.True(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.All(Statuses(client).Values, s => Assert.Equal("degraded", s));
    }

    // на одну менше за поріг — ще ok
    [Fact]
    public async Task ClientErrorStreak_BelowThreshold_Ok()
    {
        var stub = new GatewayStub();
        var client = Stubs.Client(stub, new ManualTimeProvider());
        stub.Code(ModelClient.MiniModel, 404);

        for (var i = 0; i < Stubs.Opts.FailureThreshold - 1; i++)
            await client.CallAsync(new[] { ModelClient.MiniModel }, Stubs.Msgs($"описка {i}"));

        Assert.False(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.All(Statuses(client).Values, s => Assert.Equal("ok", s));
    }

    // конфігурацію виправили: перша вдала відповідь скидає серію — знову ok
    [Fact]
    public async Task ClientErrorStreak_ThenSuccess_Ok()
    {
        var stub = new GatewayStub();
        var client = Stubs.Client(stub, new ManualTimeProvider());
        stub.Code(ModelClient.MiniModel, 404);
        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
            await client.CallAsync(new[] { ModelClient.MiniModel }, Stubs.Msgs($"описка {i}"));
        Assert.Equal("degraded", Statuses(client)[ModelClient.MiniModel]);

        stub.Ok(ModelClient.MiniModel);
        var call = await client.CallAsync(new[] { ModelClient.MiniModel }, Stubs.Msgs("виправили"));

        Assert.Equal(ModelClient.MiniModel, call.ServedModel);
        Assert.False(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.All(Statuses(client).Values, s => Assert.Equal("ok", s));
    }

    // 429 — не помилка конфігурації (його рахує запобіжник), тож серію 4xx він перериває, а не продовжує:
    // 404, 404, 429, 404, 404 — серія 2, не 4
    [Fact]
    public async Task ClientErrorStreak_429DoesNotCount()
    {
        var stub = new GatewayStub();
        var client = Stubs.Client(stub, new ManualTimeProvider());
        var only = new[] { ModelClient.MiniModel };

        foreach (var code in new[] { 404, 404, 429, 404, 404 })
        {
            stub.Code(ModelClient.MiniModel, code);
            await client.CallAsync(only, Stubs.Msgs($"код {code}"));
        }

        Assert.Equal(CircuitState.Closed, client.CircuitOf(ModelClient.MiniModel));
        Assert.False(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.All(Statuses(client).Values, s => Assert.Equal("ok", s));

        // і три 429 поспіль — це не серія 4xx, а відкритий запобіжник: down
        stub.Code(ModelClient.MiniModel, 429);
        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
            await client.CallAsync(only, Stubs.Msgs($"ліміт {i}"));
        Assert.False(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.Equal("down", Statuses(client)[ModelClient.MiniModel]);
    }

    // ще два тіла 400 від LiteLLM (лише для цих тестів); ContextWindowBody / InvalidModelBody — у GatewayStub
    private const string ContentPolicyBody =
        "{\"error\":{\"message\":\"litellm.ContentPolicyViolationError: AzureException - The response was filtered due to the prompt triggering Azure OpenAI's content management policy.\",\"type\":null,\"param\":null,\"code\":\"400\"}}";
    private const string ContextCodeBody =
        "{\"error\":{\"message\":\"This model's maximum context length is 8192 tokens\",\"type\":\"invalid_request_error\",\"param\":\"messages\",\"code\":\"context_length_exceeded\"}}";

    // троє користувачів поспіль із задовгим текстом — це не стан провайдера: серії немає, /providers лишається ok.
    // решта поведінки 400 та сама: запобіжник закритий, без fallback, відповідь — заглушка зі status 400
    [Fact]
    public async Task ClientErrorStreak_InputRejection400_DoesNotCount()
    {
        var stub = new GatewayStub();
        var client = Stubs.Client(stub, new ManualTimeProvider());
        var miniChain = ModelClient.Chain(ModelClient.MiniModel, "mock");
        stub.BadRequest(ModelClient.MiniModel, GatewayStub.ContextWindowBody);

        ModelCall? last = null;
        for (var i = 0; i < Stubs.Opts.FailureThreshold; i++)
            last = await client.CallAsync(miniChain, Stubs.Msgs($"задовгий текст {i}"));

        Assert.Equal(Stubs.Opts.FailureThreshold, stub.Calls(ModelClient.MiniModel));
        Assert.Equal(0, stub.Calls(ModelClient.MidModel));
        Assert.Equal(0, client.FallbackEvents);
        Assert.Equal(CircuitState.Closed, client.CircuitOf(ModelClient.MiniModel));
        Assert.Equal(400, last!.Status);
        Assert.Equal(ModelClient.DegradedReply, last.Reply.Content);
        Assert.False(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.All(Statuses(client).Values, s => Assert.Equal("ok", s));
    }

    // відмова через вміст серію й не скидає: config 400, config 400, content policy 400, config 400 — серія 3
    [Fact]
    public async Task ClientErrorStreak_InputRejection400_DoesNotReset()
    {
        var stub = new GatewayStub();
        var client = Stubs.Client(stub, new ManualTimeProvider());
        var only = new[] { ModelClient.MiniModel };

        foreach (var body in new[] { GatewayStub.InvalidModelBody, GatewayStub.InvalidModelBody, ContentPolicyBody })
        {
            stub.BadRequest(ModelClient.MiniModel, body);
            await client.CallAsync(only, Stubs.Msgs("запит"));
        }
        // content policy не дорахувалась: серія 2 — ще ok
        Assert.False(client.HasClientErrorStreak(ModelClient.MiniModel));

        stub.BadRequest(ModelClient.MiniModel, GatewayStub.InvalidModelBody);
        await client.CallAsync(only, Stubs.Msgs("запит"));

        // і не скинула: 2 + 1 = 3 — degraded
        Assert.True(client.HasClientErrorStreak(ModelClient.MiniModel));
        Assert.All(Statuses(client).Values, s => Assert.Equal("degraded", s));
    }

    // що не розпізнали — звичайний 4xx (рахується в серію), тобто поведінка за замовчуванням безпечна
    [Theory]
    [InlineData(ContentPolicyBody, true)]
    [InlineData(GatewayStub.ContextWindowBody, true)]
    [InlineData(ContextCodeBody, true)]
    [InlineData(GatewayStub.InvalidModelBody, false)]
    [InlineData(GatewayStub.ErrorBody, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not json at all", false)]
    [InlineData("[1,2]", false)]
    [InlineData("{\"error\":\"ContextWindowExceededError\"}", false)]
    [InlineData("{\"error\":\"boom\"}", false)]
    [InlineData("[]", false)]
    [InlineData("\"text\"", false)]
    public void ClientErrorStreak_InputRejection_Classifier(string? body, bool expected) =>
        Assert.Equal(expected, ModelClient.IsInputRejection(body));

    // власний стан запобіжника важливіший за серію: відкритий — down, а не degraded
    [Fact]
    public void ClientErrorStreak_OpenBreakerStillDown()
    {
        string Of(Func<string, CircuitState> stateOf, Func<string, bool> streak, string model) =>
            Observability.ProviderStatuses(Models, stateOf, streak).Single(r => r.Name == model).Status;

        bool StreakMini(string m) => m == ModelClient.MiniModel;
        CircuitState OpenMini(string m) => m == ModelClient.MiniModel ? CircuitState.Open : CircuitState.Closed;
        Assert.Equal("down", Of(OpenMini, StreakMini, ModelClient.MiniModel));
        Assert.Equal("degraded", Of(OpenMini, StreakMini, ModelClient.MidModel));

        // серія на закритому mini, відкритий mid: mid — down, mini і strong — degraded
        CircuitState OpenMid(string m) => m == ModelClient.MidModel ? CircuitState.Open : CircuitState.Closed;
        Assert.Equal("down", Of(OpenMid, StreakMini, ModelClient.MidModel));
        Assert.Equal("degraded", Of(OpenMid, StreakMini, ModelClient.MiniModel));
        Assert.Equal("degraded", Of(OpenMid, StreakMini, ModelClient.StrongModel));

        // без серії і з закритими запобіжниками — ok (як і без параметра серії)
        Assert.All(Models, m => Assert.Equal("ok", Of(_ => CircuitState.Closed, _ => false, m)));
    }
}

// контракт консолі: імена полів, які читає ui/src/app/console.component.ts. перейменування p95_ms чи status
// лишило б плитку «—» за зелених юніт-тестів і evals — тепер це червоний тест. серіалізація — як у Results.Json
public class ConsoleContractTests
{
    private static readonly SortedDictionary<string, string> Circuits = new() { [ModelClient.MiniModel] = "closed" };

    [Fact]
    public void Observability_FieldNamesAndNumbers()
    {
        var root = Stubs.SerializeWeb(Observability.ObservabilityResponse(
            new DaySummary(37, 37.8m, 197, 1, 2, 3, 4, 5), 14.3, 3, 6, 8, Circuits, 7));

        // консоль читає ці п'ять і показує «—», якщо поле не число
        Assert.Equal(197, root.GetProperty("p95_ms").GetInt64());
        Assert.Equal(37, root.GetProperty("requests").GetInt64());
        Assert.Equal(14.3, root.GetProperty("cache_hit_pct").GetDouble());
        Assert.Equal(37.8m, root.GetProperty("error_rate_pct").GetDecimal());
        Assert.Equal(3, root.GetProperty("fallback_events").GetInt64());

        var errors = root.GetProperty("errors");
        Assert.Equal(1, errors.GetProperty("rate_limited_429").GetInt64());
        Assert.Equal(2, errors.GetProperty("provider_5xx").GetInt64());
        Assert.Equal(3, errors.GetProperty("timeout").GetInt64());
        Assert.Equal(5, errors.GetProperty("client_4xx").GetInt64());
        Assert.Equal(4, errors.GetProperty("guardrail_blocked").GetInt64());
        Assert.Equal(6, errors.GetProperty("tool_timeout").GetInt64());
        Assert.Equal(8, errors.GetProperty("garbage_reply").GetInt64());
        Assert.Equal("closed", root.GetProperty("circuits").GetProperty(ModelClient.MiniModel).GetString());
        Assert.Equal(7, root.GetProperty("approvals_expired").GetInt64());
    }

    // база недоступна: поля з requests — null (консоль покаже «—»), поля з пам'яті — числа
    [Fact]
    public void Observability_DbDown_RequestsFieldsNull()
    {
        var root = Stubs.SerializeWeb(Observability.ObservabilityResponse(null, 0, 0, 0, 0, Circuits, 0));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("p95_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("requests").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error_rate_pct").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("cache_hit_pct").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("fallback_events").ValueKind);
        // лічильники в пам'яті — числа й без бази
        Assert.Equal(JsonValueKind.Number, root.GetProperty("errors").GetProperty("garbage_reply").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("errors").GetProperty("tool_timeout").ValueKind);
    }

    // консоль читає providers[].name і providers[].status; точка червона, якщо status !== 'ok'
    [Fact]
    public void Providers_FieldNames()
    {
        var root = Stubs.SerializeWeb(Observability.ProvidersResponse(ModelClient.ChainModels("mock"), _ => CircuitState.Open));
        var rows = root.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, r =>
        {
            Assert.Contains(r.GetProperty("name").GetString(), ModelClient.ChainModels("mock"));
            Assert.Equal("down", r.GetProperty("status").GetString());
        });
    }
}

public class CacheHitPctTests
{
    [Theory]
    [InlineData(0L, 0L, 0.0)]      // звернень ще не було — 0, а не ділення на нуль
    [InlineData(0L, 5L, 0.0)]
    [InlineData(5L, 0L, 100.0)]
    [InlineData(1L, 2L, 33.3)]
    [InlineData(2L, 1L, 66.7)]
    [InlineData(1L, 7L, 12.5)]
    public void HitPct(long hits, long misses, double expected) => Assert.Equal(expected, Observability.HitPct(hits, misses));
}

// errors.garbage_reply: лічильник росте рівно тоді, коли /chat підміняє відповідь чесним текстом
public class GarbageReplyCounterTests
{
    [Fact]
    public void Garbage_ReplacedAndCounted()
    {
        var counter = new GarbageReplyCounter();
        Assert.Equal(0, counter.Count);

        Assert.True(counter.TryReplace(null, "...", out var reply));
        Assert.Equal(ReplyQuality.GarbageReply, reply);
        Assert.Equal(1, counter.Count);

        Assert.True(counter.TryReplace(null, " ?! ", out _));
        Assert.Equal(2, counter.Count);
    }

    // звичайна відповідь і порожній текст з tool call — без підміни, лічильник стоїть
    [Theory]
    [InlineData(null, "Так")]
    [InlineData("lookup_order", "")]
    [InlineData("create_ticket", "...")]
    public void NotGarbage_PassedThrough_NotCounted(string? toolCall, string content)
    {
        var counter = new GarbageReplyCounter();

        Assert.False(counter.TryReplace(toolCall, content, out var reply));
        Assert.Equal(content, reply);
        Assert.Equal(0, counter.Count);
    }

    // паралельні /chat не гублять інкрементів
    [Fact]
    public async Task Concurrent_NoLostIncrements()
    {
        var counter = new GarbageReplyCounter();
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i => Task.Run(() => counter.TryReplace(null, "...", out _))));
        Assert.Equal(1000, counter.Count);
    }
}

public class ToolTimeoutCounterTests
{
    // повільне сховище, яке ігнорує токен скасування
    private sealed class SlowStore : IOrderStore
    {
        public async Task<string?> GetStatusAsync(string orderId, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return "занадто пізно";
        }
    }

    [Fact]
    public async Task Timeout_IncrementsOncePerToolCall()
    {
        var runner = new ToolRunner(new SlowStore(), TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, runner.Timeouts);

        await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #123?");
        Assert.Equal(1, runner.Timeouts);

        // три номери під одним бюджетом — один timeout виклику, а не три
        await runner.RunAsync("lookup_order", "{}", "#1 #2 #3");
        Assert.Equal(2, runner.Timeouts);
    }

    // без timeout лічильник стоїть: знайдено, не знайдено, валідація, create_ticket
    [Fact]
    public async Task NoTimeout_CounterUnchanged()
    {
        var runner = new ToolRunner(new FakeOrderStore(), TimeSpan.FromSeconds(2));

        await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #123?");
        await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #999?");
        await runner.RunAsync("lookup_order", "{}", "Де моє замовлення?");
        await runner.RunAsync("create_ticket", "{}", "Поверніть гроші");

        Assert.Equal(0, runner.Timeouts);
    }
}
