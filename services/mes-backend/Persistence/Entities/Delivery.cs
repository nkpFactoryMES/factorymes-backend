namespace FactoryMES.Backend.Persistence.Entities;

// 채널별 발송 이력: Outbox 전달 결과·Mock 이메일/채팅 응답·재시도 횟수를 보존한다.
public sealed class Delivery
{
    public long DeliveryId { get; set; }

    public long NotificationId { get; set; }

    public long RecipientId { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string DestinationKey { get; set; } = string.Empty;

    public string TargetRef { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public long Attempts { get; set; } = 0;

    public DateTime? NextAttemptAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
