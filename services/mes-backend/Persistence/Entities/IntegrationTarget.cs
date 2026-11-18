namespace FactoryMES.Backend.Persistence.Entities;

// 연동 대상: 관리자가 등록한 설비/AI 설정의 식별자·활성 상태·설정 버전을 관리한다.
public sealed class IntegrationTarget
{
    public long TargetId { get; set; }

    public string TargetCode { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ConnectorType { get; set; } = string.Empty;

    public long? EquipmentId { get; set; }

    public bool Enabled { get; set; } = true;

    public bool Deleted { get; set; } = false;

    public long ConfigVersion { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
