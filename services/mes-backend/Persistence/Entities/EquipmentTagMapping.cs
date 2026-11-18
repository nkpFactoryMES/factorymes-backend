namespace FactoryMES.Backend.Persistence.Entities;

// 원본 필드 매핑: 수집 JSON 경로를 표준 지표·타입·단위로 바꾼다.
public sealed class EquipmentTagMapping
{
    public long MappingId { get; set; }

    public long TargetId { get; set; }

    public string MetricCode { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string DataType { get; set; } = string.Empty;

    public string ValueKind { get; set; } = string.Empty;

    public string? Unit { get; set; }

    public decimal Scale { get; set; }

    public decimal OffsetValue { get; set; }

    public bool Enabled { get; set; } = true;

    public decimal? LatestValue { get; set; }

    public DateTime? LatestSampleAtUtc { get; set; }

    public DateTime? LatestReceivedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
