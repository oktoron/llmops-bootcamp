// HITL (W4): черга підтверджень для незворотних дій (create_ticket). Окремий клас — щоб юніт-тест міг
// підставити ручний годинник (TimeProvider) і перевірити TTL без sleep, а гонки — паралельними викликами.
// черга в пам'яті процесу: перезапуск сервісу її губить (свідоме обмеження, без змін схеми БД).

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

// стани заявки. кінцеві — done (approve), rejected (reject), expired (TTL); з них назад немає.
// executing — проміжний «захоплений» стан: лише той, хто CAS-ом перевів pending -> executing, пише поля
// результату й публікує кінцевий стан (Volatile.Write). якщо тікет не створився — executing -> pending.
public enum ApprovalState { Pending = 0, Executing = 1, Done = 2, Expired = 3, Rejected = 4 }

// сховище тікетів. інтерфейс — щоб тест підставив збій і перевірив відкат заявки в pending
public interface ITicketStore
{
    // text — уже замаскований текст заявки
    string Create(string action, string text);
}

// фейкове сховище тікетів: номери TCK-1001, TCK-1002, … без таблиці в БД. викликається ЛИШЕ з approve.
public sealed class TicketStore : ITicketStore
{
    private int _last = 1000;
    private int _created;

    // скільки тікетів створено — для тестів «рівно один тікет на заявку»
    public int Created => Volatile.Read(ref _created);

    // фейк тексту не зберігає, справжнє сховище записало б його в тікет
    public string Create(string action, string text)
    {
        Interlocked.Increment(ref _created);
        return $"TCK-{Interlocked.Increment(ref _last)}";
    }
}

public sealed class Approval
{
    internal int State;  // ApprovalState як int — для Interlocked

