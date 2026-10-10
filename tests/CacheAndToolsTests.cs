// юніт-тести W3: ключ кешу, лічильники hit/miss, TTL, single-flight, розбір номерів замовлень і timeout.
// запуск — у SDK-контейнері, не хостовим dotnet (з кореня репозиторію; копія в /w, щоб bin/obj не лягли в репо):
//   docker run --rm -v "$PWD:/src:ro" mcr.microsoft.com/dotnet/sdk:8.0 sh -c \
//     'mkdir /w && cp -r /src/service /src/tests /w/ && rm -rf /w/service/bin /w/service/obj && dotnet test /w/tests'

using System.Diagnostics;

public class ResponseCacheTests
{
    private static ChatMessage[] Msgs(string prompt, string user) =>
        new[] { new ChatMessage("system", prompt), new ChatMessage("user", user) };

    [Fact]
    public void Key_SameInput_SameKey()
    {
        Assert.Equal(
            ResponseCache.Key("mock-mid", "v2", Msgs("support v2", "Де моє замовлення #123?")),
            ResponseCache.Key("mock-mid", "v2", Msgs("support v2", "Де моє замовлення #123?")));
    }

    // негативна перевірка ментора: зміна промпта (версії чи тексту) мусить бути промахом
    [Fact]
    public void Key_ChangesWhenPromptVersionChanges()
    {
        Assert.NotEqual(
            ResponseCache.Key("mock-mini", "v2", Msgs("support", "Привіт")),
            ResponseCache.Key("mock-mini", "v1", Msgs("support", "Привіт")));
    }

    [Fact]
    public void Key_ChangesWhenPromptBodyChanges()
    {
        Assert.NotEqual(
            ResponseCache.Key("mock-mini", "v2", Msgs("support A", "Привіт")),
            ResponseCache.Key("mock-mini", "v2", Msgs("support B", "Привіт")));
    }

    [Fact]
    public void Key_ChangesWhenModelOrMessageChanges()
    {
        var baseKey = ResponseCache.Key("mock-mini", "v2", Msgs("support", "Привіт"));
        Assert.NotEqual(baseKey, ResponseCache.Key("mock-strong", "v2", Msgs("support", "Привіт")));
        Assert.NotEqual(baseKey, ResponseCache.Key("mock-mini", "v2", Msgs("support", "Привіт!")));
    }

    [Fact]
    public void HitAndMiss_CountedSeparately()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var key = ResponseCache.Key("mock-mini", "v2", Msgs("support", "Привіт"));

        Assert.False(cache.TryGet(key, out _));
        Assert.Equal(0, cache.Hits);
        Assert.Equal(1, cache.Misses);

        cache.Set(key, new CachedReply("Вітаю!", null, null));
        Assert.True(cache.TryGet(key, out var reply));
        Assert.True(cache.TryGet(key, out _));

        Assert.Equal("Вітаю!", reply!.Content);
        Assert.Equal(2, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void ExpiredEntry_IsMiss()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new ResponseCache(TimeSpan.FromSeconds(60), () => now);
        cache.Set("k", new CachedReply("old", null, null));

        now = now.AddSeconds(61);
        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void IsCacheable_RejectsFailuresAndSideEffects()
    {
        Assert.True(ResponseCache.IsCacheable(200, true, new CachedReply("ok", "lookup_order", "{}")));
        Assert.False(ResponseCache.IsCacheable(500, true, new CachedReply("ok", null, null)));
        Assert.False(ResponseCache.IsCacheable(200, false, new CachedReply("Сервіс тимчасово недоступний.", null, null)));
        Assert.False(ResponseCache.IsCacheable(200, true, new CachedReply("", null, null)));
        Assert.False(ResponseCache.IsCacheable(200, true, new CachedReply("Створюю тікет", "create_ticket", "{}")));
    }
}

public class ToolRunnerTests
{
    // сховище, що рахує виклики: так видно, чи інструмент узагалі виконувався
    private sealed class CountingStore : IOrderStore
    {
        public int Calls;
        public Task<string?> GetStatusAsync(string orderId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<string?>(orderId == "123" ? "в дорозі" : null);
        }
    }

