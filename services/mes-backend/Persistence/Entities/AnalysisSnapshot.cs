namespace FactoryMES.Backend.Persistence.Entities;

// 분석 입력 스냅샷: 시계열 사전 집계에서 만든 고정 입력을 재시도 때 재사용한다.
public sealed class AnalysisSnapshot
{
    public long SnapshotId { get; set; }

    public long EquipmentId { get; set; }

    public DateTime PeriodStartUtc { get; set; }

    public DateTime PeriodEndUtc { get; set; }

    public long InputVersion { get; set; }

    public decimal Coverage { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string InputHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