    public required string Id { get; init; }
    public required string Action { get; init; }
    // текст повідомлення, вже замаскований guardrails (email -> [email]). сирого тексту черга не бачить
    public required string Text { get; init; }
    // ключ дедуплікації (create_ticket:123,555) або null — заявка без номера з #/№ не дедуплікується
    public string? DedupKey { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    // пишуться в стані executing, до Volatile.Write кінцевого стану; читаються після Volatile.Read стану
    internal string? DecidedBy;          // хто підтвердив або відхилив
    internal string? Result;             // номер тікета (done)
    internal string? Reason;             // причина відхилення (rejected)
    internal DateTimeOffset? FinishedAt; // коли стала done / rejected / expired

    public ApprovalState Status => (ApprovalState)Volatile.Read(ref State);
}

// GET /approvals — форма з уроку L08: окремий список на кожен стан, найновіші першими
public sealed record PendingApproval(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record DoneApproval(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("approved_by")] string? ApprovedBy,
    [property: JsonPropertyName("approved_at")] DateTimeOffset ApprovedAt);

public sealed record RejectedApproval(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("rejected_by")] string? RejectedBy,
    [property: JsonPropertyName("rejected_at")] DateTimeOffset RejectedAt,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record ExpiredApproval(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expired_at")] DateTimeOffset ExpiredAt);

public sealed record ApprovalsView(
    [property: JsonPropertyName("pending")] IReadOnlyList<PendingApproval> Pending,
    [property: JsonPropertyName("done")] IReadOnlyList<DoneApproval> Done,
    [property: JsonPropertyName("rejected")] IReadOnlyList<RejectedApproval> Rejected,
    [property: JsonPropertyName("expired")] IReadOnlyList<ExpiredApproval> Expired);

// результат постановки в чергу: Created = false — дубль уже наявної заявки (той самий id).
// PreviousExpiredId — попередня заявка з тим самим ключем протермінувалась: користувачу треба про це сказати
public sealed record EnqueueResult(Approval Approval, bool Created, string? PreviousExpiredId = null);

public sealed class ApprovalQueue
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(900);
    // скільки завершених заявок (done/rejected/expired) тримаємо для консолі; старіші викидаємо разом із ключами
    public const int MaxFinished = 100;
    // текст у консолі обрізаємо: оператору досить побачити суть
    public const int MaxMessageChars = 200;
    // мітка «хто» і причина відхилення — теж обмежені
    public const int MaxLabelChars = 100;
    public const int MaxReasonChars = 200;

    private readonly ConcurrentDictionary<string, Approval> _items = new();
    private readonly ConcurrentDictionary<string, Approval> _byKey = new();
    // ключ -> id останньої протермінованої заявки, про яку користувач ще не дізнався. обмежений: запис
    // зникає, щойно про нього сказали (нова заявка з цим ключем), або разом із заявкою в Prune
    private readonly ConcurrentDictionary<string, string> _expiredByKey = new();
    private readonly ITicketStore _tickets;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _pruneLock = new();
    private long _expired;

    public TimeSpan Ttl { get; }

    // скільки заявок протермінувалось з моменту старту процесу (лічильник у пам'яті, як ModelClient.FallbackEvents).
    // на відміну від списку expired у GET /approvals, обрізання до MaxFinished його не зменшує
    public long ExpiredCount => Interlocked.Read(ref _expired);

    public ApprovalQueue(ITicketStore tickets, TimeSpan ttl, TimeProvider time, ILogger? log = null)
    {
        _tickets = tickets;
        Ttl = ttl > TimeSpan.Zero ? ttl : DefaultTtl;
        _time = time;
        _log = log;
    }

    // ключ дедуплікації: дія + відсортовані різні номери замовлень з позначкою #/№ (див. ToolRunner.MarkedOrderIds).
    // немає таких номерів або їх понад ліміт — ключа немає: інакше різні заявки злилися б в одну
    public static string? DedupKeyFor(string action, string maskedText)
    {
        var ids = ToolRunner.MarkedOrderIds(maskedText);
        if (ids.Count == 0 || ids.Count > ToolRunner.MaxOrderIds)
            return null;
        return action + ":" + string.Join(",", ids);
    }

    // обрізає до max символів UTF-16, не розриваючи сурогатну пару (емодзі): інакше в JSON піде зламаний текст
    public static string Clip(string s, int max)
    {
        if (s.Length <= max)
            return s;
        return s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)];
    }

    // єдиний вхід у чергу: вердикт guardrails, а не рядок. перевантаження з рядком — private, тож випадково
    // передати сирий body.Message не вийде; хіба що свідомо зібрати фальшивий GuardVerdict.
    // у заявку йде лише guard.Masked; заблокований запит до черги не доходить
    public EnqueueResult Enqueue(string action, GuardVerdict guard)
    {
        if (guard.Blocked)
            throw new ArgumentException("blocked request must not reach the approval queue", nameof(guard));
        return Enqueue(action, guard.Masked);
    }

    // дописка до відповіді чату (формулювання з уроку L08). номера тікета тут немає ніколи: його створює лише approve
    public static string ReplySuffix(EnqueueResult r)
    {
        var id = r.Approval.Id;
        if (r.PreviousExpiredId is { } prev)
            return $" Попередню заявку не опрацювали вчасно ({prev}), створено нову (очікує підтвердження оператора, id={id})";
        // дубль виконаної заявки: не стверджуємо, що підтвердили саме цей запит
        if (!r.Created && r.Approval.Status == ApprovalState.Done)
            return $" (заявку з цього замовлення вже оброблено оператором, id={id})";
        return $" (очікує підтвердження оператора, id={id})";
    }

    // ставить заявку в чергу (кількість pending не обмежена — rate limiting поза межами W4). maskedText — уже
    // замаскований guard.Masked.
    // дубль (той самий ключ), поки заявка pending/executing або done не довше за TTL, — повертає наявну.
    private EnqueueResult Enqueue(string action, string maskedText)
    {
        var now = _time.GetUtcNow();
        var key = DedupKeyFor(action, maskedText);
        var candidate = NewApproval(action, maskedText, key, now);
        if (key == null)
        {
            Add(candidate);
            return new EnqueueResult(candidate, true);
        }

        while (true)
        {
            // GetOrAdd зі значенням (не фабрикою): переможець один, решта отримують його ж
            var existing = _byKey.GetOrAdd(key, candidate);
            if (ReferenceEquals(existing, candidate))
            {
                try { Add(candidate); }
                catch { _byKey.TryRemove(KeyValuePair.Create(key, candidate)); throw; }
                // про протерміновану попередню заявку кажемо один раз — тому, хто створив нову
                _expiredByKey.TryRemove(key, out var previousExpired);
                return new EnqueueResult(candidate, true, previousExpired);
            }
            ExpireIfDue(existing, now);
            if (IsLive(existing, now))
                return new EnqueueResult(existing, false);
            // ключ належить мертвій заявці (expired або done понад TTL) — звільняємо, лише якщо там досі вона
            _byKey.TryRemove(KeyValuePair.Create(key, existing));
        }
    }

    // approve: лише одна з паралельних спроб переводить pending -> executing і створює тікет.
    // null — немає такої заявки, вона вже оброблена/обробляється або протермінована (ендпоінт віддає 404).
    // збій сховища тікетів: заявка повертається в pending, виняток летить до ендпоінта (503)
    public DoneApproval? Approve(string id, string? approvedBy)
    {
        var now = _time.GetUtcNow();
        if (!_items.TryGetValue(id, out var a) || !TryClaim(a, now))
            return null;

        string ticket;
        try
        {
            ticket = _tickets.Create(a.Action, a.Text);
        }
        catch
        {
            // тікет не створився — заявка знову чекає, а не застрягає в executing
            Volatile.Write(ref a.State, (int)ApprovalState.Pending);
            throw;
        }
        a.DecidedBy = approvedBy;
        a.Result = ticket;
        a.FinishedAt = now;
        Volatile.Write(ref a.State, (int)ApprovalState.Done);
        Prune();
        return ToDone(a);
    }

    // reject: те саме захоплення, що й в approve, але інструмент не викликається. відхилена заявка звільняє
    // ключ дедуплікації — наступна заявка з цього замовлення знову дійде до оператора
    public RejectedApproval? Reject(string id, string? rejectedBy, string? reason)
    {
        var now = _time.GetUtcNow();
        if (!_items.TryGetValue(id, out var a) || !TryClaim(a, now))
            return null;

        a.DecidedBy = rejectedBy;
        a.Reason = reason;
        a.FinishedAt = now;
        Volatile.Write(ref a.State, (int)ApprovalState.Rejected);
        if (a.DedupKey != null)
            _byKey.TryRemove(KeyValuePair.Create(a.DedupKey, a));
        Prune();
        return ToRejected(a);
    }

    // знімок черги для GET /approvals. перед цим — ліниве протермінування і прибирання старих завершених.
    // executing (мікросекунди між захопленням і результатом) не показуємо: дії над нею вже немає
    public ApprovalsView List()
    {
        var now = _time.GetUtcNow();
        foreach (var a in _items.Values)
            ExpireIfDue(a, now);
        Prune();

        var all = _items.Values.Select(a => (A: a, S: a.Status)).ToList();  // стан читаємо раз на заявку
        IEnumerable<Approval> In(ApprovalState s) => all.Where(x => x.S == s).Select(x => x.A)
            .OrderByDescending(a => a.FinishedAt ?? a.CreatedAt).ThenByDescending(a => a.Id, StringComparer.Ordinal);
        return new ApprovalsView(
            In(ApprovalState.Pending).Select(a => new PendingApproval(a.Id, a.Action, MessageOf(a), a.CreatedAt)).ToList(),
            In(ApprovalState.Done).Select(ToDone).ToList(),
            In(ApprovalState.Rejected).Select(ToRejected).ToList(),
            In(ApprovalState.Expired).Select(a => new ExpiredApproval(a.Id, a.Action, a.CreatedAt, a.FinishedAt ?? a.CreatedAt)).ToList());
    }

    public ApprovalState? StateOf(string id) => _items.TryGetValue(id, out var a) ? a.Status : null;

    private Approval NewApproval(string action, string text, string? key, DateTimeOffset now) => new()
    {
        Id = NewId(),
        Action = action,
        Text = text,
        DedupKey = key,
        CreatedAt = now,
    };

    // id у URL без кодування: «apr_» + 12 hex-символів з криптогенератора
    private static string NewId() => "apr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private void Add(Approval a)
    {
        // збіг 48-бітних id практично неможливий; якщо станеться — помилка запиту, а не перезапис чужої заявки
        if (!_items.TryAdd(a.Id, a))
            throw new InvalidOperationException("approval id collision");
    }

    // pending (і ще не протермінована) -> executing. true — цей виклик виграв і веде заявку далі
    private bool TryClaim(Approval a, DateTimeOffset now) =>
        !ExpireIfDue(a, now)
        && Interlocked.CompareExchange(ref a.State, (int)ApprovalState.Executing, (int)ApprovalState.Pending) == (int)ApprovalState.Pending;

    // pending старша за TTL -> expired (через executing, як і решта переходів). true — заявка протермінована
    private bool ExpireIfDue(Approval a, DateTimeOffset now)
    {
        if (now - a.CreatedAt >= Ttl
            && Interlocked.CompareExchange(ref a.State, (int)ApprovalState.Executing, (int)ApprovalState.Pending) == (int)ApprovalState.Pending)
        {
            a.FinishedAt = now;
            // запис — ДО публікації expired і звільнення ключа: Enqueue, що побачив expired і створив нову заявку,
            // гарантовано забере цей запис (інакше нового не попередили б, а запис дістався б комусь пізніше)
            if (a.DedupKey != null)
                _expiredByKey[a.DedupKey] = a.Id;
            Volatile.Write(ref a.State, (int)ApprovalState.Expired);
            if (a.DedupKey != null)
                _byKey.TryRemove(KeyValuePair.Create(a.DedupKey, a));
            Interlocked.Increment(ref _expired);
            // один рядок на протермінування: id, дія, вік. тексту заявки в лозі немає. лог не ламає запит
            try
            {
                _log?.LogInformation("approval {ApprovalId} ({Action}) expired after {AgeSeconds} s without a decision",
                    a.Id, a.Action, (long)(now - a.CreatedAt).TotalSeconds);
            }
            catch { }
        }
        return a.Status == ApprovalState.Expired;
    }

    // чи заявка ще «тримає» свій ключ: чекає, обробляється або виконана не довше за TTL тому
    private bool IsLive(Approval a, DateTimeOffset now) => a.Status switch
    {
        ApprovalState.Pending or ApprovalState.Executing => true,
        ApprovalState.Done => a.FinishedAt is { } f && now - f < Ttl,
        _ => false
    };

    // лишає MaxFinished найсвіжіших завершених; разом із заявкою звільняються її ключ і запис про протермінування
    private void Prune()
    {
        lock (_pruneLock)
        {
            var finished = _items.Values
                .Where(a => a.Status is ApprovalState.Done or ApprovalState.Expired or ApprovalState.Rejected)
                .OrderByDescending(a => a.FinishedAt ?? a.CreatedAt)
                .Skip(MaxFinished)
                .ToList();
            foreach (var a in finished)
            {
                _items.TryRemove(KeyValuePair.Create(a.Id, a));
                if (a.DedupKey != null)
                {
                    _byKey.TryRemove(KeyValuePair.Create(a.DedupKey, a));
                    _expiredByKey.TryRemove(KeyValuePair.Create(a.DedupKey, a.Id));
                }
            }
        }
    }

    private static string MessageOf(Approval a) =>
        a.Text.Length > MaxMessageChars ? Clip(a.Text, MaxMessageChars) + "…" : a.Text;

    private static DoneApproval ToDone(Approval a) =>
        new(a.Id, a.Action, a.Result ?? "", a.DecidedBy, a.FinishedAt ?? a.CreatedAt);

    private static RejectedApproval ToRejected(Approval a) =>
        new(a.Id, a.Action, a.DecidedBy, a.FinishedAt ?? a.CreatedAt, a.Reason);
}
