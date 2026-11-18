namespace FactoryMES.Backend.Persistence.Entities;

// 설비 기준정보: 작업지시와 수집 대상의 공통 식별자이며 작업 전환 잠금의 기준이다.
public sealed class Equipment
{
    public long EquipmentId { get; set; }

    public long LineId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
