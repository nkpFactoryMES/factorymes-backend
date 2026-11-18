namespace FactoryMES.Backend.Persistence.Entities;

// 배치 실행 이력: Quartz 작업의 실행 구간·처리 건수·성공 여부를 운영 화면에 제공한다.
public sealed class JobRun
{
    public long JobRunId { get; set; }

    public string JobKey { get; set; } = string.Empty;

    public DateTime ScheduledForUtc { get; set; }

    public long Attempt { get; set; } = 1;

    public string Status { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public long? AffectedRows { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
