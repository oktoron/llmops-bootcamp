// кеш відповідей (W3). Окремий клас, а не локальні функції Program.cs: інакше юніт-тест його не побачить.
// Зберігається в пам'яті процесу: Redis є лише в профілі advanced, а обов'язковий шлях не може від нього залежати.
// ОБМЕЖЕННЯ: кеш живе до перезапуску сервісу, а прострочені записи прибираються лише при наступному зверненні
// до того самого ключа — розміру кеш не обмежує.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// одне повідомлення для моделі. JsonPropertyName — бо gateway чекає role/content у нижньому регістрі.
public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

// що кладемо в кеш: вихід моделі (текст + tool call), а не фінальну відповідь.
// інструмент виконується заново на кожному запиті, тож статус замовлення з кешу не застаріває.
public sealed record CachedReply(string Content, string? ToolName, string? ToolArguments);

// результат одного виклику моделі: і те, що піде в кеш, і те, що потрібне для рядка в requests.
// UsageParsed = false — збій (вибачення замість відповіді, вартість невідома).
// ServedModel — модель, що фактично відповіла (null — не відповіла жодна, деградація);
// Hops — скільки разів ланцюжок fallback перемикався (W4, див. ModelClient).
public sealed record ModelCall(CachedReply Reply, int Status, bool UsageParsed, int PromptTokens, int CompletionTokens,
    string? ServedModel = null, int Hops = 0);

// Hit — відповідь з кешу; Miss — цей запит сам викликав модель;
// Joined — однаковий запит уже був у дорозі, і цей дочекався його вдалого результату (власного виклику моделі не було);
// JoinedFailed — те саме, але спільний виклик не вдався: рахується як miss, а не hit.
public enum CacheSource { Hit, Miss, Joined, JoinedFailed }

// Call — виклик моделі, з якого прийшла відповідь (null — відповідь з кешу)
public sealed record CacheLookup(CacheSource Source, CachedReply Reply, ModelCall? Call);

public sealed class ResponseCache
{
    private readonly ConcurrentDictionary<string, (CachedReply Reply, DateTimeOffset ExpiresAt)> _entries = new();
    // single-flight: ключ -> виклик моделі, який уже в дорозі. окремо від _entries, бо це ще не відповідь
    private readonly ConcurrentDictionary<string, Task<Flight>> _inFlight = new();
    private sealed record Flight(CachedReply Reply, ModelCall? Call);
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;
    private long _hits, _misses;

