namespace FactoryMES.Backend.Persistence.Entities;

// AUTO 작업 한 건의 소비 체크포인트. 첫 측정은 기준값이며 이후 새 측정의 양수 증분만 원장으로 보낸다.
// 수집 저장과 같은 Oracle 트랜잭션에서 갱신하므로 재시작·이벤트 재전송으로 총량이 다시 늘지 않는다.
public sealed class AutoCounterState
{
    public long OrderId { get; set; }

    public long? LastTelemetryId { get; set; }

    public string? LastStreamId { get; set; }

    public long? LastSequenceNo { get; set; }

    public decimal? LastCounter { get; set; }

    public DateTime? LastSampleAtUtc { get; set; }

    public DateTime? LastReceivedAtUtc { get; set; }

    public DateTime AcceptAfterUtc { get; set; }

    public bool AwaitingBaseline { get; set; } = true;

    public long ConfigVersion { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
