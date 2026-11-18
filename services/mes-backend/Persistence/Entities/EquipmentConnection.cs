namespace FactoryMES.Backend.Persistence.Entities;

// 설비 접속 설정: 관리자 입력을 게이트웨이 설정 캐시에 투영하고 수집기가 HTTP로 읽는다.
public sealed class EquipmentConnection
{
    public long TargetId { get; set; }

    public string Protocol { get; set; } = string.Empty;

    public string ApprovedEndpoint { get; set; } = string.Empty;

    public long PollIntervalMs { get; set; }

    public long TimeoutMs { get; set; }

    public long StaleSec { get; set; }

    public string? CredentialRef { get; set; }

    public DateTime? LastValidSampleAtUtc { get; set; }

    public DateTime? LastValidReceivedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }

    public string Kind { get; set; } = string.Empty;
}
