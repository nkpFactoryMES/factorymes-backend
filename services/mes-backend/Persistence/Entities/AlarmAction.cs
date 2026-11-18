namespace FactoryMES.Backend.Persistence.Entities;

// 경보 조치: 담당자의 처리 내용과 진행 상태를 원래 경보에 연결한다.
public sealed class AlarmAction
{
    public long ActionId { get; set; }

    public long AlarmId { get; set; }

    public long OwnerId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string? Result { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
