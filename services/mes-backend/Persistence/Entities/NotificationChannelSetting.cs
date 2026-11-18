namespace FactoryMES.Backend.Persistence.Entities;

// 사용자·업무 이벤트별 채널 설정. 주소 대신 OFF/MOCK/LOCAL 선택과 수정 버전만 저장한다.
public sealed class NotificationChannelSetting
{
    public long UserId { get; set; }

    public string EventKind { get; set; } = string.Empty;

    public string EmailMode { get; set; } = "OFF";

    public string ChatMode { get; set; } = "OFF";

    public long Version { get; set; } = 1;

    public long UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
