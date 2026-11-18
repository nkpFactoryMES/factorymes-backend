namespace FactoryMES.Backend.Persistence.Entities;

// AI 분석 작업: 예약 식별자·상태·재시도 시각을 보존하여 재시작 후 누락·중복을 제어한다.
public sealed class IntegrationJob
{
    public string JobId { get; set; } = string.Empty;

    public string RequestId { get; set; } = string.Empty;

    public long TargetId { get; set; }

    public string Operation { get; set; } = string.Empty;

    public long? ActorId { get; set; }

    public string Principal { get; set; } = string.Empty;

    public long? SnapshotId { get; set; }

    public long ConfigVersion { get; set; }

    public long Attempt { get; set; } = 1;

    public string State { get; set; } = string.Empty;

    public DateTime DeadlineUtc { get; set; }

    public DateTime? RetryAtUtc { get; set; }

    public string? Error { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
