// circuit breaker (W4, опційно): окремий запобіжник на кожну модель ланцюжка.
// без Polly свідомо: його готові пайплайни вмикають retry, а повторів у нас немає (num_retries: 0 у gateway).
// Closed -> Open після FailureThreshold збоїв поспіль -> один пробний запит (HalfOpen) -> Closed лише після вдалої проби.
// без half-open модель, що відкрилась, ніколи б не повернулась у роботу — сервіс сам собі влаштував би outage.
// ОБМЕЖЕННЯ: стан живе в пам'яті процесу, як і лічильник fallback — перезапуск сервісу закриває всі запобіжники.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

public enum CircuitState { Closed, Open, HalfOpen }

// Allow — звичайний виклик; Probe — цей запит і є пробою (єдиний); Skip — модель відкрита, її пропускаємо
public enum CircuitDecision { Allow, Probe, Skip }

// Neutral — 4xx (400/401/404...): помилка конфігурації, а не стан моделі — ні збій, ні успіх
public enum CircuitOutcome { Success, Failure, Neutral }

// ProbeInterval — через скільки після відкриття (чи після невдалої проби) пускаємо пробу;
// OpenDuration — верхня межа: після неї проба дозволена завжди. Закриває лише вдала проба, а не таймер.
public sealed record CircuitOptions(int FailureThreshold, TimeSpan OpenDuration, TimeSpan ProbeInterval)
{
    public static readonly CircuitOptions Default = new(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

    // через скільки після відкриття можна пробувати: раніша з двох меж
    public TimeSpan ProbeAfter => ProbeInterval < OpenDuration ? ProbeInterval : OpenDuration;
}

public sealed class CircuitBreaker
{
    private readonly string _model;
    private readonly CircuitOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    // переходи крихітні й рідкісні, тож звичайний lock: простіше за CAS і так само гарантує одну пробу
    private readonly object _gate = new();
    private CircuitState _state = CircuitState.Closed;
    private int _failures;
    private DateTimeOffset _openedAt;
    // покоління: +1 на кожен перехід в Open. Запит запам'ятовує його при допуску, і результат
    // з минулого покоління (допущений до відкриття) стан уже не чіпає — навіть коли проба встигла закрити
    private long _generation;

    public CircuitBreaker(string model, CircuitOptions options, TimeProvider time, ILogger? log = null)
    {
        _model = model;
        _options = options;
        _time = time;
        _log = log;
    }

    public CircuitState State { get { lock (_gate) return _state; } }

    // чи можна зараз іти в модель. Probe віддається рівно одному запиту: стан одразу стає HalfOpen,
    // і всі інші, поки проба в дорозі, отримують Skip. Хто отримав Allow чи Probe — мусить викликати Record()
    // з тим самим generation.
    public CircuitDecision TryAcquire(out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            switch (_state)
            {
                case CircuitState.Closed:
                    return CircuitDecision.Allow;
                case CircuitState.Open when _time.GetUtcNow() - _openedAt >= _options.ProbeAfter:
                    _state = CircuitState.HalfOpen;
                    _log?.LogInformation("circuit {Model} half-open probe", _model);
                    return CircuitDecision.Probe;
                default:
                    return CircuitDecision.Skip;
            }
        }
    }

    // результат виклику. ModelClient кличе це у finally, тож слот проби звільняється на кожному шляху:
    // timeout (status 0), виняток, 4xx — HalfOpen, що ніколи не завершиться, неможливий.
    // status — лише для логу (останній статус спроби)
    public void Record(CircuitOutcome outcome, bool wasProbe, long generation, int status = 0)
    {
        lock (_gate)
        {
            if (wasProbe)
            {
                if (outcome == CircuitOutcome.Success)
                {
                    _state = CircuitState.Closed;
                    _failures = 0;
                    _log?.LogInformation("circuit {Model} closed after successful probe", _model);
                }
                else
                {
                    // невдала чи нейтральна проба: знову Open, обидва таймери від цього моменту
                    _state = CircuitState.Open;
                    _openedAt = _time.GetUtcNow();
                    _generation++;
                    _log?.LogWarning("circuit {Model} probe {Result} ({Status}), stays open", _model,
                        outcome == CircuitOutcome.Failure ? "failed" : "inconclusive", status);
                }
                return;
            }

            // запит, пущений ще в Closed, міг завершитись, коли запобіжник уже відкрили інші, — а то й після того,
            // як проба знову закрила: його результат стан не змінює (інше покоління). Закрити може лише проба,
            // а відкрити — лише збої, допущені в поточному Closed: запізнілі timeout'и не відкриють здорову модель
            if (_state != CircuitState.Closed || generation != _generation)
                return;
            if (outcome == CircuitOutcome.Success)
                _failures = 0;
            else if (outcome == CircuitOutcome.Failure && ++_failures >= _options.FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = _time.GetUtcNow();
                _generation++;
                _log?.LogWarning("circuit {Model} opened after {Failures} consecutive failures (last status {Status})",
                    _model, _failures, status);
                _failures = 0;
            }
        }
    }
}

// один запобіжник на аліас моделі. known — моделі, які /observability показує одразу, ще до першого запиту.
public sealed class CircuitRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new();
    private readonly CircuitOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;

    public CircuitRegistry(CircuitOptions options, TimeProvider time, ILogger? log = null, IEnumerable<string>? known = null)
    {
        _options = options;
        _time = time;
        _log = log;
        foreach (var model in known ?? Enumerable.Empty<string>())
            For(model);
    }

    public CircuitOptions Options => _options;

    public CircuitBreaker For(string model) =>
        _breakers.GetOrAdd(model, m => new CircuitBreaker(m, _options, _time, _log));

    // для /observability: { "mock-mini": "closed" | "open" | "half_open", ... }
    public SortedDictionary<string, string> Snapshot() =>
        new(_breakers.ToDictionary(kv => kv.Key, kv => kv.Value.State switch
        {
            CircuitState.Open => "open",
            CircuitState.HalfOpen => "half_open",
            _ => "closed"
        }), StringComparer.Ordinal);
}
