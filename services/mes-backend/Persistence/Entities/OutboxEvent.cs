namespace FactoryMES.Backend.Persistence.Entities;

// 트랜잭션 발송 대기열: 업무 변경과 함께 저장한 이벤트를 전달기가 재시도 가능하게 처리한다.
public sealed class OutboxEvent
{
    public long OutboxId { get; set; }

    public string EventId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string AggregateType { get; set; } = string.Empty;

    public string AggregateId { get; set; } = string.Empty;

    public long AggregateVersion { get; set; }

    public string Payload { get; set; } = string.Empty;

    public long Attempts { get; set; } = 0;

    public DateTime? NextAttemptAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
