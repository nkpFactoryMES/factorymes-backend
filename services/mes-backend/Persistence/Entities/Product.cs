namespace FactoryMES.Backend.Persistence.Entities;

// 품목 기준정보: 관리자가 등록하며 작업지시와 일별 생산 집계가 참조한다.
public sealed class Product
{
    public long ProductId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
