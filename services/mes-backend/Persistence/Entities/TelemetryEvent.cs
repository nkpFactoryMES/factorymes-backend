namespace FactoryMES.Backend.Persistence.Entities;

// 원시 수집 이벤트: 서명된 수집 배치를 멱등 저장하고 AUTO 생산과 경보의 근거로 사용한다.
public sealed class TelemetryEvent
{
    public long TelemetryId { get; set; }

    public string EventId { get; set; } = string.Empty;

    public long EquipmentId { get; set; }

    public long TargetId { get; set; }

    public string StreamId { get; set; } = string.Empty;

    public long SequenceNo { get; set; }

    public long SchemaVersion { get; set; }

    public long ConfigVersion { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
