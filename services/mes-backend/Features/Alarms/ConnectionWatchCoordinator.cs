namespace FactoryMES.Backend.Features.Alarms;

// Single-host fairness/overlap guard. Oracle row locks also serialize multiple callers.
// 프로세스 안의 배치 동시 실행을 막고 마지막 규칙 ID 다음부터 순환 감시한다.
public sealed class ConnectionWatchCoordinator
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public long AfterRuleId { get; set; }
}



