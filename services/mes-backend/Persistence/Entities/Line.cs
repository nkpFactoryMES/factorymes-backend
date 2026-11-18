namespace FactoryMES.Backend.Persistence.Entities;

// 생산 라인 기준정보: 설비를 묶고 라인별 실적·경보 조회 범위를 제공한다.
public sealed class Line
{
    public long LineId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
