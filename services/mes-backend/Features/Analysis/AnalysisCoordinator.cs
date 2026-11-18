namespace FactoryMES.Backend.Features.Analysis;

// 한 프로세스 안의 중복 배치 진입을 제한한다. 재시작·다중 프로세스의 정합성은 DB 잠금과 상태 검증이 담당한다.
public sealed class AnalysisCoordinator
{
    public SemaphoreSlim DispatchGate { get; } = new(1, 1);

    public SemaphoreSlim ScheduleGate { get; } = new(1, 1);

    public SemaphoreSlim Gate { get; } = new(1, 1);
}