    // повільне сховище, яке ще й ігнорує токен скасування — найгірший випадок для timeout
    private sealed class SlowStore : IOrderStore
    {
        public async Task<string?> GetStatusAsync(string orderId, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return "занадто пізно";
        }
    }

    // expected — номери через кому в порядку появи; "" — номера немає
    [Theory]
    [InlineData("{\"order_id\":\"77\"}", "Де замовлення #123?", "77")]   // аргументи моделі мають пріоритет
    [InlineData("{\"order_id\":77}", "", "77")]
    [InlineData("{}", "Де моє замовлення #123?", "123")]               // так шле mock: беремо з повідомлення
    [InlineData(null, "замовлення #10482", "10482")]
    [InlineData("{\"order_id\":\"abc\"}", "Де замовлення #5?", "5")]   // не цифри — аргументу немає
    [InlineData("not json", "Де моє замовлення?", "")]
    [InlineData("{}", "Де моє замовлення?", "")]
    // позначки: №, повноширинний ＃, пробіл після позначки
    [InlineData("{}", "Де моє замовлення №123?", "123")]
    [InlineData("{}", "Де моє замовлення № 123?", "123")]
    [InlineData("{}", "Де моє замовлення # 123?", "123")]
    [InlineData("{}", "Де моє замовлення ＃123?", "123")]
    // голе число — лише коли воно єдине в повідомленні
    [InlineData("{}", "Де моє замовлення 123?", "123")]
    [InlineData("{}", "2 товари, замовлення 123", "")]
    [InlineData("{}", "2 товари в замовленні #123", "123")]             // число з позначкою важливіше за голе
    // голе число — до 6 цифр; довше (телефон) — лише з позначкою
    [InlineData("{}", "замовлення 12345", "12345")]
    [InlineData("{}", "замовлення 123456", "123456")]
    [InlineData("{}", "1234567", "")]
    [InlineData("{}", "…мій номер 0671234567", "")]
    [InlineData("{}", "мій номер 0671234567", "")]
    [InlineData("{}", "#671234567", "671234567")]
    [InlineData("{}", "№671234567", "671234567")]
    [InlineData("{}", "замовлення #1234567", "1234567")]
    // довге голе число — не кандидат, але рахується як ще одне число: перепитуємо, а не беремо коротке
    [InlineData("{}", "Де замовлення? Доставка на Шевченка 123, тел. 0671234567", "")]
    [InlineData("{}", "Мій телефон (555) 1234567", "")]
    [InlineData("{}", "Телефон 067 1234567", "")]
    [InlineData("{}", "Чекаю 3 дні, мій номер 0671234567", "")]
    [InlineData("{}", "замовлення #123, тел. 0671234567", "123")]       // з позначкою — однозначно
    // цифри, приліплені до літер (латиниця чи кирилиця, з будь-якого боку), — не номер
    [InlineData("{}", "Купив iPhone15", "")]
    [InlineData("{}", "Купив айфон15", "")]
    [InlineData("{}", "15шт", "")]
    [InlineData("{}", "#15шт", "")]
    [InlineData("{}", "Купив iPhone15, замовлення 123", "123")]         // приліплене не рахується як друге голе число
    [InlineData("{}", "Купив Хай15", "")]                         // розкладене «й» (и + комбінований знак)
    [InlineData("{}", "Купив Хай1234567, замовлення 123", "123")]  // приліплене довге теж не рахується
    // лише ASCII-цифри: арабсько-індійські й повноширинні відкидаються
    [InlineData("{}", "Де моє замовлення #١٢٣?", "")]
    [InlineData("{}", "Де моє замовлення ＃１２３?", "")]
    [InlineData("{}", "замовлення ١٢٣", "")]
    [InlineData("{}", "#12٣", "")]                                      // змішані цифри — не номер, і не шматок «12»
    // довжина 1–10 цифр
    [InlineData("{}", "#1234567890", "1234567890")]
    [InlineData("{}", "#12345678901", "")]
    // провідні нулі відкидаються, самі нулі — не номер
    [InlineData("{}", "#0123", "123")]
    [InlineData("{}", "#0", "")]
    [InlineData("{}", "#000", "")]
    // кілька номерів: до 3 різних, дублі (і після нормалізації) рахуються раз
    [InlineData("{}", "#555 і #123", "555,123")]
    [InlineData("{}", "#123, ще раз #123 і #0123", "123")]
    [InlineData("{}", "#1 #2 #3 #4 #5", "1,2,3")]
    // шлях аргументів — той самий валідатор
    [InlineData("{\"order_id\":\"№ 0123\"}", "", "123")]
    [InlineData("{\"order_id\":\"١٢٣\"}", "", "")]
    [InlineData("{\"order_id\":\"12345678901\"}", "", "")]
    [InlineData("{\"order_id\":0}", "Де замовлення #5?", "5")]        // невалідний аргумент — беремо з повідомлення
    [InlineData("{\"order_id\":1.5}", "", "")]
    // order_id довший за 6 цифр — лише якщо користувач написав той самий номер з позначкою
    [InlineData("{\"order_id\":\"123456\"}", "", "123456")]
    [InlineData("{\"order_id\":\"0671234567\"}", "Де замовлення? мій номер 0671234567", "")]
    [InlineData("{\"order_id\":671234567}", "Де замовлення?", "")]
    [InlineData("{\"order_id\":\"#0671234567\"}", "мій номер 0671234567", "")]  // позначка від моделі не рахується
    [InlineData("{\"order_id\":\"1234567\"}", "замовлення 123", "123")]           // відкинутий аргумент — беремо з повідомлення
    [InlineData("{\"order_id\":\"0671234567\"}", "Де замовлення №0671234567?", "671234567")]
    [InlineData("{\"order_id\":\"1234567\"}", "замовлення #7654321", "7654321")]  // інший номер з позначкою — не підтвердження
    public void ResolveOrderIds_ArgumentsThenMessage(string? args, string message, string expected)
    {
        Assert.Equal(expected, string.Join(",", ToolRunner.ResolveOrderIds(args, message).Ids));
    }

