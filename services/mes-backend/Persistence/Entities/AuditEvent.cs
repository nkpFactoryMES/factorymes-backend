namespace FactoryMES.Backend.Persistence.Entities;

// 감사 이벤트: 누가 어떤 업무 엔티티를 변경했는지 DB에 보존한다. 사유 원문을 운영 로그에 복제하지 않는다.
public sealed class AuditEvent
{
    public long AuditId { get; set; }

    public long? ActorId { get; set; }

    public string Principal { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public string Detail { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
