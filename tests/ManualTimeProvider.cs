// годинник, який рухає лише тест: TTL заявок HITL, проба circuit breaker і статус провайдерів — без sleep.
// один на всі тести (ApprovalQueueTests, CircuitBreakerTests, ObservabilityTests). дата старту ні на що не впливає
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
