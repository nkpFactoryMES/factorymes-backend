namespace FactoryMES.Backend.Persistence.Entities;

// 정규화 측정 지표: 매핑별 값·품질을 저장하여 임계치 판정과 AI 집계에 제공한다.
public sealed class TelemetryMetric
{
    public long TelemetryId { get; set; }

    public string MetricCode { get; set; } = string.Empty;

    public long MappingId { get; set; }

    public decimal? NumericValue { get; set; }

    public string? TextValue { get; set; }

    public string? Unit { get; set; }

    public string Quality { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