    [Fact]
    public void ResolveOrderIds_CountsDistinctIdsBeyondLimit()
    {
        Assert.Equal(2, ToolRunner.ResolveOrderIds("{}", "#1 #2 #3 #4 #5 #5").NotChecked);
        Assert.Equal(0, ToolRunner.ResolveOrderIds("{}", "#1 #2 #3 #3 #03").NotChecked);
    }

    // один валідатор для обох шляхів: однаковий рядок дає однаковий результат
    [Theory]
    [InlineData("123", "123")]
    [InlineData("#123", "123")]
    [InlineData(" № 0123 ", "123")]
    [InlineData("0000", null)]
    [InlineData("١٢٣", null)]
    [InlineData("１２３", null)]
    [InlineData("12345678901", null)]
    [InlineData("12a", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeOrderId_SharedRule(string? raw, string? expected)
    {
        Assert.Equal(expected, ToolRunner.NormalizeOrderId(raw));
    }

    // ті самі цифри в order_id і в повідомленні після # дають той самий результат.
    // довший за 6 цифр order_id без позначеного номера в повідомленні не приймається — це окремі тести нижче
    [Theory]
    [InlineData("123")]
    [InlineData("0123")]
    [InlineData("0")]
    [InlineData("١٢٣")]
    [InlineData("１２３")]
    [InlineData("123456")]
    [InlineData("12345678901")]
    public void ArgumentsAndMessage_SameRule(string digits)
    {
        var fromArgs = ToolRunner.ResolveOrderIds($"{{\"order_id\":\"{digits}\"}}", "");
        var fromMessage = ToolRunner.ResolveOrderIds("{}", "Де замовлення #" + digits + "?");
        Assert.Equal(fromMessage.Ids, fromArgs.Ids);
    }

    [Theory]
    [InlineData("#12345678901")]
    [InlineData("#123456789012345678901234567890123456789012345678901234567890")]
    public async Task TooLongNumber_RejectedAndNeverEchoed(string message)
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення " + message + "?");

        Assert.Equal(ToolOutcome.ValidationRejected, result.Outcome);
        Assert.Equal(0, store.Calls);
        Assert.DoesNotContain(message.TrimStart('#'), result.Suffix);
        Assert.DoesNotContain(message.TrimStart('#'), result.Describe());
    }

    // телефон без позначки — не номер замовлення: не перевіряється, не повертається у відповідь і не йде в лог
    [Theory]
    [InlineData("…мій номер 0671234567")]
    [InlineData("Де замовлення? мій номер 0671234567")]
    [InlineData("Купив iPhone15, де він?")]
    [InlineData("Купив Хай15, де він?")]
    public async Task BarePhoneOrGluedDigits_RejectedAndNeverEchoed(string message)
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", message);

        Assert.Equal(ToolOutcome.ValidationRejected, result.Outcome);
        Assert.Equal(0, store.Calls);
        Assert.Empty(result.Orders);
        Assert.Equal("none", result.Describe());
        Assert.DoesNotContain("671234567", result.Suffix);
        Assert.DoesNotContain("15", result.Suffix);
    }

