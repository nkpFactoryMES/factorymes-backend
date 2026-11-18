namespace FactoryMES.Backend.Persistence.Entities;

// 발생 경보: 수집·감시가 생성하며 ACK·복구·조치·종결을 구분해 추적한다.
public sealed class Alarm
{
    public long AlarmId { get; set; }

    public long RuleId { get; set; }

    public long EquipmentId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public long RuleVersion { get; set; }

    public decimal? TriggerValue { get; set; }

    public DateTime? TriggerSampleAtUtc { get; set; }

    public string RuleSnapshot { get; set; } = string.Empty;

    public DateTime OpenedAtUtc { get; set; }

    public DateTime? RecoveredAtUtc { get; set; }

    public long? AckBy { get; set; }

    public DateTime? AckAtUtc { get; set; }

    public string? CloseKind { get; set; }

    public long? ClosedBy { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public string? CloseReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
