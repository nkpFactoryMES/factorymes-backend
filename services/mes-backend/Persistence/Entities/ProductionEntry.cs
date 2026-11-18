namespace FactoryMES.Backend.Persistence.Entities;

// 생산 수량 원장: 수동 입력·자동 증분·역분개를 보존하고 TOTAL/REJECT 순합으로 집계한다.
public sealed class ProductionEntry
{
    public long EntryId { get; set; }

    public string ClientEntryId { get; set; } = string.Empty;

    public long OrderId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public long Qty { get; set; }

    public long? ReversalOf { get; set; }

    public long? CorrectionId { get; set; }

    public string? Reason { get; set; }

    public long RecordedBy { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    // MANUAL은 사용자 입력, AUTO는 설비 누적 카운터의 확정 증분이다.
    public string Source { get; set; } = "MANUAL";

    // 자동 실적이 어느 원시 수집 이벤트에서 나왔는지 추적한다. 수동 실적은 null이다.
    public long? TelemetryId { get; set; }

    public string RequestHash { get; set; } = string.Empty;
}
