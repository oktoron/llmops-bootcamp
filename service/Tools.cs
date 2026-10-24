// інструменти (W3): виконуємо tool call, який попросила модель. Окремий клас — щоб юніт-тест
// міг підставити повільне сховище і перевірити timeout (повільного інструмента в стеку немає).

using System.Text.Json;
using System.Text.RegularExpressions;

// джерело статусів замовлень. null — замовлення не знайдено.
public interface IOrderStore
{
    Task<string?> GetStatusAsync(string orderId, CancellationToken ct);
}

// фейкове сховище: детерміновані статуси в коді, без таблиці в БД (жодних змін схеми).
public sealed class FakeOrderStore : IOrderStore
{
    private static readonly Dictionary<string, string> Statuses = new()
    {
        ["123"] = "в дорозі, доставка завтра",
        ["10482"] = "доставлено",
        ["555"] = "очікує оплати",
    };

    public Task<string?> GetStatusAsync(string orderId, CancellationToken ct) =>
        Task.FromResult(Statuses.TryGetValue(orderId, out var s) ? s : null);
}

public enum ToolOutcome { Ok, NotFound, ValidationRejected, Timeout, Failed, NotExecuted }

// результат перевірки одного номера. Status — лише для Ok.
public sealed record OrderLookup(string OrderId, ToolOutcome Outcome, string? Status);

// номери, які перевіряємо (до MaxOrderIds, без дублів, у порядку появи), і скільки різних номерів лишилось поза лімітом
public sealed record OrderIds(IReadOnlyList<string> Ids, int NotChecked);

// Outcome — підсумок по всіх номерах; Orders — кожен номер зі своїм результатом.
// Suffix — те, що дописуємо до відповіді моделі (може бути порожнім).
public sealed record ToolResult(ToolOutcome Outcome, IReadOnlyList<OrderLookup> Orders, int NotChecked, string Suffix)
{
    // для логу: «123=Ok, 555=NotFound». тут лише нормалізовані номери (до 10 цифр), тож довільний рядок у лог не потрапить
    public string Describe() =>
        Orders.Count == 0 ? "none" : string.Join(", ", Orders.Select(o => $"{o.OrderId}={o.Outcome}"));
}

public sealed class ToolRunner
{
    // скільки різних номерів перевіряємо за один виклик інструмента; решту лише згадуємо у відповіді
    public const int MaxOrderIds = 3;
    // довший номер — це не номер: не перевіряємо і назад не повертаємо (ні у відповідь, ні в лог)
    public const int MaxIdDigits = 10;
    // голе число (без #/№) довше за це — не номер замовлення: «мій номер 0671234567» — це телефон,
    // його не перевіряємо і назад не повертаємо (ні у відповідь, ні в лог). довший номер — лише з позначкою.
    public const int MaxBareIdDigits = 6;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // три гілки: позначка (#, ＃ або №) з необов'язковими пробілами і ціле число будь-якої довжини
    // (довжину перевіряє валідатор); довге голе число (понад MaxBareIdDigits цифр) — окрема група long;
    // голе число з 1–MaxBareIdDigits цифр. довге голе число — не кандидат, а лише «ще одне число»:
    // у «Шевченка 123, тел. 0671234567» чисел два, тож перепитуємо, а не беремо будинок як замовлення.
    // самі його цифри далі не йдуть нікуди — ні в перевірку, ні у відповідь, ні в лог.
    // \d тут — будь-які цифри Unicode, а не лише ASCII: «#١٢٣» має знайтись як номер (і бути відкинутим
    // валідатором), а не як «номера немає».
    // межі [\d\p{L}\p{M}] з обох боків: береться ціле число, а не його шматок, і не цифри, приліплені до літер
    // будь-якої абетки, зокрема з комбінованими знаками (розкладене «й» — це «и» + знак): «Купив iPhone15»
    // чи «15шт» — це не замовлення №15. \b тут не годиться: він не відділяє цифри від літер, бо обидва —
    // символи слова.
    private static readonly Regex NumberPattern = new(
        $@"(?:(?<marker>[#＃№]\s*)(?<digits>\d+)|(?<![\d\p{{L}}\p{{M}}])(?:(?<long>\d{{{MaxBareIdDigits + 1},}})|(?<digits>\d{{1,{MaxBareIdDigits}}})))(?![\d\p{{L}}\p{{M}}])",
        RegexOptions.Compiled);

    private readonly IOrderStore _orders;
    private readonly TimeSpan _timeout;
    private long _timeouts;

    // скільки викликів інструмента не вклались у бюджет часу (TOOL_TIMEOUT_MS) з моменту старту процесу —
    // для errors.tool_timeout у /observability (W5). рахуємо виклик, а не номер: три повільні номери — це 1.
    // ОБМЕЖЕННЯ: у пам'яті процесу, як і fallback_events — перезапуск сервісу обнуляє.
    public long Timeouts => Interlocked.Read(ref _timeouts);

