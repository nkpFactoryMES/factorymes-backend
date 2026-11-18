namespace FactoryMES.Backend.Persistence.Entities;

// 작업 전 점검: 담당 작업자가 체크한 안전·준비 항목을 시작 조건으로 사용한다.
public sealed class WorkCheckItem
{
    public long CheckId { get; set; }

    public long OrderId { get; set; }

    public long Seq { get; set; }

    public string Label { get; set; } = string.Empty;

    public bool Required { get; set; } = true;

    public bool Checked { get; set; } = false;

    public long? CheckedBy { get; set; }

    public DateTime? CheckedAtUtc { get; set; }

    public string? CommentText { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
