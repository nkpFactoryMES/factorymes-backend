namespace FactoryMES.Backend.Persistence.Entities;

// 작업 중지 구간: 중지·재개 명령과 사유를 보존하며 AUTO 중지 구간의 수량은 제외한다.
public sealed class WorkPause
{
    public long PauseId { get; set; }

    public long OrderId { get; set; }

    public long? AlarmId { get; set; }

    public string ReasonCode { get; set; } = string.Empty;

    public string? Note { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public long RecordedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long EquipmentId { get; set; }
}