    // now — годинник, який тест може підмінити, щоб перевірити TTL без очікування
    public ResponseCache(TimeSpan ttl, Func<DateTimeOffset>? now = null)
    {
        _ttl = ttl;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    // скільки викликів моделі зараз у дорозі (для тестів: після завершення має бути 0)
    public int InFlight => _inFlight.Count;

    // ключ — SHA-256 від моделі, версії промпта і ВСЬОГО масиву messages, що піде в модель
    // (системне повідомлення в ньому — це текст промпта). Зміна моделі, версії чи тексту промпта —
    // інший ключ, тож відкат промпта (W1) не віддає стару відповідь. JSON замість склеювання рядків:
    // межі між полями однозначні, "a"+"bc" і "ab"+"c" не дадуть один ключ.
    public static string Key(string model, string promptVersion, IReadOnlyList<ChatMessage> messages)
    {
        var json = JsonSerializer.Serialize(new { model, prompt_version = promptVersion, messages });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    // що можна класти в кеш: лише успішна (200) розпарсена відповідь зі змістом.
    // вибачення з catch і помилки gateway не кешуємо ніколи. create_ticket — дія з побічним ефектом
    // (W4, HITL): такий вихід моделі теж не кешуємо, щоб повтор ішов у модель, а не в кеш.
    // сміття ("...", порожньо, лише пробіли — W5, див. ReplyQuality.IsGarbage) — теж ні: та сама перевірка,
    // що й у /chat, тож повтор питання знову йде в модель, а не віддає сміття з кешу ще TTL.
    public static bool IsCacheable(int status, bool parsed, CachedReply reply) =>
        status == 200 && parsed && !ReplyQuality.IsGarbage(reply.Content) && reply.ToolName != "create_ticket";

    // hit і miss рахуються окремо, атомарно: /chat обслуговує паралельні запити.
    // прострочений запис — це miss: його видаляємо, і після виклику моделі ляже свіжий.
    public bool TryGet(string key, [NotNullWhen(true)] out CachedReply? reply)
    {
        var found = TryGetFresh(key, out reply);
        if (found)
            Interlocked.Increment(ref _hits);
        else
            Interlocked.Increment(ref _misses);
        return found;
    }

    public void Set(string key, CachedReply reply) => _entries[key] = (reply, _now() + _ttl);

    // вдалий виклик моделі: 200 і розпарсений usage. те саме правило, за яким /chat пише cost_usd = NULL
    public static bool Succeeded(ModelCall call) => call.Status == 200 && call.UsageParsed;

    // кеш + single-flight. hit — віддаємо з кешу. miss — перший запит (лідер) викликає модель,
    // а однакові запити, що прийшли, поки виклик у дорозі, чекають його результат замість власного виклику.
    // очікування рахуємо, коли результат уже відомий: вдалий виклик — hit (Joined), збій, виняток чи
    // скасоване очікування — miss (JoinedFailed). так hits + misses = кількість запитів, і 503 на 20 однакових
    // запитів дає 0 hits і 20 misses, а не 19 і 1.
    // збій лідера чекачі отримують теж (без повтору), але в кеш він не потрапляє (IsCacheable),
    // тож наступний запит після збою знову піде в модель. Те саме з відповіддю після fallback (Hops > 0).
    // waitCt скасовує лише очікування цього запиту, а не сам виклик: спільний виклик доходить до кінця
    // і для інших, і для кешу, навіть якщо клієнт лідера відключився.
    // callModel не має кидати; якщо все ж кине — виняток отримають усі, хто чекав, а in-flight запис прибирається.
    public async Task<CacheLookup> GetOrCallAsync(string key, Func<Task<ModelCall>> callModel, CancellationToken waitCt = default)
    {
        if (TryGetFresh(key, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return new CacheLookup(CacheSource.Hit, cached, null);
        }

        // RunContinuationsAsynchronously: продовження чекачів не виконуються в потоці лідера
        var mine = new TaskCompletionSource<Flight>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flight = _inFlight.GetOrAdd(key, mine.Task);
        if (flight != mine.Task)
        {
            Flight shared;
            try
            {
                shared = await flight.WaitAsync(waitCt);
            }
            catch
            {
                // виклик кинув або очікування скасоване — відповіді цей запит не отримав
                Interlocked.Increment(ref _misses);
                throw;
            }
            // Call == null — лідер сам знайшов відповідь у кеші (див. RunFlightAsync), це вдалий результат
            if (shared.Call is { } sharedCall && !Succeeded(sharedCall))
            {
                Interlocked.Increment(ref _misses);
                return new CacheLookup(CacheSource.JoinedFailed, shared.Reply, sharedCall);
            }
            Interlocked.Increment(ref _hits);
            return new CacheLookup(CacheSource.Joined, shared.Reply, shared.Call);
        }

        // виклик іде окремою задачею, яку не скасовує waitCt лідера
        _ = RunFlightAsync(key, callModel, mine);
        var own = await mine.Task.WaitAsync(waitCt);
        return new CacheLookup(own.Call == null ? CacheSource.Hit : CacheSource.Miss, own.Reply, own.Call);
    }

    // ніколи не кидає: результат або виняток передається через tcs
    private async Task RunFlightAsync(string key, Func<Task<ModelCall>> callModel, TaskCompletionSource<Flight> tcs)
    {
        Flight? result = null;
        Exception? error = null;
        try
        {
            // між промахом лідера і його записом у _inFlight попередній лідер міг уже покласти відповідь у кеш
            // і прибрати свій запис — перевіряємо ще раз, щоб не йти в модель удруге
            if (TryGetFresh(key, out var cached))
            {
                Interlocked.Increment(ref _hits);
                result = new Flight(cached, null);
            }
            else
            {
                Interlocked.Increment(ref _misses);
                var call = await callModel();
                // відповідь після fallback (Hops > 0) не кешуємо: інакше під ключем основної моделі
                // ще TTL після відновлення лежала б відповідь запасної
                if (call.Hops == 0 && IsCacheable(call.Status, call.UsageParsed, call.Reply))
                    Set(key, call.Reply);
                result = new Flight(call.Reply, call);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // порядок важливий: спершу запис у кеш (вище), потім прибрати in-flight — тоді новий запит знайде
        // або кеш, або виклик у дорозі, але не «нічого». прибираємо саме свою пару, щоб не зачепити нового лідера.
        _inFlight.TryRemove(new KeyValuePair<string, Task<Flight>>(key, tcs.Task));
        if (result != null)
            tcs.SetResult(result);
        else
            tcs.SetException(error ?? new InvalidOperationException("model call produced no result"));
    }

    private bool TryGetFresh(string key, [NotNullWhen(true)] out CachedReply? reply)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > _now())
            {
                reply = entry.Reply;
                return true;
            }
            _entries.TryRemove(new KeyValuePair<string, (CachedReply, DateTimeOffset)>(key, entry));
        }
        reply = null;
        return false;
    }
}
