namespace FactoryMES.Backend.Persistence.Entities;

// 일별 생산 집계: 마감된 MANUAL/AUTO 순원장을 KST 기준으로 재계산한 조회용 데이터다.
public sealed class DailyProduction
{
    public DateTime BusinessDate { get; set; }

    public long EquipmentId { get; set; }

    public long ProductId { get; set; }

    public long TotalQty { get; set; }

    public long RejectQty { get; set; }

    public DateTime RefreshedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