    // довге голе число поруч із коротким: перепитуємо, нічого не перевіряємо, а цифри довгого
    // не потрапляють ні у відповідь, ні в лог. справжнє сховище: короткі 123 і 555 у ньому існують
    [Theory]
    [InlineData("Де замовлення? Доставка на Шевченка 123, тел. 0671234567", "671234567")]
    [InlineData("Де моє замовлення? Мій телефон (555) 1234567", "1234567")]
    [InlineData("Де замовлення? Телефон 067 1234567", "1234567")]
    [InlineData("Де замовлення? мій номер 0671234567", "671234567")]
    public async Task LongBareNumber_CountsAsSecondNumber_AsksAgain_NeverEchoed(string message, string digits)
    {
        var runner = new ToolRunner(new FakeOrderStore(), TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", message);

        Assert.Equal(ToolOutcome.ValidationRejected, result.Outcome);
        Assert.Empty(result.Orders);
        Assert.Equal("none", result.Describe());
        Assert.DoesNotContain(digits, result.Suffix);
        Assert.DoesNotContain("статус:", result.Suffix);
    }

    [Fact]
    public async Task LongMarkedNumber_LookedUp()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "Де замовлення #1234567?");

        Assert.Equal(1, store.Calls);
        Assert.Equal("1234567=NotFound", result.Describe());
    }

    // телефон, який модель могла б витягти в order_id, без позначеного номера в повідомленні:
    // не перевіряється, не повертається у відповідь і не йде в лог
    [Theory]
    [InlineData("{\"order_id\":\"0671234567\"}", "Де замовлення? мій номер 0671234567")]
    [InlineData("{\"order_id\":671234567}", "Де замовлення?")]
    [InlineData("{\"order_id\":\"#0671234567\"}", "Де замовлення? мій номер 0671234567")]
    public async Task LongArgumentWithoutMarkedMatch_IgnoredAndNeverEchoed(string args, string message)
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", args, message);

