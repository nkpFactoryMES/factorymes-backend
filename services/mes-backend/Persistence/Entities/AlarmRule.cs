namespace FactoryMES.Backend.Persistence.Entities;

// 경보 규칙: 관리자가 정한 통신 유실/HIGH 임계치와 유지시간을 수집·감시에서 평가한다.
public sealed class AlarmRule
{
    public long RuleId { get; set; }

    public long EquipmentId { get; set; }

    public long TargetId { get; set; }

    public long? MappingId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public decimal? TriggerHigh { get; set; }

    public decimal? ClearBelow { get; set; }

    public long? HoldSec { get; set; }

    public long? ClearHoldSec { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? SuppressUntilUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