    public ToolRunner(IOrderStore orders, TimeSpan timeout)
    {
        _orders = orders;
        // нуль, від'ємний чи завеликий бюджет CancellationTokenSource не прийме і кине виняток —
        // тоді беремо типовий, щоб RunAsync не впав через конфігурацію
        _timeout = timeout > TimeSpan.Zero && timeout.TotalMilliseconds <= int.MaxValue ? timeout : DefaultTimeout;
    }

    // спільний валідатор для обох шляхів (аргументи моделі і повідомлення): лише ASCII 0-9, від 1 до 10 цифр.
    // провідні нулі відкидаємо свідомо (#0123 — це #123), самі нулі — не номер.
    // одна позначка (#, ＃, №) перед цифрами дозволена: модель може передати order_id як «#123».
    public static string? NormalizeOrderId(string? raw)
    {
        var s = StripMarker(raw);
        if (s.Length == 0 || s.Length > MaxIdDigits || !s.All(char.IsAsciiDigit))
            return null;
        s = s.TrimStart('0');
        return s.Length == 0 ? null : s;
    }

    private static string StripMarker(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length > 0 && s[0] is '#' or '＃' or '№')
            s = s[1..].TrimStart();
        return s;
    }

    // номери замовлень: спершу з аргументів моделі (order_id), потім з повідомлення користувача.
    // другий крок потрібен, бо mock завжди шле arguments = "{}".
    // у повідомленні номер — число з позначкою (#123, # 123, ＃123, №123). голе число («замовлення 123») беремо,
    // лише коли воно до MaxBareIdDigits цифр і це єдине голе число в повідомленні, враховуючи й довгі:
    // у «2 товари, замовлення 123» чи «замовлення 123, тел. 0671234567» не вгадуємо, а перепитуємо.
    // довге голе число (телефон) лише рахуємо — номером замовлення воно не стає ніколи.
    // order_id з аргументів підкоряється тому самому правилу довжини: довший за MaxBareIdDigits цифр
    // приймаємо, лише коли користувач сам написав цей номер з позначкою. інакше це, найпевніше, телефон,
    // який модель витягла з тексту, — вважаємо, що аргументу немає (не перевіряємо, не повертаємо, не логуємо).
    public static OrderIds ResolveOrderIds(string? argumentsJson, string? userMessage)
    {
        var marked = new List<string>();
        var bare = new List<string>();
        var longBare = 0;
        foreach (Match m in NumberPattern.Matches(userMessage ?? ""))
        {
            if (m.Groups["long"].Success)
                longBare++;  // цифри довгого числа свідомо не зберігаємо
            else
                (m.Groups["marker"].Success ? marked : bare).Add(m.Groups["digits"].Value);
        }

        var argDigits = StripMarker(RawOrderIdFromArguments(argumentsJson));
        var fromArgs = NormalizeOrderId(argDigits);
        if (fromArgs != null
            && (argDigits.Length <= MaxBareIdDigits || marked.Any(m => NormalizeOrderId(m) == fromArgs)))
            return new OrderIds(new[] { fromArgs }, 0);

        var candidates = marked.Count > 0 ? marked : bare.Count == 1 && longBare == 0 ? bare : new List<string>();
        // HashSet — щоб повідомлення з тисячами номерів не розбиралось за квадратичний час
        var seen = new HashSet<string>();
        var ids = new List<string>(MaxOrderIds);
        foreach (var c in candidates)
            if (NormalizeOrderId(c) is { } id && seen.Add(id) && ids.Count < MaxOrderIds)  // #123 і #0123 — один номер
                ids.Add(id);
        return new OrderIds(ids, Math.Max(0, seen.Count - MaxOrderIds));
    }

    // «№» — номер замовлення лише після «замовлення / order»: інакше це «відділення №12» Нової пошти
    private static readonly Regex OrderNumeroPattern = new(
        @"\b(?:замовлен\w*|orders?)\s*№\s*(?<digits>\d+)(?![\d\p{L}\p{M}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // номери для ключа дедуплікації HITL, без повторів, відсортовані: «#123» (або «＃123») і «замовлення №123».
    // голе число тут не вгадуємо: для незворотної дії помилка дорожча за «не знайдено» — «чекаю 3 дні» і
    // «посилка йшла 3 тижні» від різних клієнтів злилися б в одну заявку. «№» без «замовлення» теж не номер:
    // «у відділенні №12» від двох клієнтів злило б їхні заявки (для lookup_order «№12» лишається номером)
    public static IReadOnlyList<string> MarkedOrderIds(string? text)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in NumberPattern.Matches(text ?? ""))
            if (m.Groups["marker"].Success && m.Groups["marker"].Value[0] != '№'
                && NormalizeOrderId(m.Groups["digits"].Value) is { } id)
                ids.Add(id);
        foreach (Match m in OrderNumeroPattern.Matches(text ?? ""))
            if (NormalizeOrderId(m.Groups["digits"].Value) is { } id)
                ids.Add(id);
        return ids.ToList();
    }

    private static string? RawOrderIdFromArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("order_id", out var v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;  // зіпсовані аргументи — це "аргументу немає", а не падіння запиту
        }
    }

    // виконує інструмент і ніколи не кидає виняток у /chat.
    // create_ticket НЕ виконуємо: це незворотна дія, її черга підтверджень (HITL) — W4.
    public async Task<ToolResult> RunAsync(string toolName, string? argumentsJson, string userMessage)
    {
        try
        {
            return await RunCoreAsync(toolName, argumentsJson, userMessage);
        }
        catch (Exception)
        {
            return new ToolResult(ToolOutcome.Failed, Array.Empty<OrderLookup>(), 0,
                " Не вдалося перевірити статус замовлення, спробуйте, будь ласка, пізніше.");
        }
    }

    private async Task<ToolResult> RunCoreAsync(string toolName, string? argumentsJson, string userMessage)
    {
        if (toolName != "lookup_order")
            return new ToolResult(ToolOutcome.NotExecuted, Array.Empty<OrderLookup>(), 0, "");

        // валідація: без номера замовлення інструмент не викликаємо і статус не вигадуємо
        var ids = ResolveOrderIds(argumentsJson, userMessage);
        if (ids.Ids.Count == 0)
            return new ToolResult(ToolOutcome.ValidationRejected, Array.Empty<OrderLookup>(), 0,
                " Вкажіть, будь ласка, номер замовлення (наприклад, #123 або №123).");

        // ОДИН бюджет часу на весь виклик інструмента, а не на кожен номер: номери перевіряємо паралельно
        // під спільним токеном, тож три номери не займуть утричі більше часу. хто не встиг — Timeout саме для нього.
        using var cts = new CancellationTokenSource(_timeout);
        var orders = await Task.WhenAll(ids.Ids.Select(id => LookupAsync(id, cts.Token)));

        var outcome =
            orders.Any(o => o.Outcome == ToolOutcome.Timeout) ? ToolOutcome.Timeout
            : orders.Any(o => o.Outcome == ToolOutcome.Failed) ? ToolOutcome.Failed
            : orders.Any(o => o.Outcome == ToolOutcome.Ok) ? ToolOutcome.Ok
            : ToolOutcome.NotFound;
        if (outcome == ToolOutcome.Timeout)
            Interlocked.Increment(ref _timeouts);
        return new ToolResult(outcome, orders, ids.NotChecked, Suffix(orders, ids.NotChecked));
    }

    // ніколи не кидає. WaitAsync спрацює, навіть якщо сховище ігнорує токен скасування.
    private async Task<OrderLookup> LookupAsync(string id, CancellationToken ct)
    {
        try
        {
            var status = await _orders.GetStatusAsync(id, ct).WaitAsync(ct);
            return new OrderLookup(id, status == null ? ToolOutcome.NotFound : ToolOutcome.Ok, status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new OrderLookup(id, ToolOutcome.Timeout, null);
        }
        catch (Exception)
        {
            return new OrderLookup(id, ToolOutcome.Failed, null);
        }
    }

    // один номер — форма « (статус: …)», як і раніше. кілька — « (статуси: #555 — …; #123 — …)»:
    // крапка з комою, бо в самому статусі буває кома («в дорозі, доставка завтра»).
    private static string Suffix(IReadOnlyList<OrderLookup> orders, int notChecked)
    {
        string text;
        if (orders.Count == 1)
        {
            var o = orders[0];
            text = o.Outcome switch
            {
                ToolOutcome.Ok => $" (статус: {o.Status})",
                ToolOutcome.NotFound => $" (статус: замовлення #{o.OrderId} не знайдено)",
                ToolOutcome.Timeout => " Не вдалося перевірити статус замовлення вчасно, спробуйте, будь ласка, пізніше.",
                _ => " Не вдалося перевірити статус замовлення, спробуйте, будь ласка, пізніше."
            };
        }
        else
        {
            text = " (статуси: " + string.Join("; ", orders.Select(o => $"#{o.OrderId} — " + (o.Outcome switch
            {
                ToolOutcome.Ok => o.Status,
                ToolOutcome.NotFound => "не знайдено",
                ToolOutcome.Timeout => "не вдалося перевірити вчасно",
                _ => "не вдалося перевірити"
            }))) + ")";
            if (orders.Any(o => o.Outcome is ToolOutcome.Timeout or ToolOutcome.Failed))
                text += " Замовлення, які не вдалося перевірити, спробуйте перевірити пізніше, будь ласка.";
        }

        // номери понад ліміт не перелічуємо: їх може бути скільки завгодно, у відповідь іде лише кількість
        if (notChecked > 0)
            text += $" Інші номери замовлень (ще {notChecked}) не перевірено: за один запит перевіряю щонайбільше {MaxOrderIds}.";
        return text;
    }
}