        Assert.Equal(ToolOutcome.ValidationRejected, result.Outcome);
        Assert.Equal(0, store.Calls);
        Assert.Equal("none", result.Describe());
        Assert.DoesNotContain("671234567", result.Suffix);
        Assert.DoesNotContain("671234567", result.Describe());
    }

    [Fact]
    public async Task LongArgument_MarkedInMessage_Accepted()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{\"order_id\":\"0671234567\"}", "Де замовлення №0671234567?");

        Assert.Equal(1, store.Calls);
        Assert.Equal("671234567=NotFound", result.Describe());
    }

    // ~100 тис. номерів в одному повідомленні: розбір лінійний, а у відповідь іде лише кількість
    [Fact]
    public async Task HugeIdList_FastAndOnlyCounted()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));
        var message = string.Join(" ", Enumerable.Range(1, 100_000).Select(i => "#" + i));
        var sw = Stopwatch.StartNew();

        var result = await runner.RunAsync("lookup_order", "{}", message);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"100k ids took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(3, store.Calls);
        Assert.Equal(99_997, result.NotChecked);
        Assert.Contains("ще 99997", result.Suffix);
    }

    [Fact]
    public async Task SeveralIds_EachGetsOwnStatus()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "Де замовлення #555 і #123?");

        Assert.Equal(2, store.Calls);
        Assert.Equal(ToolOutcome.Ok, result.Outcome);
        Assert.Equal(" (статуси: #555 — не знайдено; #123 — в дорозі)", result.Suffix);
        Assert.Equal("555=NotFound, 123=Ok", result.Describe());
    }

    [Fact]
    public async Task DuplicateIds_LookedUpOnce_SingleForm()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "#123, #123 і #0123");

        Assert.Equal(1, store.Calls);
        Assert.Equal(" (статус: в дорозі)", result.Suffix);
    }

    [Fact]
    public async Task MoreThanThreeIds_FirstThreeChecked_RestMentionedNotEchoed()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "#123 #2 #3 #4444 #5555");

        Assert.Equal(3, store.Calls);
        Assert.Equal(2, result.NotChecked);
        Assert.Contains("#123 — в дорозі", result.Suffix);
        Assert.Contains("ще 2", result.Suffix);
        Assert.DoesNotContain("4444", result.Suffix);
        Assert.DoesNotContain("5555", result.Suffix);
    }

    [Fact]
    public async Task NumeroSignAndBareNumber_LookedUp()
    {
        var runner = new ToolRunner(new FakeOrderStore(), TimeSpan.FromSeconds(2));
        foreach (var message in new[] { "Де моє замовлення №123?", "Де моє замовлення 123?", "Де моє замовлення # 123?" })
        {
            var result = await runner.RunAsync("lookup_order", "{}", message);
            Assert.Equal(" (статус: в дорозі, доставка завтра)", result.Suffix);
        }
    }

    [Fact]
    public async Task NonAsciiDigits_ValidationRejected()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #١٢٣?");

        Assert.Equal(ToolOutcome.ValidationRejected, result.Outcome);
        Assert.Equal(0, store.Calls);
        Assert.Contains("№123", result.Suffix);
    }

    [Fact]
    public async Task MissingOrderNumber_ValidationRejected_NotExecuted()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення?");

        Assert.Equal(ToolOutcome.ValidationRejected, result.Outcome);
        Assert.Equal(0, store.Calls);
        Assert.DoesNotContain("статус:", result.Suffix);
    }

    [Fact]
    public async Task LookupOrder_AppendsStatus()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var found = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #123?");
        var missing = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #999?");

        Assert.Equal(ToolOutcome.Ok, found.Outcome);
        Assert.Equal(" (статус: в дорозі)", found.Suffix);
        Assert.Equal(ToolOutcome.NotFound, missing.Outcome);
        Assert.Contains("статус:", missing.Suffix);
    }

    [Fact]
    public async Task FakeStore_KnowsOrder123()
    {
        var runner = new ToolRunner(new FakeOrderStore(), TimeSpan.FromSeconds(2));
        var result = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #123?");
        Assert.Equal(ToolOutcome.Ok, result.Outcome);
        Assert.StartsWith(" (статус: ", result.Suffix);
    }

    [Fact]
    public async Task CreateTicket_NotExecuted()
    {
        var store = new CountingStore();
        var runner = new ToolRunner(store, TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("create_ticket", "{}", "Поверніть гроші за #123");

        Assert.Equal(ToolOutcome.NotExecuted, result.Outcome);
        Assert.Equal("", result.Suffix);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task SlowTool_TimesOutWithinBudget()
    {
        var runner = new ToolRunner(new SlowStore(), TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();

        var result = await runner.RunAsync("lookup_order", "{}", "Де моє замовлення #123?");
        sw.Stop();

        Assert.Equal(ToolOutcome.Timeout, result.Outcome);
        Assert.DoesNotContain("статус:", result.Suffix);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"timeout took {sw.ElapsedMilliseconds} ms");
    }

    // один бюджет на весь виклик: три повільні номери не займають утричі більше часу
    [Fact]
    public async Task ThreeSlowIds_ShareOneBudget()
    {
        var runner = new ToolRunner(new SlowStore(), TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();

        var result = await runner.RunAsync("lookup_order", "{}", "#1 #2 #3");
        sw.Stop();

        Assert.Equal(ToolOutcome.Timeout, result.Outcome);
        Assert.All(result.Orders, o => Assert.Equal(ToolOutcome.Timeout, o.Outcome));
        Assert.True(sw.ElapsedMilliseconds < 1400, $"3 ids took {sw.ElapsedMilliseconds} ms, budget 500 ms");
    }

    // швидкий номер встигає, повільний — ні: кожен зі своїм результатом
    private sealed class MixedStore : IOrderStore
    {
        public async Task<string?> GetStatusAsync(string orderId, CancellationToken ct)
        {
            if (orderId == "2")
                await Task.Delay(TimeSpan.FromSeconds(10));
            return "в дорозі";
        }
    }

    [Fact]
    public async Task PartialTimeout_FastIdStillReported()
    {
        var runner = new ToolRunner(new MixedStore(), TimeSpan.FromMilliseconds(200));

        var result = await runner.RunAsync("lookup_order", "{}", "#1 і #2");

        Assert.Equal(ToolOutcome.Timeout, result.Outcome);
        Assert.Equal("1=Ok, 2=Timeout", result.Describe());
        Assert.StartsWith(" (статуси: #1 — в дорозі; #2 — не вдалося перевірити вчасно)", result.Suffix);
    }

    private sealed class ThrowingStore : IOrderStore
    {
        public Task<string?> GetStatusAsync(string orderId, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
    }

    [Fact]
    public async Task ThrowingStore_Failed_NeverThrows()
    {
        var runner = new ToolRunner(new ThrowingStore(), TimeSpan.FromSeconds(2));
        var result = await runner.RunAsync("lookup_order", "{}", "#123");
        Assert.Equal(ToolOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("статус:", result.Suffix);
    }

    // CancellationTokenSource кинув би на такий бюджет — RunAsync не має
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.MaxValue)]
    public async Task BadTimeout_FallsBackToDefault_NeverThrows(double ms)
    {
        var timeout = ms == double.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(ms);
        var runner = new ToolRunner(new CountingStore(), timeout);
        var result = await runner.RunAsync("lookup_order", "{}", "#123");
        Assert.Equal(ToolOutcome.Ok, result.Outcome);
    }
}

public class SingleFlightTests
{
    private static ModelCall Ok(string text) => new(new CachedReply(text, null, null), 200, true, 10, 5);
    private static ModelCall Failure() => new(new CachedReply("Сервіс тимчасово недоступний.", null, null), 500, false, 0, 0);

    [Fact]
    public async Task TwentyParallelMisses_OneModelCall()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ModelCall> Model()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return Ok("відповідь");
        }

        var requests = Enumerable.Range(0, 20).Select(_ => Task.Run(() => cache.GetOrCallAsync("k", Model))).ToArray();
        await Task.Delay(300);  // дати всім двадцяти дійти до очікування
        gate.SetResult();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, calls);
        Assert.Single(results, r => r.Source == CacheSource.Miss);
        Assert.All(results, r => Assert.Equal("відповідь", r.Reply.Content));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(19, cache.Hits);
        Assert.Equal(0, cache.InFlight);
        Assert.True(cache.TryGet("k", out _));  // результат ліг у кеш до того, як in-flight запис прибрали
    }

    [Fact]
    public async Task Failure_SharedWithWaiters_NotCached()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ModelCall> Model()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return Failure();
        }

        // без Task.Delay: після LeaderAndWaiters усі чотири чекачі гарантовано приєдналися до виклику лідера
        var (leader, waiters) = LeaderAndWaiters(cache, Model, 4);
        Assert.Equal(1, cache.InFlight);
        gate.SetResult();

        var first = await leader;
        var results = await Task.WhenAll(waiters);

        Assert.Equal(1, calls);  // чекачі не повторюють виклик
        Assert.Equal(CacheSource.Miss, first.Source);
        Assert.Equal(500, first.Call!.Status);
        Assert.All(results, r => Assert.Equal(500, r.Call!.Status));
        Assert.All(results, r => Assert.Equal(CacheSource.JoinedFailed, r.Source));
        Assert.Equal(0, cache.InFlight);

        // збій не закешовано: наступний запит знову йде в модель
        var next = await cache.GetOrCallAsync("k", () => Task.FromResult(Ok("ожило")));
        Assert.Equal(CacheSource.Miss, next.Source);
        Assert.Equal("ожило", next.Reply.Content);
    }

    [Fact]
    public async Task Success_InFlightRemoved_NextIsHit()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var first = await cache.GetOrCallAsync("k", () => Task.FromResult(Ok("a")));
        Assert.Equal(CacheSource.Miss, first.Source);
        Assert.Equal(0, cache.InFlight);

        var second = await cache.GetOrCallAsync("k", () => throw new InvalidOperationException("не мало викликатись"));
        Assert.Equal(CacheSource.Hit, second.Source);
        Assert.Null(second.Call);
    }

    [Fact]
    public async Task Exception_InFlightRemoved_NextCallsModelAgain()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCallAsync("k", () => throw new InvalidOperationException("boom")));
        Assert.Equal(0, cache.InFlight);

        var next = await cache.GetOrCallAsync("k", () => Task.FromResult(Ok("b")));
        Assert.Equal(CacheSource.Miss, next.Source);
    }

    // відключення лідера скасовує лише його очікування, а не спільний виклик
    [Fact]
    public async Task LeaderCancelled_SharedCallCompletesForOthers()
    {
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ModelCall> Model()
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            await gate.Task;
            return Ok("спільна");
        }

        using var leaderAborted = new CancellationTokenSource();
        var leader = cache.GetOrCallAsync("k", Model, leaderAborted.Token);
        await started.Task;
        var follower = cache.GetOrCallAsync("k", Model);

        leaderAborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        gate.SetResult();

        var result = await follower;
        Assert.Equal(CacheSource.Joined, result.Source);
        Assert.Equal("спільна", result.Reply.Content);
        Assert.Equal(1, calls);
        Assert.Equal(0, cache.InFlight);
        Assert.True(cache.TryGet("k", out _));
    }

    // лідер і N чекачів, усі вже в очікуванні до того, як модель відповість.
    // без Task.Run і без Task.Delay: GetOrCallAsync синхронно доходить до свого await (лідер — до gate,
    // чекач — до очікування in-flight задачі), тож після циклу всі N гарантовано приєдналися.
    private static (Task<CacheLookup> Leader, Task<CacheLookup>[] Waiters) LeaderAndWaiters(
        ResponseCache cache, Func<Task<ModelCall>> model, int waiters)
    {
        var leader = cache.GetOrCallAsync("k", model);
        var joined = Enumerable.Range(0, waiters).Select(_ => cache.GetOrCallAsync("k", model)).ToArray();
        return (leader, joined);
    }

    [Fact]
    public async Task FailedJoin_CountedAsMiss_NotHit()
    {
        const int n = 4;
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ModelCall> Model()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return Failure();
        }

        var (leader, waiters) = LeaderAndWaiters(cache, Model, n);
        Assert.Equal(1, cache.InFlight);
        gate.SetResult();

        Assert.Equal(CacheSource.Miss, (await leader).Source);
        var results = await Task.WhenAll(waiters);
        Assert.All(results, r => Assert.Equal(CacheSource.JoinedFailed, r.Source));
        Assert.All(results, r => Assert.Equal(500, r.Call!.Status));
        Assert.Equal(1, calls);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(n + 1, cache.Misses);
    }

    [Fact]
    public async Task ThrowingLeader_WaitersCountedAsMisses()
    {
        const int n = 3;
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModelCall> Model()
        {
            await gate.Task;
            throw new InvalidOperationException("boom");
        }

        var (leader, waiters) = LeaderAndWaiters(cache, Model, n);
        gate.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => leader);
        foreach (var w in waiters)
            await Assert.ThrowsAsync<InvalidOperationException>(() => w);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(n + 1, cache.Misses);
        Assert.Equal(0, cache.InFlight);
    }

    [Fact]
    public async Task SuccessfulJoin_CountedAsHit()
    {
        const int n = 4;
        var cache = new ResponseCache(TimeSpan.FromMinutes(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModelCall> Model()
        {
            await gate.Task;
            return Ok("спільна");
        }

        var (leader, waiters) = LeaderAndWaiters(cache, Model, n);
        gate.SetResult();

        Assert.Equal(CacheSource.Miss, (await leader).Source);
        var results = await Task.WhenAll(waiters);
        Assert.All(results, r => Assert.Equal(CacheSource.Joined, r.Source));
        Assert.Equal(n, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }
}
