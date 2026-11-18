namespace FactoryMES.Backend.Persistence.Entities;

// 관리자 정정 요청: 제출/마감 전 원장을 지우지 않고 허용된 정정 절차를 추적한다.
public sealed class CorrectionRequest
{
    public long CorrectionId { get; set; }

    public long OrderId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public long RequestedBy { get; set; }

    public long? ResolvedBy { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public string? ResolutionNote { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
