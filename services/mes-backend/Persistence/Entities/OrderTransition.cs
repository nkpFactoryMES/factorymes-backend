namespace FactoryMES.Backend.Persistence.Entities;

// 작업 상태 이력: 업무 명령의 이전·이후 상태와 수행자를 감사 목적으로 보존한다.
public sealed class OrderTransition
{
    public long TransitionId { get; set; }

    public long OrderId { get; set; }

    public string FromStatus { get; set; } = string.Empty;

    public string ToStatus { get; set; } = string.Empty;

    public long? ActorId { get; set; }

    public string Principal { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
