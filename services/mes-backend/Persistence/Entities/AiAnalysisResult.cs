namespace FactoryMES.Backend.Persistence.Entities;

// AI 분석 결과: 분석 작업에 연결한 예측/위험도와 제공자 결과를 보존한다.
public sealed class AiAnalysisResult
{
    public long ResultId { get; set; }

    public string JobId { get; set; } = string.Empty;

    public long Attempt { get; set; } = 1;

    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    public DateTime InputPeriodStartUtc { get; set; }

    public DateTime InputPeriodEndUtc { get; set; }

    public decimal DataCoverage { get; set; }

    public long ResultVersion { get; set; }

    public string? Risk { get; set; }

    public string Evidence { get; set; } = string.Empty;

    public bool MockFlag { get; set; } = false;

    public bool Accepted { get; set; } = false;

    public DateTime CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
