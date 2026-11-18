namespace FactoryMES.Backend.Persistence.Entities;

// 공통 알림 본문: 경보·업무 이벤트가 생성하고 사용자별 수신 상태와 분리한다.
public sealed class Notification
{
    public long NotificationId { get; set; }

    public string EventId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public long? AlarmId { get; set; }

    public long? OrderId { get; set; }

    public long? AnalysisResultId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
