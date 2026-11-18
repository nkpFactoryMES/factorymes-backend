namespace FactoryMES.Backend.Persistence.Entities;

// 작업지시: 관리자가 발행하고 담당 작업자가 수행한다. AUTO 수량은 수집 원장에서 계산한다.
public sealed class WorkOrder
{
    public long OrderId { get; set; }

    public string OrderNo { get; set; } = string.Empty;

    public long ProductId { get; set; }

    public long EquipmentId { get; set; }

    public long AssigneeId { get; set; }

    public string? OperationLabel { get; set; }

    public long TargetQty { get; set; }

    public string CountMode { get; set; } = string.Empty;

    public long? CounterMappingId { get; set; }

    public DateTime PlannedStartUtc { get; set; }

    public DateTime PlannedEndUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public long CreatedBy { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    public string? CancelReason { get; set; }

    public string? CompletionReason { get; set; }

    public decimal? StartCounter { get; set; }

    public decimal? EndCounter { get; set; }

    public DateTime? StartSampleAtUtc { get; set; }

    public DateTime? EndSampleAtUtc { get; set; }

    public DateTime? StartReceivedAtUtc { get; set; }

    public DateTime? EndReceivedAtUtc { get; set; }

    public bool CounterInvalid { get; set; } = false;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
