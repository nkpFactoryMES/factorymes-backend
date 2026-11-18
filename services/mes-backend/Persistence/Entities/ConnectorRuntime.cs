namespace FactoryMES.Backend.Persistence.Entities;

// 커넥터 실행 상태: heartbeat·마지막 성공·실패 상태를 관리 화면과 통신 경보에서 읽는다.
public sealed class ConnectorRuntime
{
    public long TargetId { get; set; }

    public string InstanceId { get; set; } = string.Empty;

    public long AppliedConfigVersion { get; set; }

    public string ConnectionState { get; set; } = string.Empty;

    public string? OperatingState { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
