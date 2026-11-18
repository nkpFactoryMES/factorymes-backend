namespace FactoryMES.Backend.Persistence.Entities;

// 알림 수신함: 수신 사용자별 읽음 상태를 관리하며 타인 알림 조회를 차단한다.
public sealed class NotificationRecipient
{
    public long NotificationId { get; set; }

    public long UserId { get; set; }

    public DateTime? ReadAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
