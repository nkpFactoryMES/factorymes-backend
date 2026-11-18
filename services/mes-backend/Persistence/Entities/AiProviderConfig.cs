namespace FactoryMES.Backend.Persistence.Entities;

// AI 제공자 설정: Mock/로컬/클라우드 선택을 분리하며 실제 키는 외부 비밀 저장소 참조로 다룬다.
public sealed class AiProviderConfig
{
    public long TargetId { get; set; }

    public string ProviderKind { get; set; } = string.Empty;

    public string? ApprovedEndpoint { get; set; }

    public string? Model { get; set; }

    public string? CredentialRef { get; set; }

    public long TimeoutMs { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }

    public string Kind { get; set; } = string.Empty;
}
